namespace GetAndSee.Core.Errors;

/// <summary>
/// Raised when the device cannot be reached or an AFC/lockdown read operation fails.
/// Carries a user-facing message suitable for printing directly to the console.
/// </summary>
public class DeviceException : Exception
{
    /// <summary>Creates a <see cref="DeviceException"/> with a user-facing message.</summary>
    /// <param name="message">A clear, actionable description of what went wrong.</param>
    public DeviceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a <see cref="DeviceException"/> with a user-facing message and an inner cause.</summary>
    /// <param name="message">A clear, actionable description of what went wrong.</param>
    /// <param name="innerException">The underlying exception.</param>
    public DeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
