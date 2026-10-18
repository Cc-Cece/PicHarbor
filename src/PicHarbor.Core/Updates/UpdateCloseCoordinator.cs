namespace PicHarbor.Core.Updates;

/// <summary>Disposition of a window close request while an update is ready.</summary>
public enum UpdateCloseAction
{
    /// <summary>Close normally without starting another installation.</summary>
    Close,
    /// <summary>Defer this close while attempting the scheduled installation.</summary>
    InstallOnExit,
    /// <summary>Cancel an existing installation wait and honour the new close request.</summary>
    CancelInstallationAndClose
}

// A second close request must be an escape from an asynchronous install wait, not another veto.
/// <summary>Prevents a scheduled update from trapping the user in a repeated close wait.</summary>
public sealed class UpdateCloseCoordinator
{
    private bool permitted;
    /// <summary>Whether a close-triggered installation attempt is pending.</summary>
    public bool IsHandlingClose { get; private set; }

    /// <summary>Classifies a close request, permitting a second request to escape the pending wait.</summary>
    public UpdateCloseAction RequestClose(bool ready, bool installOnExit)
    {
        if (permitted) return UpdateCloseAction.Close;
        if (IsHandlingClose)
        {
            permitted = true;
            return UpdateCloseAction.CancelInstallationAndClose;
        }
        if (!ready || !installOnExit) return UpdateCloseAction.Close;
        IsHandlingClose = true;
        return UpdateCloseAction.InstallOnExit;
    }

    /// <summary>Allows an installer handoff or declined prompt to close without another attempt.</summary>
    public void PermitClose() => permitted = true;
    /// <summary>Releases the pending close attempt after success, failure or cancellation.</summary>
    public void FinishCloseAttempt() => IsHandlingClose = false;
}
