namespace PicHarbor.Core.GooglePhotos;

/// <summary>A fixed selection prepared once for a Google Photos upload session.</summary>
public sealed class GooglePhotosUploadPlan
{
    internal GooglePhotosUploadPlan(IEnumerable<GooglePhotosPreparedFile> files, string? targetDirectory)
    {
        Files = Array.AsReadOnly(files.ToArray());
        TargetDirectory = targetDirectory;
        TotalBytes = Files.Sum(f => f.Size);
    }

    /// <summary>Fixed file selection and its verification evidence.</summary>
    public IReadOnlyList<GooglePhotosPreparedFile> Files { get; }
    /// <summary>Original external directory, retained for display only.</summary>
    public string? TargetDirectory { get; }
    /// <summary>Total size of the prepared files.</summary>
    public long TotalBytes { get; }

    /// <summary>Retains the original verification evidence when resuming or retrying a subset.</summary>
    public GooglePhotosUploadPlan SelectFiles(IEnumerable<string> paths)
    {
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subset = Files.Where(f => selected.Remove(f.Path)).ToArray();
        if (selected.Count != 0) throw new ArgumentException("Files do not belong to the prepared upload plan.", nameof(paths));
        return new(subset, TargetDirectory);
    }
}

/// <summary>Metadata and optional catalog digest associated with a verified file.</summary>
public sealed record GooglePhotosPreparedFile(string Path, long Size, long ModifiedUnixMilliseconds, string? Sha256);
