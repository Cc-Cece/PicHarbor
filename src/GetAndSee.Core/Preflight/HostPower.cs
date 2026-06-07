using System.Runtime.InteropServices;

namespace GetAndSee.Core.Preflight;

/// <summary>Host AC/battery power state, used by the on-battery pre-flight warning (R14).</summary>
public enum HostPowerStatus
{
    /// <summary>Could not be determined.</summary>
    Unknown,

    /// <summary>Running on AC (mains) power.</summary>
    Ac,

    /// <summary>Running on battery.</summary>
    Battery,
}

/// <summary>Reads the host's power-line status on Windows via <c>GetSystemPowerStatus</c>.</summary>
internal static partial class HostPower
{
    /// <summary>Returns the current power state, or <see cref="HostPowerStatus.Unknown"/> if unavailable.</summary>
    public static HostPowerStatus Get()
    {
        if (!OperatingSystem.IsWindows())
        {
            return HostPowerStatus.Unknown;
        }

        try
        {
            if (GetSystemPowerStatus(out SystemPowerStatus status))
            {
                return status.AcLineStatus switch
                {
                    0 => HostPowerStatus.Battery,
                    1 => HostPowerStatus.Ac,
                    _ => HostPowerStatus.Unknown,
                };
            }
        }
        catch (Exception)
        {
            // Power status is advisory only; never fail a run because we couldn't read it.
        }

        return HostPowerStatus.Unknown;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}
