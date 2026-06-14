namespace GetAndSee.Tests.TestSupport;

/// <summary>
/// A read stream that delivers a few chunks and then <b>stops</b> mid-file: the later read never returns
/// and ignores its cancellation token, modeling the real <c>afc_file_read</c> after a cable yank (#42 —
/// bytes were flowing, then forward progress froze). It is the intra-file manifestation, distinct from a
/// read that parks from the very first byte.
/// </summary>
internal sealed class ChunkThenStallReadStream : Stream
{
    private readonly int chunkSize;
    private int chunksRemaining;
    private readonly TaskCompletionSource<int> stallGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stalledReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a stream that returns <paramref name="chunksBeforeStall"/> chunks then stalls.</summary>
    /// <param name="chunksBeforeStall">How many chunks to deliver before forward progress stops.</param>
    /// <param name="chunkSize">The size of each delivered chunk, in bytes.</param>
    public ChunkThenStallReadStream(int chunksBeforeStall, int chunkSize)
    {
        chunksRemaining = chunksBeforeStall;
        this.chunkSize = chunkSize;
    }

    /// <summary>Completes once the stalling read (after the initial chunks) has begun.</summary>
    public Task StalledReadStarted => stalledReadStarted.Task;

    /// <summary>Releases the stalled read so an abandoned orphan can finally unwind in a test's teardown.</summary>
    public void ReleaseStall() => stallGate.TrySetResult(0);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (chunksRemaining > 0)
        {
            chunksRemaining--;
            int bytes = Math.Min(chunkSize, count);
            buffer.AsSpan(offset, bytes).Fill(1);
            return Task.FromResult(bytes);
        }

        // Forward byte-progress stops here. The native read ignores cancellation and never returns on its
        // own; only the run-level watchdog abandoning it lets the copy loop unwind.
        stalledReadStarted.TrySetResult();
        return stallGate.Task;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
