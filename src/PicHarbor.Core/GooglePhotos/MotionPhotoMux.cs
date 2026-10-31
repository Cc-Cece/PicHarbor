namespace PicHarbor.Core.GooglePhotos;

/// <summary>Builds a Google motion-photo file without re-encoding the still image.</summary>
internal static class MotionPhotoMux
{
    private static readonly byte[] XmpUuid =
    [
        0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
        0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC,
    ];

    /// <summary>True when the file already carries a motion-photo marker.</summary>
    public static bool IsMotionContainer(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 16)
            {
                return false;
            }

            int head = (int)Math.Min(info.Length, 512 * 1024);
            if (ContainsMarker(path, 0, head))
            {
                return true;
            }

            if (info.Length > head)
            {
                long tail = Math.Min(64 * 1024, info.Length);
                return ContainsMarker(path, info.Length - tail, (int)tail);
            }

            return false;
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

    /// <summary>Writes a motion-photo file next to <paramref name="directory"/> and returns its path.</summary>
    public static string Package(string stillPath, string videoPath, string directory, long presentationTimestampUs = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stillPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        Directory.CreateDirectory(directory);
        byte[] still = File.ReadAllBytes(stillPath);
        byte[] video = File.ReadAllBytes(videoPath);
        if (video.Length == 0)
        {
            throw new InvalidOperationException("实况视频是空的，无法封装动态照片。");
        }

        bool jpeg = still.Length > 2 && still[0] == 0xFF && still[1] == 0xD8;
        bool heic = IsHeif(still);
        if (!jpeg && !heic)
        {
            throw new InvalidOperationException("这张实况照片不是 JPEG 或 HEIC，无法封装动态照片。可改用原始照片配对。");
        }

        if (heic && video.LongLength > uint.MaxValue - 8)
        {
            throw new InvalidOperationException("实况视频过大，无法封装动态照片。可改用原始照片配对。");
        }

        string mime = jpeg ? "image/jpeg" : "image/heic";
        string videoMime = Path.GetExtension(videoPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? "video/mp4"
            : "video/quicktime";
        string xmp = Xmp(mime, videoMime, video.LongLength, presentationTimestampUs, heic);
        byte[] image = jpeg ? InsertJpegXmp(still, xmp) : InsertHeicXmp(still, xmp);
        string extension = jpeg ? ".jpg" : ".heic";
        string output = Path.Combine(directory, Guid.NewGuid().ToString("N") + "_MP" + extension);
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(image);
            if (heic)
            {
                uint marker = (uint)video.LongLength + 8;
                Span<byte> header = stackalloc byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, marker);
                header[4] = (byte)'m';
                header[5] = (byte)'p';
                header[6] = (byte)'v';
                header[7] = (byte)'d';
                stream.Write(header);
            }

            stream.Write(video);
        }

        return output;
    }

    private static bool ContainsMarker(string path, long offset, int count)
    {
        byte[] buffer = new byte[count];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        int read = stream.Read(buffer, 0, count);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, read).Contains("MotionPhoto", StringComparison.Ordinal);
    }

    private static string Xmp(string mime, string videoMime, long videoLength, long timestampUs, bool heic)
    {
        int padding = heic ? 8 : 0;
        return $"""
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description xmlns:Camera="http://ns.google.com/photos/1.0/camera/" xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/" Camera:MotionPhoto="1" Camera:MotionPhotoVersion="1" Camera:MotionPhotoPresentationTimestampUs="{timestampUs}"><Container:Directory><rdf:Seq><rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="{mime}" Item:Semantic="Primary" Item:Padding="{padding}"/></rdf:li><rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="{videoMime}" Item:Semantic="MotionPhoto" Item:Length="{videoLength}"/></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>
            """;
    }

    private static byte[] InsertJpegXmp(byte[] jpeg, string xmp)
    {
        byte[] packet = System.Text.Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmp);
        if (packet.Length + 2 > ushort.MaxValue)
        {
            throw new InvalidOperationException("动态照片描述过长。");
        }

        int insertAt = 2;
        if (jpeg.Length > 4 && jpeg[2] == 0xFF && jpeg[3] == 0xE0)
        {
            int length = (jpeg[4] << 8) + jpeg[5];
            insertAt = 4 + length;
        }

        byte[] segment = new byte[packet.Length + 4];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)((packet.Length + 2) >> 8);
        segment[3] = (byte)(packet.Length + 2);
        packet.CopyTo(segment, 4);
        byte[] output = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, insertAt).CopyTo(output);
        segment.CopyTo(output, insertAt);
        jpeg.AsSpan(insertAt).CopyTo(output.AsSpan(insertAt + segment.Length));
        return output;
    }

    private static bool IsHeif(byte[] data)
    {
        if (data.Length < 12)
        {
            return false;
        }

        string brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
        return brand is "heic" or "heix" or "mif1" or "heif" or "avif";
    }

    private static byte[] InsertHeicXmp(byte[] heic, string xmp)
    {
        if (heic.Length < 8)
        {
            throw new InvalidOperationException("HEIC 文件不完整，无法封装动态照片。");
        }

        long boxSize = ReadBoxSize(heic, 0, out int header);
        if (boxSize < header || boxSize > heic.Length)
        {
            throw new InvalidOperationException("HEIC 文件结构无法识别，可改用原始照片配对。");
        }

        byte[] xmpBytes = System.Text.Encoding.UTF8.GetBytes(xmp);
        byte[] uuid = new byte[8 + XmpUuid.Length + xmpBytes.Length];
        int size = uuid.Length;
        uuid[0] = (byte)(size >> 24);
        uuid[1] = (byte)(size >> 16);
        uuid[2] = (byte)(size >> 8);
        uuid[3] = (byte)size;
        uuid[4] = (byte)'u';
        uuid[5] = (byte)'u';
        uuid[6] = (byte)'i';
        uuid[7] = (byte)'d';
        XmpUuid.CopyTo(uuid, 8);
        xmpBytes.CopyTo(uuid, 8 + XmpUuid.Length);
        byte[] output = new byte[heic.Length + uuid.Length];
        int split = (int)boxSize;
        heic.AsSpan(0, split).CopyTo(output);
        uuid.CopyTo(output, split);
        heic.AsSpan(split).CopyTo(output.AsSpan(split + uuid.Length));
        return output;
    }

    private static long ReadBoxSize(byte[] data, int offset, out int headerLength)
    {
        uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        headerLength = 8;
        if (size == 1)
        {
            headerLength = 16;
            return (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
        }

        return size == 0 ? data.Length - offset : size;
    }
}
