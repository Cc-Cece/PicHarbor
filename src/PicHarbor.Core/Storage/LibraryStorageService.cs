using System.IO;
using PicHarbor.Core.Journal;

namespace PicHarbor.Core.Storage;

/// <summary>
/// Information about a candidate storage volume / drive for PicHarbor photo libraries.
/// </summary>
public sealed record LibraryVolumeInfo(
    string DriveLetter,
    string VolumeLabel,
    string RootPath,
    long TotalBytes,
    long AvailableFreeBytes,
    bool HasExistingLibrary,
    bool IsSystemDrive,
    bool IsActiveTarget)
{
    /// <summary>Free space in gigabytes, rounded to one decimal place.</summary>
    public double FreeSpaceGb => Math.Round(AvailableFreeBytes / (1024.0 * 1024.0 * 1024.0), 1);

    /// <summary>Total space in gigabytes, rounded to one decimal place.</summary>
    public double TotalSpaceGb => Math.Round(TotalBytes / (1024.0 * 1024.0 * 1024.0), 1);

    /// <summary>Human-readable display text for volume selection UI.</summary>
    public string DisplayText =>
        $"{DriveLetter} [{VolumeLabel}] (可用: {FreeSpaceGb} GB / {TotalSpaceGb} GB)" +
        (HasExistingLibrary ? " ★已有备份库" : "");
}

/// <summary>
/// Service managing standardized library paths, drive enumeration, auto-discovery of existing
/// PicHarbor archives across drives, and device folder sanitization.
/// </summary>
public static class LibraryStorageService
{
    /// <summary>The fixed, standardized folder name for PicHarbor libraries.</summary>
    public const string StandardFolderName = "PicHarbor";

    /// <summary>
    /// Normalizes a drive letter input (e.g. "D", "D:", "D:\") to uppercase form with colon (e.g. "D:").
    /// </summary>
    public static string NormalizeDriveLetter(string? driveInput)
    {
        if (string.IsNullOrWhiteSpace(driveInput))
        {
            return string.Empty;
        }

        string trimmed = driveInput.Trim().TrimEnd('\\', '/');
        if (!trimmed.EndsWith(':') && trimmed.Length == 1 && char.IsLetter(trimmed[0]))
        {
            trimmed += ":";
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>
    /// Resolves the standardized library root directory for a given drive.
    /// <para>
    /// - For the system/user drive (matching user profile): <c>%USERPROFILE%\Pictures\PicHarbor</c>.
    /// - For any other data/removable drive (e.g. D:): <c>&lt;Drive&gt;:\Pictures\PicHarbor</c>.
    /// </para>
    /// </summary>
    public static string GetLibraryRootForDrive(string driveLetter)
    {
        string norm = NormalizeDriveLetter(driveLetter);
        if (string.IsNullOrWhiteSpace(norm))
        {
            norm = GetDefaultDriveLetter();
        }

        string driveRoot = norm.EndsWith('\\') ? norm : norm + "\\";

        string userPictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string? userPicturesRoot = Path.GetPathRoot(userPictures);

        if (!string.IsNullOrEmpty(userPicturesRoot) &&
            string.Equals(NormalizeDriveLetter(userPicturesRoot), norm, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(userPictures, StandardFolderName);
        }

        return Path.Combine(driveRoot, "Pictures", StandardFolderName);
    }

    /// <summary>
    /// Discovers all ready drives on the system, resolves their standardized library paths,
    /// and flags whether an existing archive/journal was detected.
    /// </summary>
    public static IReadOnlyList<LibraryVolumeInfo> DiscoverVolumes(string? activeDriveLetter = null)
    {
        string normalizedActive = NormalizeDriveLetter(activeDriveLetter);
        string userPicturesRoot = NormalizeDriveLetter(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));

        var volumes = new List<LibraryVolumeInfo>();

        try
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            foreach (DriveInfo drive in drives)
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                string letter = NormalizeDriveLetter(drive.Name);
                if (string.IsNullOrWhiteSpace(letter))
                {
                    continue;
                }

                string rootPath = GetLibraryRootForDrive(letter);
                bool hasExisting = File.Exists(TransferJournal.ResolveDatabasePath(rootPath));
                bool isSystem = string.Equals(letter, userPicturesRoot, StringComparison.OrdinalIgnoreCase);
                bool isActive = !string.IsNullOrEmpty(normalizedActive) &&
                                string.Equals(letter, normalizedActive, StringComparison.OrdinalIgnoreCase);

                string label = drive.VolumeLabel;
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = isSystem ? "系统盘" : (drive.DriveType == DriveType.Removable ? "移动磁盘" : "本地磁盘");
                }

                volumes.Add(new LibraryVolumeInfo(
                    letter,
                    label,
                    rootPath,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    hasExisting,
                    isSystem,
                    isActive));
            }
        }
        catch
        {
            // Best effort drive discovery
        }

        // Sort: Active target first, then existing library first, then largest free space.
        return volumes
            .OrderByDescending(v => v.IsActiveTarget)
            .ThenByDescending(v => v.HasExistingLibrary)
            .ThenByDescending(v => v.AvailableFreeBytes)
            .ToList();
    }

    /// <summary>
    /// Discovers all candidate library roots across ready drives that currently contain an existing database.
    /// </summary>
    public static IReadOnlyList<string> FindAllExistingLibraryRoots()
    {
        return DiscoverVolumes()
            .Where(v => v.HasExistingLibrary)
            .Select(v => v.RootPath)
            .ToList();
    }

    /// <summary>
    /// Gets the default drive letter, preferring a non-system drive with maximum free space if available,
    /// otherwise falling back to the system drive.
    /// </summary>
    public static string GetDefaultDriveLetter()
    {
        try
        {
            var volumes = DiscoverVolumes();
            if (volumes.Count == 0)
            {
                return "C:";
            }

            // Prefer existing library first
            var existing = volumes.FirstOrDefault(v => v.HasExistingLibrary);
            if (existing is not null)
            {
                return existing.DriveLetter;
            }

            // Then prefer non-system drive with > 20GB free space
            var nonSystem = volumes.FirstOrDefault(v => !v.IsSystemDrive && v.AvailableFreeBytes > 20L * 1024 * 1024 * 1024);
            if (nonSystem is not null)
            {
                return nonSystem.DriveLetter;
            }

            return volumes[0].DriveLetter;
        }
        catch
        {
            return "C:";
        }
    }

    /// <summary>
    /// Sanitizes a device name for safe usage as a subdirectory name on Windows.
    /// Replaces or strips invalid filesystem characters (&lt;, &gt;, :, ", /, \, |, ?, *).
    /// </summary>
    public static string SanitizeDeviceFolderName(string? rawName, string fallback = "DefaultDevice")
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallback;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();

        string sanitized = new(rawName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());

        sanitized = sanitized.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    /// <summary>
    /// Resolves the full path for a device folder within a library root.
    /// </summary>
    public static string GetDeviceFolderPath(string libraryRoot, string deviceFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        return Path.Combine(libraryRoot, SanitizeDeviceFolderName(deviceFolderName));
    }
}
