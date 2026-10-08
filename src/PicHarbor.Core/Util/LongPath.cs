namespace PicHarbor.Core.Util;

/// <summary>
/// Normalizes Windows destination paths to the extended-length <c>\\?\</c> form so the copier can
/// write past the legacy 260-character <c>MAX_PATH</c> limit (R6).
/// </summary>
/// <remarks>
/// <para>
/// A deep destination root combined with a long original filename can exceed <c>MAX_PATH</c> and
/// throw a cryptic <see cref="System.IO.IOException"/> (actually <see cref="System.IO.PathTooLongException"/>).
/// Prefixing a fully-qualified path with the <c>\\?\</c> device prefix (or <c>\\?\UNC\</c> for a network
/// share) tells Windows to skip the legacy limit and pass the path straight to the file system. The
/// prefix works regardless of the machine's <c>LongPathsEnabled</c> registry/manifest setting, so it
/// is the reliable mechanism on any runner.
/// </para>
/// <para>
/// Read-only safety: this only affects how <b>destination</b> (PC-side) paths are spelled. It never
/// touches the device or the AFC read path, so the read-only contract is unaffected.
/// </para>
/// <para>
/// The input is expected to be an absolute, separator-normalized path (e.g. the output of
/// <see cref="System.IO.Path.GetFullPath(string)"/>). A <c>\\?\</c> path is passed to the OS
/// verbatim, so forward slashes and relative segments are <i>not</i> normalized by Windows — callers
/// must hand in a clean, backslash-separated absolute path.
/// </para>
/// </remarks>
public static class LongPath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Returns <paramref name="path"/> rewritten with the Windows extended-length prefix when it is a
    /// fully-qualified Windows path, so it can exceed <c>MAX_PATH</c>. Returns the input unchanged on
    /// non-Windows platforms, for already-prefixed paths, and for paths that are not fully qualified.
    /// </summary>
    /// <param name="path">An absolute, normalized path (e.g. from <see cref="System.IO.Path.GetFullPath(string)"/>).</param>
    /// <returns>The extended-length form on Windows, otherwise the original path.</returns>
    public static string ToExtended(string path)
    {
        if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows())
        {
            return path;
        }

        path = path.Replace('/', '\\');

        // Already extended (covers both \\?\ and \\?\UNC\).
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path;
        }

        // A relative path cannot be safely prefixed — the OS would not normalize it.
        if (!Path.IsPathFullyQualified(path))
        {
            return path;
        }

        // UNC share: \\server\share\... -> \\?\UNC\server\share\...
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ExtendedUncPrefix + path[2..];
        }

        // Drive-rooted: C:\... -> \\?\C:\...
        return ExtendedPrefix + path;
    }
}
