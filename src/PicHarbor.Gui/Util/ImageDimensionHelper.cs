using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace PicHarbor.Gui.Util;

/// <summary>
/// Fast image and video dimension reader that inspects file headers without decoding full pixel buffers.
/// Accounts for EXIF orientation (tags 5, 6, 7, 8) to ensure portrait photos have correct aspect ratios.
/// Also pairs Live Photo MOV videos with their counterpart still photos for instant dimension matching.
/// </summary>
public static class ImageDimensionHelper
{
    private static readonly ConcurrentDictionary<string, (int Width, int Height)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static (int Width, int Height)? GetDimensions(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        if (Cache.TryGetValue(filePath, out var cached))
            return cached;

        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            // Video types: MOV, MP4, MKV, AVI
            if (ext is ".mov" or ".mp4" or ".m4v" or ".avi" or ".mkv" or ".3gp")
            {
                // Check if this is a Live Photo video with an existing still photo
                string? stillPath = FindLivePhotoStill(filePath);
                if (!string.IsNullOrEmpty(stillPath))
                {
                    var stillDims = GetDimensions(stillPath);
                    if (stillDims.HasValue)
                    {
                        Cache[filePath] = stillDims.Value;
                        return stillDims.Value;
                    }
                }

                // Standalone video: extract dimensions from shell thumbnail
                var thumb = ShellServices.GetShellThumbnail(filePath, 240, 240, thumbnailOnly: false);
                if (thumb is BitmapSource bs && bs.PixelWidth > 0 && bs.PixelHeight > 0)
                {
                    var videoDims = (bs.PixelWidth, bs.PixelHeight);
                    Cache[filePath] = videoDims;
                    return videoDims;
                }

                return null;
            }

            (int Width, int Height)? dims = ext switch
            {
                ".png" => GetPngDimensions(filePath),
                ".webp" => GetWebpDimensions(filePath),
                _ => GetWpfDecoderDimensions(filePath)
            };

            if (dims.HasValue && dims.Value.Width > 0 && dims.Value.Height > 0)
            {
                if (Cache.Count < 5000)
                {
                    Cache[filePath] = dims.Value;
                }
                return dims.Value;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// For a video file (e.g. IMG_1234.MOV), finds a companion still image (e.g. IMG_1234.HEIC or IMG_1234.JPG).
    /// </summary>
    public static string? FindLivePhotoStill(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return null;
        try
        {
            string dir = Path.GetDirectoryName(videoPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return null;

            string[] candidates = [".heic", ".HEIC", ".jpg", ".JPG", ".jpeg", ".JPEG", ".png", ".PNG"];
            foreach (var ext in candidates)
            {
                string candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// For a still photo (e.g. IMG_1234.HEIC), finds a companion video (e.g. IMG_1234.MOV or IMG_1234.MP4).
    /// </summary>
    public static string? FindLivePhotoVideo(string photoPath)
    {
        if (string.IsNullOrWhiteSpace(photoPath)) return null;
        try
        {
            string dir = Path.GetDirectoryName(photoPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(photoPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return null;

            string[] candidates = [".mov", ".MOV", ".mp4", ".MP4"];
            foreach (var ext in candidates)
            {
                string candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch { }
        return null;
    }

    private static (int Width, int Height)? GetPngDimensions(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[24];
            if (fs.Read(header) == 24)
            {
                // PNG signature: 89 50 4E 47 0D 0A 1A 0A
                if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                {
                    int width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
                    int height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
                    if (width > 0 && height > 0)
                        return (width, height);
                }
            }
        }
        catch { }
        return null;
    }

    private static (int Width, int Height)? GetWebpDimensions(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[30];
            if (fs.Read(header) >= 30)
            {
                if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                    header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                {
                    if (header[12] == 0x56 && header[13] == 0x50 && header[14] == 0x38 && header[15] == 0x58)
                    {
                        int width = 1 + (header[24] | (header[25] << 8) | (header[26] << 16));
                        int height = 1 + (header[27] | (header[28] << 8) | (header[29] << 16));
                        return (width, height);
                    }
                }
            }
        }
        catch { }
        return GetWpfDecoderDimensions(filePath);
    }

    private static (int Width, int Height)? GetWpfDecoderDimensions(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                int w = frame.PixelWidth;
                int h = frame.PixelHeight;

                if (frame.Metadata is BitmapMetadata meta)
                {
                    try
                    {
                        if (meta.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
                        {
                            if (orientation is 5 or 6 or 7 or 8)
                            {
                                (w, h) = (h, w);
                            }
                        }
                    }
                    catch { }
                }

                if (w > 0 && h > 0)
                    return (w, h);
            }
        }
        catch { }
        return null;
    }
}
