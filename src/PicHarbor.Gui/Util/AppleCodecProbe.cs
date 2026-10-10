using Windows.Management.Deployment;

namespace PicHarbor.Gui.Util;

public readonly record struct AppleCodecStatus(bool HeifInstalled, bool HevcInstalled);

/// <summary>
/// Detects the HEIF image extension and the HEVC video extension for the current user.
/// The paid HEVC package and the device-manufacturer package share the same package name.
/// </summary>
public static class AppleCodecProbe
{
    public const string HeifPackageName = "Microsoft.HEIFImageExtension";
    public const string HevcPackageName = "Microsoft.HEVCVideoExtension";

    public static AppleCodecStatus Probe()
    {
        try
        {
            var manager = new PackageManager();
            bool heif = false;
            bool hevc = false;
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                string? name = package.Id?.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name.Equals(HeifPackageName, StringComparison.OrdinalIgnoreCase))
                {
                    heif = true;
                }
                else if (name.Equals(HevcPackageName, StringComparison.OrdinalIgnoreCase))
                {
                    hevc = true;
                }

                if (heif && hevc)
                {
                    break;
                }
            }

            return new AppleCodecStatus(heif, hevc);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AppleCodecProbe failed: {ex.Message}");
            return new AppleCodecStatus(false, false);
        }
    }
}
