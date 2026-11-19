using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using PicHarbor.Core.GooglePhotos;

namespace PicHarbor.Core.Remote;

/// <summary>Serialized, private-runtime host for publisher-signed remote tools.</summary>
public sealed class RemoteRuntime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string directory;
    /// <summary>Uses the normal service directory, or an isolated local directory for a host.</summary>
    public RemoteRuntime(string? dataDirectory = null) => directory = dataDirectory ?? Home;
    /// <summary>Local data directory, separate from Google credentials and libraries.</summary>
    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicHarbor", "Services");
    /// <summary>Runs a bounded request; neither request data nor errors are written to a shell command.</summary>
    public async Task<JsonObject> CallAsync(string action, JsonObject? payload = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            string runner = Path.Combine(directory, "harbor_remote.py");
            await using (Stream source = typeof(RemoteRuntime).Assembly.GetManifestResourceStream("harbor_remote.py")!)
            {
                using var data = new MemoryStream();
                await source.CopyToAsync(data, ct);
                byte[] bytes = data.ToArray();
                if (!File.Exists(runner) || !File.ReadAllBytes(runner).AsSpan().SequenceEqual(bytes))
                    await File.WriteAllBytesAsync(runner, bytes, ct);
            }
            payload ??= new JsonObject();
            payload["context"] = new JsonObject { ["platform"] = "windows", ["version"] = typeof(RemoteRuntime).Assembly.GetName().Version?.ToString() ?? "unknown", ["language"] = System.Globalization.CultureInfo.CurrentUICulture.Name };
            string interpreter = await Task.Run(GooglePhotosSyncEngine.LocatePythonExecutable, ct);
            var start = new ProcessStartInfo(interpreter)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add("-X"); start.ArgumentList.Add("utf8"); start.ArgumentList.Add(runner);
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            using var process = Process.Start(start) ?? throw new IOException("REMOTE_START_FAILED");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardInput.WriteAsync(new JsonObject { ["home"] = directory, ["action"] = action, ["payload"] = payload }.ToJsonString());
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                string text = await output;
                _ = await errors; // Deliberately do not log publisher exceptions or local request values.
                if (text.Length > 524288) throw new IOException("REMOTE_LIMIT");
                return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
            }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { return new JsonObject { ["ok"] = false, ["error"] = "REMOTE_UNAVAILABLE" }; }
        finally { gate.Release(); }
    }
}
