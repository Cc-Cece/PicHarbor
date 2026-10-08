using iMobileDevice.Afc;
using PicHarbor.Core.Errors;
using PicHarbor.Core.Util;

namespace PicHarbor.Core.Device;

/// <summary>
/// Forward-only, read-only <see cref="Stream"/> over an open AFC file handle.
/// Wraps <c>afc_file_read</c> and closes the handle (<c>afc_file_close</c>) on dispose.
/// </summary>
internal sealed class AfcReadStream : Stream
{
    private readonly IAfcApi afc;
    private readonly AfcClientHandle client;
    private readonly string path;
    private readonly ulong handle;
    private long position;
    private bool closed;

    public AfcReadStream(IAfcApi afc, AfcClientHandle client, ulong handle, string path)
    {
        this.afc = afc;
        this.client = client;
        this.handle = handle;
        this.path = path;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("AFC read streams do not expose a length.");

    public override long Position
    {
        get => position;
        set => throw new NotSupportedException("AFC read streams are forward-only.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
        {
            throw new ArgumentException("The sum of offset and count is larger than the buffer length.");
        }

        if (count == 0)
        {
            return 0;
        }

        // afc_file_read fills the supplied array from index 0. When the caller wants the bytes at a
        // non-zero offset, read into a scratch array and copy across.
        byte[] target = offset == 0 ? buffer : new byte[count];
        uint bytesRead = 0;
        long startTimestamp = ReadDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        AfcError error = afc.afc_file_read(client, handle, target, (uint)count, ref bytesRead);
        if (ReadDiagnostics.Enabled)
        {
            ReadDiagnostics.LogRead(
                path, error, count, bytesRead,
                System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp), position + bytesRead);
        }

        if (error != AfcError.Success)
        {
            throw AfcErrors.ToException(error, $"Error reading from \"{path}\" on the device: {error}.");
        }

        if (bytesRead == 0)
        {
            return 0;
        }

        if (offset != 0)
        {
            Array.Copy(target, 0, buffer, offset, (int)bytesRead);
        }

        position += bytesRead;
        return (int)bytesRead;
    }

    public override void Flush()
    {
        // Read-only stream: nothing to flush.
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("AFC read streams are forward-only.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("AFC read streams are read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("AFC read streams are read-only.");

    protected override void Dispose(bool disposing)
    {
        if (!closed)
        {
            closed = true;
            afc.afc_file_close(client, handle);
        }

        base.Dispose(disposing);
    }
}
