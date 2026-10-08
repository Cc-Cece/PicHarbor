using PicHarbor.Core.Errors;

namespace PicHarbor.FakeDevice;

/// <summary>
/// The one fault-scriptable, read-only device stream the fake device hands back from
/// <c>OpenReadAsync</c>. It delivers deterministic <see cref="FakeContent"/> and injects a single
/// <see cref="ReadFault"/> keyed by byte offset, folding the three earlier ad-hoc fakes
/// (<c>ControlledReadStream</c>, <c>ChunkThenStallReadStream</c>, <c>SpinOnDisposeStream</c>) into one model
/// while preserving their exact park / spin repros.
/// </summary>
/// <remarks>
/// <para>
/// A park is a <b>real</b> blocking <see cref="Task"/> (not a cancellation token that resolves immediately),
/// and a spinning close is a real <see cref="SpinWait"/> busy-loop in <see cref="Dispose(bool)"/> — so the
/// stream exercises the production watchdog/escape-hatch the way the native faults do, not a convenient
/// stand-in. The stream is async-only: a synchronous <see cref="Read(byte[], int, int)"/> is refused so a
/// stuck read can always be abandoned on cancellation, exactly like the shipped read path.
/// </para>
/// <para>
/// All time is driven by the caller's <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/> via
/// the watchdog; this stream itself introduces no real delays, so every scenario is deterministic.
/// </para>
/// </remarks>
public sealed class ScriptedReadStream : Stream
{
    private const int SlowChunkBytes = 64 * 1024;

    private readonly int contentSeed;
    private readonly long declaredSize;
    private readonly ReadFault? fault;
    private readonly string sourcePath;

    private readonly TaskCompletionSource<int> parkGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource parkedReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposeSpinStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long position;
    private bool spinArmed;
    private volatile bool spinReleased;

    /// <summary>Creates a scripted read stream over a file's deterministic content.</summary>
    /// <param name="contentSeed">The file's <see cref="FakeContent"/> seed.</param>
    /// <param name="declaredSize">The size AFC reports for the file (what the copier verifies against).</param>
    /// <param name="fault">The fault to inject, or <see langword="null"/> for a faithful full read.</param>
    /// <param name="sourcePath">The device path, used only in injected error messages.</param>
    public ScriptedReadStream(int contentSeed, long declaredSize, ReadFault? fault = null, string sourcePath = "/DCIM/fake")
    {
        this.contentSeed = contentSeed;
        this.declaredSize = declaredSize;
        this.fault = fault;
        this.sourcePath = sourcePath;
    }

    /// <summary>Completes once a <see cref="ReadFaultKind.Park"/> read has begun blocking.</summary>
    public Task ParkedReadStarted => parkedReadStarted.Task;

    /// <summary>Completes once a <see cref="ReadFaultKind.SpinOnDispose"/> dispose has begun busy-spinning.</summary>
    public Task DisposeSpinStarted => disposeSpinStarted.Task;

    /// <summary>True once the stream has been disposed (the analogue of the native file handle being closed).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Releases a parked read, completing it with <paramref name="bytes"/> delivered bytes (default 0, i.e.
    /// the orphaned read finally returning empty so a test's teardown can unwind it).
    /// </summary>
    /// <param name="bytes">Number of bytes the released read returns.</param>
    public void ReleasePark(int bytes = 0) => parkGate.TrySetResult(bytes);

    /// <summary>Releases a spinning dispose — the test's analogue of the OS reaping the pinned thread on terminate.</summary>
    public void ReleaseSpin() => spinReleased = true;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => declaredSize;

    /// <inheritdoc />
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException("Scripted read streams are forward-only.");
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>Refused: a synchronous read would block in native code with no way to abandon it on a disconnect.</remarks>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("ScriptedReadStream is async-only; use ReadAsync so a stuck read can be abandoned.");

    /// <inheritdoc />
    public override void Flush()
    {
        // Read-only stream: nothing to flush.
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Scripted read streams are read-only.");

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0)
        {
            return 0;
        }

        long remaining = declaredSize - position;
        ReadFaultKind kind = fault?.Kind ?? ReadFaultKind.None;
        long faultAfter = fault?.FaultAfterBytes ?? 0;

        switch (kind)
        {
            case ReadFaultKind.Empty:
                return 0;

            case ReadFaultKind.Slow:
                return Deliver(buffer.Span, Math.Min(remaining, SlowChunkBytes));

            case ReadFaultKind.Park:
            case ReadFaultKind.PrematureEof:
            case ReadFaultKind.ConnectionFatal:
            case ReadFaultKind.PerFileError:
            case ReadFaultKind.SpinOnDispose:
                if (position < faultAfter)
                {
                    return Deliver(buffer.Span, Math.Min(faultAfter - position, remaining));
                }

                return await TriggerFaultAsync(kind, buffer, cancellationToken).ConfigureAwait(false);

            default:
                return Deliver(buffer.Span, remaining);
        }
    }

    private async ValueTask<int> TriggerFaultAsync(ReadFaultKind kind, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case ReadFaultKind.Park:
                return await ParkAsync(buffer, cancellationToken).ConfigureAwait(false);

            case ReadFaultKind.ConnectionFatal:
                throw new DeviceConnectionLostException();

            case ReadFaultKind.PerFileError:
                throw new DeviceException($"Could not read \"{sourcePath}\" from the device: a single-file read error.");

            case ReadFaultKind.SpinOnDispose:
                spinArmed = true; // premature EOF now; the busy-spin happens in Dispose.
                return 0;

            default: // PrematureEof
                return 0;
        }
    }

    private async ValueTask<int> ParkAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        parkedReadStarted.TrySetResult();

        bool observeCancellation = fault?.ObserveCancellation ?? false;
        int released;
        if (observeCancellation)
        {
            await using CancellationTokenRegistration registration =
                cancellationToken.Register(static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(), parkGate);
            released = await parkGate.Task.ConfigureAwait(false);
        }
        else
        {
            // Ignore cancellation — model the real native read that keeps running after the copy loop abandons it.
            released = await parkGate.Task.ConfigureAwait(false);
        }

        if (released > 0)
        {
            Deliver(buffer.Span, released);
        }

        return released;
    }

    private int Deliver(Span<byte> buffer, long cap)
    {
        int count = (int)Math.Min(buffer.Length, Math.Max(0, cap));
        if (count <= 0)
        {
            return 0;
        }

        FakeContent.Fill(buffer[..count], position, contentSeed);
        position += count;
        return count;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;

        if (spinArmed && !spinReleased)
        {
            disposeSpinStarted.TrySetResult();
            SpinWait spinner = default;
            while (!spinReleased)
            {
                spinner.SpinOnce();
            }
        }

        base.Dispose(disposing);
    }
}
