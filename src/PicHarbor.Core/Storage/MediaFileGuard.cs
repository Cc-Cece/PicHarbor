using PicHarbor.Core.Journal;

namespace PicHarbor.Core.Storage;

/// <summary>Checks the selected physical content before previews and outgoing transfers.</summary>
public static class MediaFileGuard
{
    /// <summary>Refuses unavailable or replaced files; never substitutes another location silently.</summary>
    public static bool TryVerify(ManifestEntry media,string defaultRoot,out string path,CancellationToken ct=default)
    {
        path=string.Empty;
        if(media.Availability!=ReplicaState.Available) return false;
        try
        {
            path=ArchivePathResolver.Resolve(media.ArchiveRoot ?? defaultRoot,media.DestPath);
            var file=new FileInfo(path);
            return file.Exists && file.Length==media.SizeBytes &&
                (media.MediaId is null || LibraryMaintenanceService.Digest(path,ct:ct)==media.MediaId);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
