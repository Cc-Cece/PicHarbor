namespace PicHarbor.Core.Journal;

/// <summary>Lifecycle state of a single file in the transfer journal.</summary>
public enum FileState
{
    /// <summary>Enumerated and planned, not yet started.</summary>
    Pending,

    /// <summary>Copy in flight (a <c>.partial</c> may exist on disk).</summary>
    InProgress,

    /// <summary>Copied, size-verified, and renamed to its final path.</summary>
    Done,

    /// <summary>Copy failed; safe to retry on the next run.</summary>
    Failed,
    /// <summary>Member is accessible but its physical copy is confirmed missing.</summary>
    Missing,
    /// <summary>Source content was verified in another registered member; no local physical copy is claimed.</summary>
    ExternallyVerified,
}

/// <summary>Maps <see cref="FileState"/> to and from its persisted text form.</summary>
internal static class FileStateText
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Failed = "failed";

    public static string ToText(this FileState state) => state switch
    {
        FileState.Pending => Pending,
        FileState.InProgress => InProgress,
        FileState.Done => Done,
        FileState.Failed => Failed,
        FileState.Missing => "missing",
        FileState.ExternallyVerified => "external",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown file state."),
    };

    public static FileState? FromText(string? text) => text switch
    {
        Pending => FileState.Pending,
        InProgress => FileState.InProgress,
        Done => FileState.Done,
        Failed => FileState.Failed,
        "missing" => FileState.Missing,
        "external" => FileState.ExternallyVerified,
        _ => null,
    };
}
