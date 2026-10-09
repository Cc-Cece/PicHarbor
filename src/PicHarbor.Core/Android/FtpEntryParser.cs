using System.Globalization;
using System.Text.RegularExpressions;

namespace PicHarbor.Core.Android;

/// <summary>
/// Parsed information for a directory entry returned by FTP LIST command.
/// </summary>
public sealed record FtpFileSystemEntry(string Name, bool IsDirectory, long Size, DateTimeOffset? ModifiedAt);

/// <summary>
/// Robust parser for standard FTP LIST responses (UNIX style and Windows/DOS style).
/// </summary>
public static class FtpEntryParser
{
    private static readonly Regex DosRegex = new(
        @"^(?<date>\d{2}-\d{2}-\d{2,4})\s+(?<time>\d{2}:\d{2}(?:[AP]M)?)\s+(?:<DIR>|(?<size>\d+))\s+(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses a single line from an FTP LIST response. Returns null if the line is not a valid entry (e.g. . or .. or header).
    /// </summary>
    public static FtpFileSystemEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        line = line.Trim();

        // 1. Check Windows / DOS format
        // Example: 04-15-24  01:23PM       <DIR>          Camera
        // Example: 04-15-24  01:23PM              2048576 IMG_0001.JPG
        var dosMatch = DosRegex.Match(line);
        if (dosMatch.Success)
        {
            string name = dosMatch.Groups["name"].Value.Trim();
            if (name == "." || name == "..") return null;

            bool isDir = line.Contains("<DIR>", StringComparison.OrdinalIgnoreCase);
            long size = 0;
            if (!isDir && dosMatch.Groups["size"].Success)
            {
                long.TryParse(dosMatch.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            }

            DateTimeOffset? dt = null;
            string dateStr = $"{dosMatch.Groups["date"].Value} {dosMatch.Groups["time"].Value}";
            if (DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDt))
            {
                dt = parsedDt;
            }

            return new FtpFileSystemEntry(name, isDir, size, dt);
        }

        // 2. Check UNIX format
        // Example: drwxr-xr-x  2 owner group 4096 Oct 09 12:00 Camera
        // Example: -rw-r--r--  1 owner group 2048576 Oct 09 12:00 IMG_0001.JPG
        char firstChar = line[0];
        if (firstChar == 'd' || firstChar == '-' || firstChar == 'l')
        {
            bool isDir = firstChar == 'd';
            // Split by whitespace into max 9 parts
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 9)
            {
                // In standard UNIX format:
                // tokens[0]: permissions (e.g. -rw-r--r--)
                // tokens[1]: link count
                // tokens[2]: owner
                // tokens[3]: group
                // tokens[4]: size
                // tokens[5]: month
                // tokens[6]: day
                // tokens[7]: time or year
                // tokens[8..]: name (can have spaces)
                long size = 0;
                long.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);

                // Reconstruct filename from tokens[8] onward
                // Find where tokens[8] starts in the original line
                int nameStartIndex = 0;
                int tokenCount = 0;
                for (int i = 0; i < line.Length; i++)
                {
                    if (!char.IsWhiteSpace(line[i]))
                    {
                        if (i == 0 || char.IsWhiteSpace(line[i - 1]))
                        {
                            tokenCount++;
                            if (tokenCount == 9)
                            {
                                nameStartIndex = i;
                                break;
                            }
                        }
                    }
                }

                string name = nameStartIndex > 0 ? line.Substring(nameStartIndex).Trim() : tokens[8];
                if (name == "." || name == "..") return null;

                // Handle symlink "name -> target"
                if (firstChar == 'l' && name.Contains(" -> ", StringComparison.Ordinal))
                {
                    name = name.Substring(0, name.IndexOf(" -> ", StringComparison.Ordinal)).Trim();
                }

                DateTimeOffset? dt = null;
                string dateStr = $"{tokens[5]} {tokens[6]} {tokens[7]}";
                if (DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDt))
                {
                    dt = parsedDt;
                }

                return new FtpFileSystemEntry(name, isDir, size, dt);
            }
        }

        return null;
    }
}
