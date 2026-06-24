using System.Diagnostics;

namespace GetAndSee.Tests.E2E;

/// <summary>The captured outcome of one external process run: its exit code and drained stdout/stderr.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Everything the process wrote to stdout.</param>
/// <param name="StandardError">Everything the process wrote to stderr.</param>
internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Spawns a real child process and captures its exit code and output for the real-<c>.exe</c> E2E lane. Uses
/// <see cref="ProcessStartInfo.ArgumentList"/> (no shell, no string concatenation) so arguments are passed
/// verbatim with no quoting/injection surface, and drains stdout/stderr concurrently with the wait so a
/// chatty child can never deadlock on a full pipe. A per-run timeout kills the process tree so a wedged
/// subprocess fails the test fast instead of hanging the run.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> and returns its captured result.</summary>
    /// <param name="fileName">The executable to launch.</param>
    /// <param name="arguments">Arguments passed verbatim (each its own argv entry — no shell parsing).</param>
    /// <param name="workingDirectory">Working directory for the child, or <see langword="null"/> to inherit.</param>
    /// <param name="environment">Extra environment variables to set on the child only (the parent is untouched).</param>
    /// <param name="timeout">Maximum time to wait before the process tree is killed.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The process's exit code and drained output.</returns>
    /// <exception cref="TimeoutException">The process did not exit within <paramref name="timeout"/>.</exception>
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();

        // Start draining both pipes BEFORE waiting so the child never blocks on a full stdout/stderr buffer.
        Task<string> readStandardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> readStandardError = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKillTree(process);

            // Killing the tree closes the child's pipes, so the drain tasks complete; capture whatever was
            // emitted before the timeout so a hang is diagnosable instead of an opaque "did not exit".
            string partialOutput = await DrainSafelyAsync(readStandardOutput).ConfigureAwait(false);
            string partialError = await DrainSafelyAsync(readStandardError).ConfigureAwait(false);
            throw new TimeoutException(
                $"Process '{fileName}' did not exit within {timeout.TotalSeconds:N0}s and was killed.\n" +
                $"partial stdout:\n{partialOutput}\npartial stderr:\n{partialError}");
        }

        string standardOutput = await readStandardOutput.ConfigureAwait(false);
        string standardError = await readStandardError.ConfigureAwait(false);
        return new ProcessRunResult(process.ExitCode, standardOutput, standardError);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already exited between the check and the kill — nothing to reap.
        }
    }

    private static async Task<string> DrainSafelyAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return "(unavailable)";
        }
    }
}
