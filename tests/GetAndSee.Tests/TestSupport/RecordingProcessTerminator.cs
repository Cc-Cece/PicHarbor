using GetAndSee.Core.Transfer;

namespace GetAndSee.Tests.TestSupport;

/// <summary>
/// An <see cref="IProcessTerminator"/> test double that records the requested exit code instead of
/// terminating, so the disconnect escape-hatch (#45) can be asserted without killing the test host.
/// </summary>
internal sealed class RecordingProcessTerminator : IProcessTerminator
{
    private int invocations;

    /// <summary>How many times <see cref="Terminate"/> has been called.</summary>
    public int Invocations => Volatile.Read(ref invocations);

    /// <summary>The exit code from the most recent <see cref="Terminate"/> call, or null if never called.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>True once <see cref="Terminate"/> has been called at least once.</summary>
    public bool WasInvoked => Invocations > 0;

    /// <inheritdoc />
    public void Terminate(int exitCode)
    {
        ExitCode = exitCode;
        Interlocked.Increment(ref invocations);
    }
}
