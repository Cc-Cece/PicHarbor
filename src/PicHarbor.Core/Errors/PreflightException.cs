namespace PicHarbor.Core.Errors;

/// <summary>
/// Raised by a pre-flight check that should stop the run before any copying begins
/// (driver service down, destination not writable, insufficient free space).
/// Carries a user-facing, actionable message.
/// </summary>
public sealed class PreflightException : Exception
{
    /// <summary>Creates a <see cref="PreflightException"/> with a user-facing message.</summary>
    /// <param name="message">A clear, actionable description of what failed and how to fix it.</param>
    public PreflightException(string message)
        : base(message)
    {
    }
}
