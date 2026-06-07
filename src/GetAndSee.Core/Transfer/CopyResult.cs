using GetAndSee.Core.Device;

namespace GetAndSee.Core.Transfer;

/// <summary>Outcome of attempting to copy one file.</summary>
public enum CopyStatus
{
    /// <summary>The file was streamed, verified, and moved into place this run.</summary>
    Copied,

    /// <summary>The file was already <c>done</c> in the journal and was skipped.</summary>
    Skipped,

    /// <summary>The copy failed; the journal row is marked <c>failed</c> and the file can be retried.</summary>
    Failed,
}

/// <summary>Result of a single-file copy attempt.</summary>
/// <param name="Status">What happened.</param>
/// <param name="File">The source file.</param>
/// <param name="RelativeDestPath">The destination path relative to the root, when copied; otherwise <see langword="null"/>.</param>
/// <param name="BytesCopied">Bytes written this run (0 for skipped/failed).</param>
/// <param name="Error">Failure message when <see cref="Status"/> is <see cref="CopyStatus.Failed"/>.</param>
/// <param name="Sha256">
/// Lowercase hex SHA-256 of the copied bytes when <c>--verify-hash</c> was enabled and the file was
/// copied this run; otherwise <see langword="null"/> (R12).
/// </param>
public sealed record CopyResult(
    CopyStatus Status,
    RemoteFile File,
    string? RelativeDestPath,
    long BytesCopied,
    string? Error,
    string? Sha256 = null);
