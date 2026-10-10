using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PicHarbor.Core.Device;

/// <summary>
/// Represents a hardware USB portable device detected on the Windows host.
/// </summary>
/// <param name="FriendlyName">Display friendly name (e.g. Xiaomi 14, M2012K11AC).</param>
/// <param name="HardwareSerial">Hardware factory serial number extracted from USB descriptor.</param>
/// <param name="DeviceInstancePath">Full device instance path in Windows device tree.</param>
public sealed record UsbDetectedDevice(
    string FriendlyName,
    string HardwareSerial,
    string DeviceInstancePath);

/// <summary>
/// High-speed native Windows probe for detecting USB device hardware serial numbers without requiring ADB or developer mode.
/// </summary>
public static class UsbDeviceSerialProbe
{
    private static readonly Regex UsbSerialPattern = new(
        @"USB#(?:VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}(?:&MI_\d+)?)#([^#&]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Probes connected or registered portable devices and extracts their factory hardware serial numbers.
    /// </summary>
    public static IReadOnlyList<UsbDetectedDevice> ProbeConnectedUsbDevices()
    {
        var devices = new List<UsbDetectedDevice>();

        if (!OperatingSystem.IsWindows())
        {
            return devices;
        }

        try
        {
            using RegistryKey? devicesKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Portable Devices\Devices");
            if (devicesKey == null)
            {
                return devices;
            }

            foreach (string subKeyName in devicesKey.GetSubKeyNames())
            {
                Match match = UsbSerialPattern.Match(subKeyName);
                if (match.Success)
                {
                    string serial = match.Groups[1].Value.Trim();
                    // Exclude trivial Windows auto-generated composite counters if present
                    if (string.IsNullOrWhiteSpace(serial) || serial.StartsWith("0000") && serial.Length < 8)
                    {
                        continue;
                    }

                    using RegistryKey? subKey = devicesKey.OpenSubKey(subKeyName);
                    string friendlyName = subKey?.GetValue("FriendlyName") as string ?? "Android Device";

                    // Avoid duplicate serial entries
                    if (!devices.Any(d => string.Equals(d.HardwareSerial, serial, StringComparison.OrdinalIgnoreCase)))
                    {
                        devices.Add(new UsbDetectedDevice(friendlyName, serial, subKeyName));
                    }
                }
            }
        }
        catch
        {
            // Best effort registry probe
        }

        return devices;
    }
}
