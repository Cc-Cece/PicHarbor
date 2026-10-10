using System.Buffers.Binary;
using System.Text;
using PicHarbor.Core.Util;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Util;

public sealed class IsoBmffDimensionsTests
{
    [Fact]
    public void Reads_track_header_and_swaps_axes_when_the_matrix_is_rotated()
    {
        byte[] file = Box("moov", Box("trak", Box("tkhd", TrackHeader(width: 1920, height: 1080, rotate: true))));

        using var stream = new MemoryStream(file);
        IsoBmffDimensions.TryReadDisplaySize(stream, out int width, out int height).ShouldBeTrue();
        width.ShouldBe(1080);
        height.ShouldBe(1920);
    }

    [Fact]
    public void Skips_a_leading_media_box_and_ignores_a_zero_size_audio_track()
    {
        byte[] file = Concat(
            Box("mdat", new byte[32]),
            Box("moov", Concat(
                Box("trak", Box("tkhd", TrackHeader(width: 0, height: 0, rotate: false))),
                Box("trak", Box("tkhd", TrackHeader(width: 1280, height: 720, rotate: false))))));

        using var stream = new MemoryStream(file);
        IsoBmffDimensions.TryReadDisplaySize(stream, out int width, out int height).ShouldBeTrue();
        width.ShouldBe(1280);
        height.ShouldBe(720);
    }

    private static byte[] TrackHeader(int width, int height, bool rotate)
    {
        byte[] payload = new byte[84];
        WriteFixed(payload, 40, rotate ? 0 : 1);
        WriteFixed(payload, 44, rotate ? 1 : 0);
        WriteFixed(payload, 52, rotate ? -1 : 0);
        WriteFixed(payload, 56, rotate ? 0 : 1);
        WriteFixed(payload, 72, 1);
        WriteFixed(payload, 76, width);
        WriteFixed(payload, 80, height);
        return payload;
    }

    private static void WriteFixed(byte[] buffer, int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), value << 16);
    }

    private static byte[] Box(string type, byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box.AsSpan(4));
        payload.CopyTo(box.AsSpan(8));
        return box;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] all = new byte[parts.Sum(part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(all.AsSpan(offset));
            offset += part.Length;
        }

        return all;
    }
}
