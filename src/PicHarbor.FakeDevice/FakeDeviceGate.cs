using PicHarbor.Core.Device;

namespace PicHarbor.FakeDevice;

/// <summary>
/// The opt-in EXE seam: reads the <c>GAS_FAKE_DEVICE</c> environment variable and, when set, builds a
/// <see cref="FakeAfcDevice"/> from it so the real CLI can run end-to-end against the fake with no hardware.
/// </summary>
/// <remarks>
/// This type is referenced by <c>PicHarbor.Cli</c> <b>only</b> when the CLI is compiled with the
/// <c>FAKE_DEVICE</c> constant (a non-shipping, opt-in build). A normal release build never references this
/// assembly, so the env var has no effect on — and no presence in — the shipped <c>picharbor.exe</c>. The
/// seam is read-only: it can only ever return an <see cref="IPhoneClient"/> (whose surface is read-only) and
/// makes no real-device call.
/// </remarks>
public static class FakeDeviceGate
{
    /// <summary>The environment variable that activates the fake device.</summary>
    public const string EnvironmentVariable = "GAS_FAKE_DEVICE";

    /// <summary>Whether the fake-device environment variable is currently set to a non-empty value.</summary>
    public static bool IsActive => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Builds a fake device from <c>GAS_FAKE_DEVICE</c> when it is set; otherwise returns
    /// <see langword="null"/> so the caller falls back to the real <c>AfcIPhoneClient</c>.
    /// </summary>
    /// <returns>A connected-capable fake device, or <see langword="null"/> when the env var is unset.</returns>
    /// <exception cref="FormatException">The env var is set but its spec string is malformed.</exception>
    public static IPhoneClient? TryCreate()
    {
        string? spec = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(spec) ? null : FakeDeviceSpecParser.Parse(spec).Build();
    }
}
