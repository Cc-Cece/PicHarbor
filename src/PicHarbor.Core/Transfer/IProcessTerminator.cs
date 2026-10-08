namespace PicHarbor.Core.Transfer;

/// <summary>
/// Abstraction over an immediate, finalizer-skipping process termination, injected so the disconnect
/// escape-hatch (<see cref="DisconnectEscapeHatch"/>) can be exercised in tests without killing the test
/// host.
/// </summary>
/// <remarks>
/// The production implementation (<see cref="TerminateProcessTerminator"/>) hard-terminates the current
/// process via the OS so that <b>no managed or critical finalizer runs</b>. That is the whole point on the
/// #45 disconnect path: the byte heartbeat is dead because a synchronous native call (<c>afc_file_close</c>)
/// is busy-spinning on a dead transport, and any finalizer that re-enters that native layer would itself
/// hang. A test double records the requested exit code instead of terminating, so the escape-hatch's
/// behaviour can be asserted deterministically.
/// </remarks>
public interface IProcessTerminator
{
    /// <summary>
    /// Immediately terminates the current process with <paramref name="exitCode"/> without running any
    /// finalizers. The call is not expected to return.
    /// </summary>
    /// <param name="exitCode">The process exit code (3 for a lost-device disconnect).</param>
    void Terminate(int exitCode);
}
