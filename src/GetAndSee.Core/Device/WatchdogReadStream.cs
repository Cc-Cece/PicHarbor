using GetAndSee.Core.Errors;

namespace GetAndSee.Core.Device;

/// <summary>
/// Read-only <see cref="Stream"/> decorator that enforces a per-read inactivity timeout — the
/// read-stall watchdog (#11 / R2).
/// </summary>
/// <remarks>
/// <para>
/// The native AFC read is a blocking call with no timeout: a cable yank, a sleeping device, or a USB
/// bus reset parks it forever (the Sprint 1 #11 hang). This decorator races each
/// <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> against a <see cref="TimeProvider"/>-based
/// delay. If no bytes arrive within the timeout, the read is abandoned and a
/// <see cref="DeviceStallException"/> is thrown so the run can stop cleanly and resumably.
/// </para>
/// <para>
/// On a stall the caller is never blocked waiting for the orphaned read. Instead, disposal of the
/// inner stream (which closes the native AFC handle) is scheduled for whenever that read eventually
/// returns or faults — so the handle is not leaked. If the read never returns, the process is exiting
/// anyway and the OS reclaims it.
/// </para>
/// <para>
/// The timeout and the <see cref="TimeProvider"/> are injectable so the watchdog can be unit-tested
/// against a mocked stalling stream — no cable yank required.
/// </para>
/// </remarks>
internal sealed class WatchdogReadStream : Stream
{
    private readonly Stream inner;
    private readonly TimeSpan timeout;
    private readonly TimeProvider timeProvider;
    private byte[]? scratch;
    private int innerDisposed;
    private bool abandoned;

    /// <summary>Wraps <paramref name="inner"/> with an inactivity watchdog.</summary>
    /// <param name="inner">The underlying read stream (owned and disposed by this decorator).</param>
    /// <param name="timeout">Maximum time a single read may produce no bytes before it is treated as a stall.</param>
    /// <param name="timeProvider">Time source for the watchdog; defaults to <see cref="TimeProvider.System"/>.</param>
    public WatchdogReadStream(Stream inner, TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Watchdog timeout must be positive.");
        }

        this.inner = inner;
        this.timeout = timeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("Watchdog read streams do not expose a length.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("Watchdog read streams are forward-only.");
        set => throw new NotSupportedException("Watchdog read streams are forward-only.");
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
        Task<int> readTask = inner.ReadAsync(target, 0, buffer.Length, cancellationToken);

        if (!readTask.IsCompleted)
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delayTask = Task.Delay(timeout, timeProvider, delayCts.Token);

            Task finished = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
            if (finished != readTask)
            {
                // The watchdog timer (or caller cancellation) won. Abandon the read and drop our
                // reference to the scratch buffer so it is never reused while the orphaned read may
                // still be writing into it.
                AbandonRead(readTask);
                scratch = null;
                cancellationToken.ThrowIfCancellationRequested();
                throw new DeviceStallException();
            }
        }

        int bytesRead = await readTask.ConfigureAwait(false);
        if (bytesRead > 0)
        {
            new ReadOnlySpan<byte>(target, 0, bytesRead).CopyTo(buffer.Span);
        }

        return bytesRead;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override void Flush()
    {
        // Read-only stream: nothing to flush.
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Watchdog read streams are forward-only.");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("Watchdog read streams are read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Watchdog read streams are read-only.");

    private void AbandonRead(Task<int> readTask)
    {
        abandoned = true;

        // When the orphaned read finally returns or faults, close the native handle so it is not
        // leaked. This never blocks the current operation.
        _ = readTask.ContinueWith(
            completed =>
            {
                _ = completed.Exception; // observe to avoid unobserved-exception escalation
                DisposeInner();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

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
