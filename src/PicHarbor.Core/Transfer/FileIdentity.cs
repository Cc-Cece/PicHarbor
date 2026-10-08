namespace PicHarbor.Core.Transfer;

/// <summary>
/// Encapsulates metadata identifying a file for equivalence checks.
/// </summary>
public readonly record struct FileIdentity(
    string Path,
    long Size,
    DateTimeOffset? LastModified = null,
    string? Sha256 = null);
