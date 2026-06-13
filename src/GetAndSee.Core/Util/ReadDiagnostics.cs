using System.Globalization;

namespace GetAndSee.Core.Util;

/// <summary>
/// Hidden, opt-in diagnostics for the device read path, used to capture the exact native return values
/// during a real cable-yank (the #11 → #25 → #38 → #42 unplug investigation). Disabled unless the
/// <c>GAS_DEBUG_READS</c> environment variable is set, so the healthy copy path pays only a single cached
/// boolean check per read and the diagnostics never ship enabled.
/// </summary>
/// <remarks>
/// <para>
/// When enabled, each <c>afc_file_read</c> call and the staging file's growth are logged to
/// <see cref="Console.Error"/> with a <c>[reads]</c> prefix: the per-read AFC error code, the requested
/// and returned byte counts, the elapsed native time, and the running total. A frozen total at ~100% CPU
/// with no error is the signature of a native read that never returns — the thing this exists to prove on
/// hardware.
/// </para>
/// <para>
/// Read-only and side-effect-free with respect to the device: it only observes values the read path
/// already produced. It logs device file paths (e.g. <c>/DCIM/100APPLE/IMG_0001.MOV</c>), which are
/// generic camera paths, never file contents or user-identifying data.
/// </para>
/// </remarks>
public static class ReadDiagnostics
{
    /// <summary>
    /// Whether read diagnostics are enabled (the <c>GAS_DEBUG_READS</c> environment variable is set to a
    /// truthy value). Evaluated once at startup so the healthy read path stays a single field read.
    /// </summary>
    public static readonly bool Enabled = IsTruthy(Environment.GetEnvironmentVariable("GAS_DEBUG_READS"));

    private static readonly object Gate = new();

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.Ordinal) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Logs a single device read. No-op unless <see cref="Enabled"/>; callers must still guard the
    /// surrounding timing so a disabled build allocates nothing.
    /// </summary>
    /// <param name="path">The device path being read.</param>
    /// <param name="error">The AFC error code the native read returned (e.g. <c>Success</c>).</param>
    /// <param name="requested">The number of bytes requested.</param>
    /// <param name="received">The number of bytes the native read reported.</param>
    /// <param name="elapsed">How long the native read took.</param>
    /// <param name="runningTotal">The running total of bytes streamed for this file so far.</param>
    public static void LogRead(string path, object error, int requested, long received, TimeSpan elapsed, long runningTotal)
    {
        if (!Enabled)
        {
            return;
        }

        Write(
            $"[reads] read path={path} error={error} requested={requested} received={received} " +
            $"elapsedMs={elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)} total={runningTotal}");
    }

    /// <summary>
    /// Logs a free-form diagnostic line. No-op unless <see cref="Enabled"/>.
    /// </summary>
    /// <param name="message">The message to log (without the <c>[reads]</c> prefix).</param>
    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Write($"[reads] {message}");
    }

    private static void Write(string line)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (Gate)
        {
            Console.Error.WriteLine($"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}");
        }
    }
}
