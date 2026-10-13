namespace PicHarbor.Core.Storage;

/// <summary>Resolves archive-relative paths without following directory junctions or escaping the archive.</summary>
public static class ArchivePathResolver
{
    /// <summary>Returns an absolute path, or throws when the path is unsafe.</summary>
    public static string Resolve(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') ||
            relativePath.Split(['/', '\\']).Any(p => p is ".." or "."))
            throw new InvalidDataException("The archive contains an unsafe relative path.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The path escapes the archive.");
        CheckReparsePoints(fullRoot);
        string current = fullRoot;
        foreach (string part in Path.GetRelativePath(fullRoot, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            CheckReparsePoints(current);
        }
        return full;
    }

    private static void CheckReparsePoints(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Archive paths cannot traverse symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = current.Parent;
        }
    }
}
