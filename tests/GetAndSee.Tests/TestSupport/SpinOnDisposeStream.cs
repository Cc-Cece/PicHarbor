namespace GetAndSee.Tests.TestSupport;

/// <summary>
/// Models the #45 spinning unwind: a read stream that delivers a few chunks (forward byte progress), then
/// returns a premature zero-byte read (the yanked-cable <c>EmptyResponse</c>/0), and whose
/// <see cref="Dispose(bool)"/> then <b>busy-spins</b> — exactly like the synchronous native
/// <c>afc_file_close</c> pinning a core on a dead transport during the <c>await using</c> disposal. Only
/// process termination (or, in a test, <see cref="ReleaseSpin"/>) ends the spin; cancellation cannot,
/// because it is a synchronous native call, not an awaitable. This is the spinning <i>close</i>, not a
/// cancellable read stall — the exact shape a stall double or a cancellation-ignoring read fails to model.
/// </summary>
internal sealed class SpinOnDisposeStream : Stream
{
    private readonly int chunkSize;
    private int chunksRemaining;
    private readonly TaskCompletionSource disposeSpinStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool released;

    /// <summary>Creates a stream that returns <paramref name="chunksBeforeEof"/> chunks then a spinning close.</summary>
    /// <param name="chunksBeforeEof">How many chunks to deliver (as forward progress) before the premature EOF.</param>
    /// <param name="chunkSize">The size of each delivered chunk, in bytes.</param>
    public SpinOnDisposeStream(int chunksBeforeEof, int chunkSize)
    {
        chunksRemaining = chunksBeforeEof;
        this.chunkSize = chunkSize;
    }

    /// <summary>Completes once <see cref="Dispose(bool)"/> has begun busy-spinning (the close hang is live).</summary>
    public Task DisposeSpinStarted => disposeSpinStarted.Task;

    /// <summary>Releases the spinning close — the test's analogue of the OS reaping the thread on terminate.</summary>
    public void ReleaseSpin() => released = true;

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (chunksRemaining > 0)
        {
            chunksRemaining--;
            int bytes = Math.Min(chunkSize, count);
            buffer.AsSpan(offset, bytes).Fill(1);
            return Task.FromResult(bytes);
        }

        // Premature EOF: the device returns Success+0 / EmptyResponse after the yank. The fault unwinds into
        // disposal below, where the real afc_file_close busy-spins on the dead transport.
        return Task.FromResult(0);
    }

    protected override void Dispose(bool disposing)
    {
        disposeSpinStarted.TrySetResult();
        SpinWait spinner = default;
        while (!released)
        {
            spinner.SpinOnce();
        }

        base.Dispose(disposing);
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
