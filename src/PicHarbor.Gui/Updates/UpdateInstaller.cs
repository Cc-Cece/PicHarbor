using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using PicHarbor.Core.Updates;
using PicHarbor.Gui.Util;
using PicHarbor.Vault;

namespace PicHarbor.Gui.Updates;

internal sealed record InstallRequest(int ParentId, long ParentStart, string Target, string Candidate, string Tag,
    bool Restart, bool Elevated, string JobDirectory, string? ProxyAddress);
internal sealed record InstallResult(string Target, string Backup, string NewHash, bool Success, string Error, string? Candidate = null);

/// <summary>Two-process installer: an unelevated relay owns relaunch; a worker owns the file/service transaction.</summary>
internal static class UpdateInstaller
{
    public static readonly string CacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicHarbor", "Updates");

    // IncludeAllContentForSelfExtract gives managed assemblies a Location in the extraction folder.
    // Compare against the actual apphost directory; compatibility extraction can also change BaseDirectory.
    public static bool IsPackaged => !Debugger.IsAttached &&
        (string.IsNullOrEmpty(typeof(App).Assembly.Location) ||
         !string.Equals(Path.GetDirectoryName(typeof(App).Assembly.Location),
             Path.GetDirectoryName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
#if DEBUG
        && false
#endif
        ;

    public static bool RequiresElevation(string target)
    {
        if (ServiceInstaller.IsInstalled()) return true;
        try
        {
            RejectLinks(target);
            // Do not open a running image for writing: Windows rejects that even for an administrator.
            // Replacement needs DELETE on the old file and creation rights in its directory.
            using var image = CreateFile(target, 0x00010000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (image.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 5) return true;
                throw new IOException(new Win32Exception(error).Message);
            }
            string probe = Path.Combine(Path.GetDirectoryName(target)!, ".picharbor-write-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static async Task<bool> LaunchAsync(string candidate, string tag, bool restart, bool elevated)
    {
        string target = Environment.ProcessPath!;
        string job = Path.Combine(CacheRoot, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        string helper = Path.Combine(job, "Updater.exe");
        File.Copy(target, helper);
        using var parent = Process.GetCurrentProcess();
        var request = new InstallRequest(parent.Id, parent.StartTime.ToUniversalTime().Ticks, target, candidate, tag, restart, elevated, job,
            Config.AppSettings.Load().GooglePhotosProxy);
        using var server = CreatePipe(out string pipe);
        using var child = Process.Start(StartInfo(helper, "--update-relay", parent.Id, pipe, false))
            ?? throw new IOException("Unable to start updater.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await server.WaitForConnectionAsync(timeout.Token);
        VerifyPeer(server, child.Id, client: true);
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        string? ready = await reader.ReadLineAsync(timeout.Token);
        if (ready != "READY") throw new IOException(ready ?? "Updater disconnected.");
        await writer.WriteLineAsync("COMMIT");
        return true;
    }

    public static int Run(string[] args, bool relay)
    {
        try { return RunAsync(args, relay).GetAwaiter().GetResult(); }
        catch (Exception ex) { Debug.WriteLine(ex); return 1; }
    }

    private static async Task<int> RunAsync(string[] args, bool relay)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out int launcherId)) return 1;
        using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await pipe.ConnectAsync(timeout.Token);
        VerifyPeer(pipe, launcherId, client: false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var request = JsonSerializer.Deserialize<InstallRequest>(await reader.ReadLineAsync(timeout.Token) ?? "")
            ?? throw new InvalidDataException("Missing installation request.");
        ValidateRequest(request);
        if (relay)
        {
            bool committed = false;
            try
            {
                using var workerPipe = CreatePipe(out string workerName);
                using var worker = Process.Start(StartInfo(Environment.ProcessPath!, "--update-worker", Environment.ProcessId, workerName, request.Elevated))
                    ?? throw new IOException("Unable to start installation worker.");
                await workerPipe.WaitForConnectionAsync(timeout.Token);
                VerifyPeer(workerPipe, worker.Id, client: true);
                using var workerReader = new StreamReader(workerPipe, Encoding.UTF8, false, 4096, true);
                using var workerWriter = new StreamWriter(workerPipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                await workerWriter.WriteLineAsync(JsonSerializer.Serialize(request));
                string? ready = await workerReader.ReadLineAsync(timeout.Token);
                await writer.WriteLineAsync(ready ?? "Installation worker disconnected.");
                if (ready != "READY" || await reader.ReadLineAsync(timeout.Token) != "COMMIT") return 1;
                await workerWriter.WriteLineAsync("COMMIT");
                committed = true;
                // Parent has committed to closing. Keep this ordinary-user relay alive for result reporting/relaunch.
                using var applyTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                string resultJson = await workerReader.ReadLineAsync(applyTimeout.Token) ?? throw new IOException("Installation result missing.");
                var result = JsonSerializer.Deserialize<InstallResult>(resultJson)!;
                File.WriteAllText(Path.Combine(request.JobDirectory, "result.json"), resultJson);
                if (!result.Success) MessageBox.Show(result.Error, "PicHarbor — Update", MessageBoxButton.OK, MessageBoxImage.Error);
                if (request.Restart)
                    Process.Start(new ProcessStartInfo(request.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(request.Target)! });
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                try { await writer.WriteLineAsync(ex is Win32Exception { NativeErrorCode: 1223 } ? "UAC_CANCELLED" : ex.Message); } catch { }
                File.WriteAllText(Path.Combine(request.JobDirectory, "error.log"), ex.ToString());
                if (committed) MessageBox.Show(ex.Message + "\n" + request.JobDirectory, "PicHarbor — Update", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        using var parentProcess = Process.GetProcessById(request.ParentId);
        string? staged = null;
        ServiceInstaller.ImageUpdate? service = null;
        var replacement = new ExecutableReplacement(request.Target);
        string backup = replacement.Backup;
        string hash = "";
        bool committedToApply = false;
        string? originalServiceImage = null;
        bool serviceWasRunning = false;
        void Journal(string phase, string? error = null) => File.WriteAllText(Path.Combine(request.JobDirectory, "transaction.json"),
            JsonSerializer.Serialize(new { phase, request.Target, backup, newHash = hash, originalServiceImage, serviceWasRunning, error }));
        try
        {
            // The GUI snapshots its setting over the authenticated pipe; an elevated worker may run as another account.
            using var http = UpdateHttp.CreateOfficialClient(request.ProxyAddress);
            var client = new ReleaseClient(http);
            // Re-fetch the expected checksum inside the worker: an elevated installation must not trust a user-writable manifest.
            var release = await client.ByTagAsync(request.Tag, timeout.Token);
            hash = await client.ChecksumAsync(release, timeout.Token);
            await UpdatePolicy.VerifyAsync(request.Candidate, release, hash, timeout.Token);
            if (request.Elevated)
            {
                if (!VaultServiceManager.IsElevated) throw new UnauthorizedAccessException("Administrator permission is required.");
                staged = ServiceInstaller.StageVerifiedUpdate(request.Candidate);
            }
            else
            {
                staged = request.Target + ".picharbor-new-" + Guid.NewGuid().ToString("N");
                File.Copy(request.Candidate, staged);
            }
            await UpdatePolicy.VerifyAsync(staged, release, hash, timeout.Token);
            // Probe the verified new binary without launching WPF or touching the archive.
            string expectedBuild = await ProbeBuildAsync(staged, request.Elevated, timeout.Token);
            if (ServiceInstaller.IsInstalled()) service = new ServiceInstaller.ImageUpdate(staged);
            originalServiceImage = service?.OriginalImage;
            serviceWasRunning = service?.RequiresRunningValidation == true;
            Journal("Prepared");
            await writer.WriteLineAsync("READY");
            if (await reader.ReadLineAsync(timeout.Token) != "COMMIT") return 1;
            committedToApply = true;
            using var applyTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await parentProcess.WaitForExitAsync(exitTimeout.Token);
            RejectLinks(request.Target);
            Journal("Replacing");
            await replacement.ApplyAsync(staged, local => UpdatePolicy.VerifyAsync(local, release, hash, applyTimeout.Token));
            Journal("UpdatingService");
            service?.Apply();
            if (service?.RequiresRunningValidation == true)
            {
                bool valid = false;
                for (int i = 0; i < 60; i++)
                {
                    var status = await Vault.VaultClient.QueryStatusAsync(applyTimeout.Token);
                    if (status?.ServerBuild == expectedBuild && status.ServerSid == "S-1-5-18") { valid = true; break; }
                    await Task.Delay(500, applyTimeout.Token);
                }
                if (!valid) throw new IOException("Updated Vault service did not become ready.");
            }
            Journal("Succeeded");
            var success = new InstallResult(request.Target, backup, hash, true, "", request.Candidate);
            string resultJson = JsonSerializer.Serialize(success);
            File.WriteAllText(Path.Combine(request.JobDirectory, "result.json"), resultJson);
            service?.Commit();
            // A broken reporting pipe after commit must not roll back only half of a successful update.
            try { await writer.WriteLineAsync(resultJson); } catch (IOException) { }
            return 0;
        }
        catch (Exception ex)
        {
            string error = ex.ToString();
            var rollbackService = service;
            service = null;
            try { rollbackService?.Dispose(); } catch (Exception rollback) { error += "\nSERVICE ROLLBACK FAILED: " + rollback; }
            try { replacement.Rollback(); }
            catch (Exception rollback) { error += "\nGUI ROLLBACK FAILED: " + rollback; }
            var result = new InstallResult(request.Target, backup, hash, false, error);
            try
            {
                Journal("Failed", error);
                File.WriteAllText(Path.Combine(request.JobDirectory, "result.json"), JsonSerializer.Serialize(result));
                await writer.WriteLineAsync(committedToApply ? JsonSerializer.Serialize(result) : ex.Message);
            }
            catch (IOException) { }
            return 1;
        }
        finally
        {
            service?.Dispose();
            try
            {
                if (request.Elevated && staged is not null) ServiceInstaller.ReleaseStagedUpdate(staged);
                else if (staged is not null && File.Exists(staged)) File.Delete(staged);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Debug.WriteLine("Update staging cleanup: " + ex.Message); }
        }
    }

    private static void ValidateRequest(InstallRequest request)
    {
        using var parent = Process.GetProcessById(request.ParentId);
        if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStart ||
            !string.Equals(parent.MainModule?.FileName, request.Target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update target does not match the requesting process.");
        RejectLinks(request.Target);
        RejectLinks(Environment.ProcessPath!);
        if (!string.Equals(Path.GetDirectoryName(Environment.ProcessPath), request.JobDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(request.JobDirectory).StartsWith("job-", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid updater job directory.");
        if (UpdatePolicy.ParseVersion(request.Tag) is not { } version ||
            version <= UpdatePolicy.ParseVersion(FileVersionInfo.GetVersionInfo(request.Target).ProductVersion, false))
            throw new InvalidDataException("Update must be newer than the current executable.");
        using var original = File.OpenRead(request.Target);
        using var helper = File.OpenRead(Environment.ProcessPath!);
        if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(helper)))
            throw new InvalidDataException("Updater does not match the requesting executable.");
    }

    private static async Task<string> ProbeBuildAsync(string executable, bool protectedStage, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        if (protectedStage)
            info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(Path.GetDirectoryName(executable)!, "BundleCache");
        info.ArgumentList.Add("--update-probe");
        using var process = Process.Start(info) ?? throw new IOException("Unable to probe new executable.");
        string build = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !Guid.TryParse(build, out _)) throw new InvalidDataException("Invalid new executable build.");
        return build;
    }

    private static ProcessStartInfo StartInfo(string executable, string command, int parent, string pipe, bool elevate)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = elevate, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (elevate) info.Verb = "runas";
        info.ArgumentList.Add(command);
        info.ArgumentList.Add(parent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(pipe);
        return info;
    }

    private static NamedPipeServerStream CreatePipe(out string name)
    {
        name = "PicHarbor.Update." + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private static void VerifyPeer(PipeStream pipe, int expected, bool client)
    {
        uint pid;
        bool ok = client ? GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid);
        if (!ok || pid != expected) throw new UnauthorizedAccessException("Unexpected updater pipe peer.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    private static void RejectLinks(string target)
    {
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked update targets are unsupported.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(target)!); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked update directories are unsupported.");
    }

    public static void CompleteStartup()
    {
        if (!IsPackaged || !Directory.Exists(CacheRoot)) return;
        foreach (string job in Directory.EnumerateDirectories(CacheRoot, "job-*"))
        {
            try
            {
                string resultPath = Path.Combine(job, "result.json");
                if (!File.Exists(resultPath)) continue;
                var result = JsonSerializer.Deserialize<InstallResult>(File.ReadAllText(resultPath));
                if (result is not { Success: true } || result.Target != Environment.ProcessPath ||
                    !result.Backup.StartsWith(result.Target + ".picharbor-old-", StringComparison.Ordinal) ||
                    Path.GetDirectoryName(result.Backup) != Path.GetDirectoryName(result.Target)) continue;
                using var current = File.OpenRead(result.Target);
                if (!Convert.ToHexString(SHA256.HashData(current)).Equals(result.NewHash, StringComparison.OrdinalIgnoreCase)) continue;
                bool backupRemoved = false;
                try { if (File.Exists(result.Backup)) File.Delete(result.Backup); backupRemoved = true; }
                catch (UnauthorizedAccessException) { /* Never request UAC solely for cleanup. Retain the receipt for a later attempt. */ }
                if (result.Candidate is { } candidate && Path.GetFileName(candidate) == ReleaseUpdate.AssetName &&
                    Path.GetDirectoryName(Path.GetDirectoryName(candidate)) == CacheRoot && File.Exists(candidate))
                {
                    RejectLinks(candidate);
                    File.Delete(candidate);
                }
                string helper = Path.Combine(job, "Updater.exe");
                if (File.Exists(helper)) File.Delete(helper);
                if (backupRemoved) File.Delete(resultPath);
            }
            catch (Exception ex) { Debug.WriteLine("Update cleanup: " + ex.Message); }
        }
    }
}
