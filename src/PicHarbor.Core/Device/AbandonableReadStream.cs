using System.Security.Cryptography;

namespace PicHarbor.Core.Device;

/// <summary>
/// Read-only <see cref="Stream"/> decorator that shields the copy loop from an orphaned device read so
/// the run-level <see cref="PicHarbor.Core.Transfer.ForwardProgressWatchdog"/> can stop a stuck transfer
/// cleanly (#11 / #25 / #42 / R2).
/// </summary>
/// <remarks>
/// <para>
/// The native AFC read is a blocking call with no timeout, dispatched onto a thread-pool worker. A cable
/// yank, a sleeping device, or a USB bus reset can make it park forever or pin a core and never return.
/// This decorator races each <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> against the supplied
/// cancellation token: when the run-level forward-progress watchdog observes that bytes have stopped
/// flowing and trips that token, the in-flight read is <i>abandoned</i> and an
/// <see cref="OperationCanceledException"/> is surfaced, so the caller is never blocked on the orphaned
/// read.
/// </para>
/// <para>
/// Unlike the Sprint 2 design, this decorator no longer owns its own inactivity timer. Liveness is a
/// single run-level concept driven by the byte heartbeat; this type only provides the two things the
/// watchdog needs to act on a stuck read: (1) an abandon-on-cancellation race so a cancel actually
/// unblocks the pinned read, and (2) a private reusable scratch buffer so an abandoned read that keeps
/// writing can never corrupt a pooled buffer already handed back to the caller's
/// <see cref="System.Buffers.ArrayPool{T}"/>.
/// </para>
/// <para>
/// On abandonment, disposal of the inner stream (which closes the native AFC handle) is scheduled for
/// whenever the orphaned read eventually returns or faults — so the handle is not leaked. If the read
/// never returns, the process is exiting anyway and the OS reclaims it.
/// </para>
/// </remarks>
internal sealed class AbandonableReadStream : Stream
{
    private readonly Stream inner;
    private readonly TimeProvider timeProvider;
    private byte[]? scratch;
    private int innerDisposed;
    private bool abandoned;

    /// <summary>Wraps <paramref name="inner"/> so its reads can be abandoned on cancellation.</summary>
    /// <param name="inner">The underlying read stream (owned and disposed by this decorator).</param>
    /// <param name="timeProvider">Time source passed through to the abandon race; defaults to <see cref="TimeProvider.System"/>.</param>
    public AbandonableReadStream(Stream inner, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("Abandonable read streams do not expose a length.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("Abandonable read streams are forward-only.");
        set => throw new NotSupportedException("Abandonable read streams are forward-only.");
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (buffer.Length == 0)
        {
            return 0;
        }

        // Read into a private, reusable scratch buffer rather than the caller's buffer. If the read
        // stalls and is abandoned, the still-running native read keeps writing into `scratch`; by not
        // sharing it with the caller (whose buffer may be a pooled array) we guarantee an orphaned
        // read can never corrupt a buffer that has been handed back to an ArrayPool. The scratch is
        // reused across reads so the hot path allocates nothing.
        byte[] target = RentScratch(buffer.Length);
        int length = buffer.Length;

        int bytesRead;
        try
        {
            // Race the read against cancellation only (no per-read timer — liveness is the run-level
            // watchdog's job). On a trip the run-level watchdog cancels the token; this abandons the read
            // and, via the abandon hook, closes the inner native handle once the orphaned read finally
            // returns (no leak).
            bytesRead = await DeviceWatchdog.RaceAgainstTimeoutAsync(
                inner.ReadAsync(target, 0, length, cancellationToken),
                Timeout.InfiniteTimeSpan,
                timeProvider,
                cancellationToken,
                onAbandoned: _ => DisposeInner()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The orphaned read may still be writing into the reusable scratch buffer; drop our
            // reference so it is never reused while in flight, and mark the stream abandoned so Dispose
            // leaves the inner handle to the watchdog's abandon hook (which closes it when that read
            // finally returns).
            abandoned = true;
            scratch = null;
            throw;
        }

        if (bytesRead > 0)
        {
            new ReadOnlySpan<byte>(target, 0, bytesRead).CopyTo(buffer.Span);

            // Don't let device bytes linger in the long-lived scratch buffer between reads. Zeroing the
            // region we just used keeps the reusable buffer clean at rest (defense in depth) so a later
            // read can never surface stale bytes from a previous file.
            CryptographicOperations.ZeroMemory(target.AsSpan(0, bytesRead));
        }

        return bytesRead;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Not supported: the copy pipeline reads asynchronously so reads can be raced against cancellation.
    /// A synchronous read would block on the inner native read with no way to abandon it on a disconnect
    /// (the very hang this exists to prevent), so it is disabled rather than silently falling through.
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException(
            "AbandonableReadStream is async-only; use ReadAsync so a stuck read can be abandoned on cancellation.");

    /// <inheritdoc />
    public override void Flush()
    {
        // Read-only stream: nothing to flush.
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Abandonable read streams are forward-only.");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("Abandonable read streams are read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Watchdog read streams are read-only.");

    private byte[] RentScratch(int length)
    {
        if (scratch is null || scratch.Length < length)
        {
            scratch = new byte[length];
        }

        return scratch;
    }

    private void DisposeInner()
    {
        if (Interlocked.Exchange(ref innerDisposed, 1) == 0)
        {
            inner.Dispose();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // If a read was abandoned, the continuation owns inner disposal — do not block here.
        if (disposing && !abandoned)
        {
            DisposeInner();
        }

        base.Dispose(disposing);
    }
}
