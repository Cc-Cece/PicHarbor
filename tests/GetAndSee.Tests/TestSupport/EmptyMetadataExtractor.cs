using GetAndSee.Core.Organize;

namespace GetAndSee.Tests.TestSupport;

/// <summary>
/// An <see cref="IMediaMetadataExtractor"/> that always returns <see cref="MediaMetadata.Empty"/>, so the
/// real <see cref="DateFolderOrganizer"/> falls back to the file's <c>st_mtime</c> (the spec's capture date)
/// to choose the date folder. This keeps the harness's organize assertions driven by the spec without
/// embedding real EXIF bytes — EXIF parsing itself stays covered by the dedicated
/// <c>ExifMetadataExtractor</c> fixture tests.
/// </summary>
public sealed class EmptyMetadataExtractor : IMediaMetadataExtractor
{
    /// <inheritdoc />
    public MediaMetadata Extract(string filePath) => MediaMetadata.Empty;
}
