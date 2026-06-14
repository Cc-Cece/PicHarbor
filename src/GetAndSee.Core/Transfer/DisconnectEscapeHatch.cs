using GetAndSee.Core.Journal;
using GetAndSee.Core.Summary;

namespace GetAndSee.Core.Transfer;

/// <summary>
/// The Sprint 3.4 disconnect escape-hatch: the single action run on the forward-progress watchdog's
/// independent timer thread when the device is proven gone (#45). It writes the run summary and terminates
/// the process with exit code 3 <b>without unwinding through native device code</b>.
/// </summary>
/// <remarks>
/// <para>
/// Five rounds of guarding individual native calls (read, open, stat, list) each relocated the hang to the
/// next unguarded synchronous native call. #45 captured the real class live: on a mid-copy yank
/// <c>afc_file_read</c> returns <c>EmptyResponse</c>/0 and the fault unwinds into the read stream's
/// <c>await using</c> disposal → <c>afc_file_close</c>, which busy-spins forever on the dead transport.
/// </para>
/// <para>
/// When the byte heartbeat is dead the device is provably gone and the data is already safe (the journal is
/// a crash-safe SQLite WAL with each file committed). So instead of trying to unwind gracefully through
/// native code that may spin, this escape-hatch — running on the watchdog's independent timer thread, which
/// the wedged main thread cannot block — prints the disconnect line, writes and flushes
/// <c>summary.txt</c> from a fresh read-only journal connection (no device call), and hard-terminates the
/// process via <see cref="IProcessTerminator"/>. The orphaned spinning native thread is reaped by the OS.
/// </para>
/// <para>
/// Summary writing is best-effort: the journal on disk is the durable source of truth and recovers on the
/// next open, so a failure to refresh <c>summary.txt</c> must never prevent the terminate — the terminate
/// is the load-bearing guarantee.
/// </para>
/// </remarks>
public sealed class DisconnectEscapeHatch
{
    /// <summary>The process exit code that signals a clean, resumable lost-device stop.</summary>
    public const int DisconnectExitCode = 3;

    /// <summary>The single line printed when the device is detected gone.</summary>
    public const string DisconnectMessage =
        "Device disconnected. Progress saved — reconnect and re-run to resume.";

    private readonly string destinationRoot;
    private readonly IProcessTerminator terminator;
    private readonly TextWriter output;
    private int activated;

    /// <summary>Creates a disconnect escape-hatch targeting <paramref name="destinationRoot"/>.</summary>
    /// <param name="destinationRoot">Destination root holding the journal and where <c>summary.txt</c> is written.</param>
    /// <param name="terminator">The finalizer-skipping process terminator (real impl = <see cref="TerminateProcessTerminator"/>).</param>
    /// <param name="output">Where the disconnect line is printed; defaults to <see cref="Console.Out"/>.</param>
    public DisconnectEscapeHatch(string destinationRoot, IProcessTerminator terminator, TextWriter? output = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(terminator);
        this.destinationRoot = destinationRoot;
        this.terminator = terminator;
        this.output = output ?? Console.Out;
    }

    /// <summary>
    /// Runs the escape-hatch exactly once: print the disconnect line, write and flush <c>summary.txt</c>,
    /// then terminate the process with <see cref="DisconnectExitCode"/>. Safe to call from the watchdog's
    /// timer thread; never re-enters native device code.
    /// </summary>
    public void Activate()
    {
        if (Interlocked.Exchange(ref activated, 1) != 0)
        {
            return; // A single trip fires the escape-hatch once; ignore any subsequent calls.
        }

        // Everything before the terminate is best-effort so the terminate is UNCONDITIONAL: it is the
        // load-bearing exit-3 guarantee and must run even if printing the line throws (e.g. a broken pipe
        // on a redirected stdout racing the yank). A swallowed print never costs us the clean, resumable
        // exit 3; this runs on the watchdog's timer thread, where an escaped exception would also skip it.
        try
        {
            output.WriteLine(DisconnectMessage);
        }
        catch (Exception)
        {
            // Swallow: a failed console write must never skip the terminate below.
        }

        TryWriteSummary();
        terminator.Terminate(DisconnectExitCode);
    }

    private void TryWriteSummary()
    {
        try
        {
            // A FRESH read-only connection, not the in-flight writer connection: the main thread is wedged
            // in native code and a SqliteConnection is not thread-safe, so we never touch its connection.
            // WAL lets this reader see every committed file without blocking. No device call is made.
            using TransferJournal journal = TransferJournal.OpenReadOnly(destinationRoot);
            new SummaryWriter().Write(
                destinationRoot,
                journal.ReadManifest(),
                journal.ReadDevices(),
                journal.ReadRunsSummary(),
                DateTimeOffset.UtcNow,
                flushToDisk: true);
        }
        catch (Exception)
        {
            // Best-effort only. The journal on disk (crash-safe SQLite WAL, per-file committed) is the
            // durable record and recovers on the next run, which regenerates summary.txt. Never let a
            // summary-write failure block the load-bearing terminate below.
        }
    }
}
