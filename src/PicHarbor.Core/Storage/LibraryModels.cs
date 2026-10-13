namespace PicHarbor.Core.Storage;

/// <summary>Portable logical library and physical member identities.</summary>
public sealed record LibraryIdentity(string LibraryId, string StorageId);

/// <summary>Device source identity kept separately from physical media locations.</summary>
public sealed record SourceIdentity(string DeviceId,string SourcePath,long Size,DateTimeOffset? ModifiedAt,string MediaId);

/// <summary>Availability of one physical copy.</summary>
public enum ReplicaState
{
    /// <summary>Accessible file.</summary>
    Available,
    /// <summary>Member is disconnected.</summary>
    Offline,
    /// <summary>Member cannot currently be accessed.</summary>
    Inaccessible,
    /// <summary>Accessible member no longer has the file.</summary>
    Missing,
    /// <summary>Content differs from its previous digest.</summary>
    Changed,
    /// <summary>Recoverable redundant copy.</summary>
    Recycled
}

/// <summary>A portable archive member.</summary>
public sealed record LibraryMember(string LibraryId, string StorageId, string Root, bool Included = true);

/// <summary>A media location preserving its own archive root.</summary>
public sealed record LibraryMedia(string MediaId, string StorageId, string Root, string RelativePath,
    long Size, string? Sha256, ReplicaState State, DateTimeOffset? CapturedAt = null)
{
    /// <summary>First successful archival time, distinct from the photo's capture date.</summary>
    public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>Outcomes of an archive self-check.</summary>
public sealed record LibraryCheckResult(int Checked, int Recycled, int Purged, IReadOnlyList<string> Errors);

/// <summary>A recoverable redundant copy with a stable operation identity.</summary>
public sealed record RecycledCopy(string Root, string OperationId, string RelativePath, DateTimeOffset RecycledAt);
