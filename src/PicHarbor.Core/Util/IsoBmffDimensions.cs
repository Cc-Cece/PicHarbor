using System.Buffers.Binary;
using System.Text;

namespace PicHarbor.Core.Util;

/// <summary>
/// Reads the display size of an MP4 or QuickTime file from the track header.
/// Walks box sizes only, so a large media payload is never decoded.
/// </summary>
public static class IsoBmffDimensions
{
    /// <summary>Reads width and height from <paramref name="filePath"/>. Returns false when the header has no video track.</summary>
    public static bool TryReadDisplaySize(string filePath, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!TryReadDisplaySize(stream, out width, out height))
            {
                return false;
            }

            return width > 0 && height > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads width and height from a seekable MP4 or QuickTime stream.</summary>
    public static bool TryReadDisplaySize(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!stream.CanSeek || !stream.CanRead)
        {
            return false;
        }

        long end = stream.Length;
        return TryWalk(stream, 0, end, 0, ref width, ref height) && width > 0 && height > 0;
    }

    private static bool TryWalk(Stream stream, long start, long end, int depth, ref int width, ref int height)
    {
        if (depth > 6 || start < 0 || end > stream.Length || start >= end)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        long position = start;
        bool found = false;
        while (position + 8 <= end)
        {
            stream.Position = position;
            if (stream.Read(header[..8]) < 8)
            {
                break;
            }

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            int headerLength = 8;
            long size = size32;
            if (size32 == 1)
            {
                if (stream.Read(header[..8]) < 8)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header);
                headerLength = 16;
            }
            else if (size32 == 0)
            {
                size = end - position;
            }

            if (size < headerLength)
            {
                break;
            }

            long next = position + size;
            if (next <= position || next > end)
            {
                break;
            }

            long payload = position + headerLength;
            if (type is "moov" or "trak" or "mdia" or "minf" or "stbl")
            {
                if (TryWalk(stream, payload, next, depth + 1, ref width, ref height))
                {
                    found = true;
                }
            }
            else if (type == "tkhd" && TryReadTkhd(stream, payload, next - payload, out int trackWidth, out int trackHeight))
            {
                if ((long)trackWidth * trackHeight > (long)width * height)
                {
                    width = trackWidth;
                    height = trackHeight;
                    found = true;
                }
            }

            position = next;
        }

        return found;
    }

    private static bool TryReadTkhd(Stream stream, long payload, long length, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (length < 84)
        {
            return false;
        }

        Span<byte> versionByte = stackalloc byte[1];
        stream.Position = payload;
        if (stream.Read(versionByte) < 1)
        {
            return false;
        }

        int version = versionByte[0];
        int widthOffset = version == 1 ? 88 : 76;
        int matrixOffset = version == 1 ? 52 : 40;
        if (length < widthOffset + 8)
        {
            return false;
        }

        Span<byte> matrix = stackalloc byte[8];
        stream.Position = payload + matrixOffset;
        if (stream.Read(matrix) < 8)
        {
            return false;
        }

        int a = BinaryPrimitives.ReadInt32BigEndian(matrix);
        int b = BinaryPrimitives.ReadInt32BigEndian(matrix.Slice(4));
        stream.Position = payload + matrixOffset + 12;
        if (stream.Read(matrix) < 8)
        {
            return false;
        }

        int c = BinaryPrimitives.ReadInt32BigEndian(matrix);
        int d = BinaryPrimitives.ReadInt32BigEndian(matrix.Slice(4));

        Span<byte> sizeBytes = stackalloc byte[8];
        stream.Position = payload + widthOffset;
        if (stream.Read(sizeBytes) < 8)
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(sizeBytes) >> 16;
        height = BinaryPrimitives.ReadInt32BigEndian(sizeBytes.Slice(4)) >> 16;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // 90° and 270° store the rotation in the matrix and the unrotated sample size in the header.
        const int axis = 0x4000;
        bool swapped = Math.Abs(a) < axis && Math.Abs(d) < axis && (Math.Abs(b) >= axis || Math.Abs(c) >= axis);
        if (swapped)
        {
            (width, height) = (height, width);
        }

        return true;
    }
}
