using System.Text.Json.Serialization;

namespace GetAndSee.Core.GooglePhotos;

/// <summary>
/// A raw progress event emitted by gpmc with --json-progress over stderr.
/// </summary>
public sealed class GooglePhotosProgressEvent
{
    /// <summary>Schema version of the progress event.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Event type discriminator (e.g. progress).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "progress";

    /// <summary>Operation being performed (e.g. upload).</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "upload";

    /// <summary>Current phase of operation (hashing, checking, uploading, completed, skipped, error).</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    /// <summary>Absolute path to the file on disk.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>File name.</summary>
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    /// <summary>Bytes transferred so far.</summary>
    [JsonPropertyName("bytes_completed")]
    public long BytesCompleted { get; set; }

    /// <summary>Total bytes of the file.</summary>
    [JsonPropertyName("bytes_total")]
    public long BytesTotal { get; set; }

    /// <summary>Error message if the operation failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Current retry attempt number (1-based).</summary>
    [JsonPropertyName("retry_attempt")]
    public int? RetryAttempt { get; set; }

    /// <summary>Maximum retry attempts.</summary>
    [JsonPropertyName("retry_max")]
    public int? RetryMax { get; set; }

    /// <summary>Backoff delay in seconds before this retry.</summary>
    [JsonPropertyName("retry_delay")]
    public double? RetryDelay { get; set; }
}

/// <summary>
/// Aggregated real-time progress snapshot reporting to the UI.
/// </summary>
public sealed record GooglePhotosProgressSnapshot(
    int TotalFiles,
    int UploadedFiles,
    int SkippedFiles,
    int FailedFiles,
    long TotalBytes,
    long UploadedBytes,
    double OverallPercent,
    double SpeedBytesPerSecond,
    string CurrentFile,
    string CurrentPhase,
    double CurrentFilePercent);

/// <summary>
/// Summary result of a completed Google Photos synchronization run.
/// </summary>
public sealed record GooglePhotosSyncResult(
    int UploadedCount,
    int SkippedCount,
    int FailedCount,
    long BytesUploaded,
    IReadOnlyDictionary<string, string> UploadedMediaKeys);

/// <summary>
/// Result of an authentication/connection test.
/// </summary>
public sealed record GooglePhotosAuthTestResult(
    bool Success,
    string? Email,
    string? ErrorMessage,
    string? ExchangedAuthData = null);

/// <summary>
/// Result of a network proxy connectivity test.
/// </summary>
public sealed record GooglePhotosProxyTestResult(
    bool Success,
    long LatencyMs,
    string Message,
    string? NormalizedProxy = null);
