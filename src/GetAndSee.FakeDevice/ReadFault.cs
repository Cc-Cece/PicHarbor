namespace GetAndSee.FakeDevice;

/// <summary>
/// The kind of managed-observable read failure a <see cref="ScriptedReadStream"/> injects. These are the
/// failure shapes the copy pipeline can actually observe in managed code; the model deliberately does not
/// claim to reproduce the native <c>afc_file_close</c> core-pin (see <c>docs/sprint-3.5/done.md</c>).
/// </summary>
public enum ReadFaultKind
{
    /// <summary>No fault — deliver the file's full declared content, then end cleanly at EOF.</summary>
    None,

    /// <summary>Deliver the full content in many tiny chunks (still making forward progress, never stalling).</summary>
    Slow,

    /// <summary>Deliver up to the trigger offset, then block forever on a <b>real</b> <see cref="Task"/> (a parked native read; #11/#42).</summary>
    Park,

    /// <summary>Return zero bytes on the very first read — the <c>EmptyResponse</c>/0 a yank can surface before any content.</summary>
    Empty,

    /// <summary>Deliver up to the trigger offset, then return a premature zero-byte read short of the declared size.</summary>
    PrematureEof,

    /// <summary>Deliver up to the trigger offset, then throw a connection-fatal error (a cable-yank surfacing as a lost connection).</summary>
    ConnectionFatal,

    /// <summary>Deliver up to the trigger offset, then throw a single-file, non-connection-fatal device error.</summary>
    PerFileError,

    /// <summary>Deliver up to the trigger offset, return a premature EOF, then busy-spin on dispose — the #45 spinning-close shape.</summary>
    SpinOnDispose,
}

/// <summary>
/// A deterministic, scriptable read fault keyed by byte offset, injected by <see cref="ScriptedReadStream"/>.
/// One value describes how a single file's read stream misbehaves; the fake device attaches it per file so a
/// fault is reproducible by call + file + byte-offset with no wall-clock dependence.
/// </summary>
/// <remarks>
/// This one model folds in the three earlier ad-hoc fakes: a park (with or without cancellation observance)
/// replaces <c>ControlledReadStream</c> and <c>ChunkThenStallReadStream</c>; a premature EOF plus a spinning
/// dispose replaces <c>SpinOnDisposeStream</c>.
/// </remarks>
public sealed record ReadFault
{
    private ReadFault(ReadFaultKind kind, long faultAfterBytes, bool observeCancellation)
    {
        Kind = kind;
        FaultAfterBytes = faultAfterBytes;
        ObserveCancellation = observeCancellation;
    }

    /// <summary>The failure shape this fault injects.</summary>
    public ReadFaultKind Kind { get; }

    /// <summary>How many bytes of real content are delivered before the fault triggers (0 = fault on the first read).</summary>
    public long FaultAfterBytes { get; }

    /// <summary>
    /// For <see cref="ReadFaultKind.Park"/>: whether the parked read observes its cancellation token. A real
    /// native AFC read does <b>not</b> (the copy loop abandons it via the watchdog), so this defaults to
    /// <see langword="false"/>; set it <see langword="true"/> to model a cooperatively cancellable read.
    /// </summary>
    public bool ObserveCancellation { get; }

    /// <summary>Deliver the full content in tiny chunks — still progressing, so the watchdog never trips.</summary>
    public static ReadFault Slow { get; } = new(ReadFaultKind.Slow, 0, observeCancellation: false);

    /// <summary>Return zero bytes on the first read (empty/0-byte response before any content).</summary>
    public static ReadFault Empty { get; } = new(ReadFaultKind.Empty, 0, observeCancellation: false);

    /// <summary>Deliver <paramref name="bytes"/> of content, then block forever on a real Task (a parked read).</summary>
    /// <param name="bytes">Bytes delivered before the park (0 = park on the first read).</param>
    /// <param name="observeCancellation">Whether the parked read honors its cancellation token (default <see langword="false"/>).</param>
    public static ReadFault ParkAfter(long bytes, bool observeCancellation = false) =>
        new(ReadFaultKind.Park, bytes, observeCancellation);

    /// <summary>Deliver <paramref name="bytes"/> of content, then return a premature EOF short of the declared size.</summary>
    /// <param name="bytes">Bytes delivered before the premature EOF.</param>
    public static ReadFault PrematureEofAfter(long bytes) => new(ReadFaultKind.PrematureEof, bytes, observeCancellation: false);

    /// <summary>Deliver <paramref name="bytes"/> of content, then throw a connection-fatal device error.</summary>
    /// <param name="bytes">Bytes delivered before the connection drops (0 = throw on the first read).</param>
    public static ReadFault ConnectionFatalAfter(long bytes) => new(ReadFaultKind.ConnectionFatal, bytes, observeCancellation: false);

    /// <summary>Deliver <paramref name="bytes"/> of content, then throw a single-file, non-connection-fatal device error.</summary>
    /// <param name="bytes">Bytes delivered before the per-file error.</param>
    public static ReadFault PerFileErrorAfter(long bytes) => new(ReadFaultKind.PerFileError, bytes, observeCancellation: false);

    /// <summary>Deliver <paramref name="bytes"/> of content, return a premature EOF, then busy-spin on dispose (#45).</summary>
    /// <param name="bytes">Bytes delivered before the premature EOF and spinning close.</param>
    public static ReadFault SpinOnDisposeAfter(long bytes) => new(ReadFaultKind.SpinOnDispose, bytes, observeCancellation: false);
}
