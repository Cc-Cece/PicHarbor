using System.Globalization;

namespace PicHarbor.Core.Util;

/// <summary>Formats byte counts as compact, human-readable sizes (e.g. <c>397.2 GB</c>).</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count using binary (1024) units and one decimal place above bytes.</summary>
    /// <param name="bytes">A non-negative byte count.</param>
    /// <returns>A string such as <c>0 B</c>, <c>512 B</c>, or <c>1.5 GB</c>.</returns>
    public static string Humanize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} {Units[0]}";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {Units[unit]}");
    }
}
