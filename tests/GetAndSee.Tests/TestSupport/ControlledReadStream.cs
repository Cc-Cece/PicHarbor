namespace GetAndSee.Tests.TestSupport;

/// <summary>
/// A read stream whose single read completes only when the test explicitly releases it — used to
/// simulate a device that stops sending bytes (a stall) without any hardware. The gate is created up
/// front so a test may release before or after the read starts, with no race.
/// </summary>
internal sealed class ControlledReadStream : Stream
{
    private readonly TaskCompletionSource<int> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once the stream has been disposed (used to assert the native handle is closed).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Completes the read with <paramref name="bytes"/> bytes.</summary>
    public void Release(int bytes) => gate.TrySetResult(bytes);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        AwaitGateAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(AwaitGateAsync(buffer, cancellationToken));

    public override int Read(byte[] buffer, int offset, int count) =>
        AwaitGateAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => 0; set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }

    private async Task<int> AwaitGateAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await using CancellationTokenRegistration registration =
            cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));

        int bytes = await gate.Task.ConfigureAwait(false);
        buffer.Span[..bytes].Fill(1);
        return bytes;
    }
}
