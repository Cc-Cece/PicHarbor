using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.System;

namespace PicHarbor.Gui.Util;

/// <summary>
/// Provides read-only Windows Shell integration for file operations
/// (Open, Open With, Copy, Copy Path, Show in Explorer, Properties).
/// </summary>
public static class ShellServices
{
    #region Win32 API Definitions

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string pcszFile;
        public string? pcszClass;
        public OPENASINFOFLAGS oaifInFlags;
    }

    [Flags]
    private enum OPENASINFOFLAGS
    {
        OAIF_ALLOW_REGISTRATION = 0x0001,
        OAIF_REGISTER_EXT = 0x0002,
        OAIF_EXEC = 0x0004,
        OAIF_FORCE_REGISTRATION = 0x0008,
        OAIF_HIDE_REGISTRATION = 0x0020,
        OAIF_URL_PROTOCOL = 0x0040,
        OAIF_FILE_IS_URI = 0x0080
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SHObjectProperties(IntPtr hwndOwner, uint dwType, string objectName, string? pageName);

    private const uint SHOP_FILEPATH = 0x00000002;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, uint dwFlags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    #endregion

    private static readonly string[] PictureExtensions =
    [
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".webp",
        ".heic", ".heif", ".tif", ".tiff", ".dng"
    ];

    public static bool IsPicture(string path)
    {
        string extension = Path.GetExtension(path);
        return PictureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens one picture in the default viewer together with the other pictures in its folder,
    /// so the viewer can move to the previous and next image.
    /// </summary>
    public static async Task OpenImageWithNeighborsAsync(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        bool launched = false;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            StorageFolder? folder = await file.GetParentAsync();
            StorageFileQueryResult? neighbors = null;
            if (folder is not null)
            {
                var queryOptions = new QueryOptions
                {
                    FolderDepth = FolderDepth.Shallow,
                    IndexerOption = IndexerOption.DoNotUseIndexer
                };
                queryOptions.FileTypeFilter.Clear();
                foreach (string extension in PictureExtensions)
                {
                    queryOptions.FileTypeFilter.Add(extension);
                }

                neighbors = folder.CreateFileQueryWithOptions(queryOptions);
                await neighbors.GetFilesAsync(0, 1);
            }

            var options = new LauncherOptions();
            if (neighbors is not null)
            {
                options.NeighboringFilesQuery = neighbors;
            }

            launched = await Launcher.LaunchFileAsync(file, options);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenImageWithNeighborsAsync error: {ex.Message}");
        }

        if (!launched)
        {
            OpenFiles(new[] { path });
        }
    }

    /// <summary>
    /// Opens the specified files using their Windows default associated applications.
    /// </summary>
    public static void OpenFiles(IReadOnlyList<string> filePaths)
    {
        foreach (string path in filePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open {path}: {ex.Message}");
                    try
                    {
                        MessageBox.Show(
                            string.Format(App.GetString("FmtOpenFileFailed", "无法打开文件 \"{0}\"：\n{1}"), Path.GetFileName(path), ex.Message),
                            App.GetString("OpenFileFailedTitle", "打开文件失败"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Displays the Windows standard "Open With..." dialog for a single file.
    /// </summary>
    public static void OpenWith(string filePath, IntPtr hwndOwner)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var info = new OPENASINFO
            {
                pcszFile = filePath,
                pcszClass = null,
                oaifInFlags = OPENASINFOFLAGS.OAIF_ALLOW_REGISTRATION | OPENASINFOFLAGS.OAIF_EXEC
            };
            SHOpenWithDialog(hwndOwner, ref info);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenWith error: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the actual files to the Windows Shell Clipboard.
    /// </summary>
    public static void CopyFilesToClipboard(IReadOnlyList<string> filePaths)
    {
        var existing = filePaths.Where(File.Exists).ToList();
        if (existing.Count == 0) return;

        var collection = new System.Collections.Specialized.StringCollection();
        foreach (string path in existing)
        {
            collection.Add(path);
        }

        try
        {
            Clipboard.SetFileDropList(collection);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CopyFilesToClipboard error: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the file paths as plain text to the Windows Clipboard.
    /// </summary>
    public static void CopyPathsToClipboard(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0) return;
        string text = string.Join(Environment.NewLine, filePaths);
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CopyPathsToClipboard error: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens Explorer and selects/highlights the specified file(s).
    /// </summary>
    public static void ShowInExplorer(IReadOnlyList<string> filePaths)
    {
        var existing = filePaths.Where(File.Exists).ToList();
        if (existing.Count == 0) return;

        if (existing.Count == 1)
        {
            Process.Start("explorer.exe", $"/select,\"{existing[0]}\"");
            return;
        }

        // Group files by parent directory for SHOpenFolderAndSelectItems
        var groups = existing.GroupBy(Path.GetDirectoryName);
        foreach (var group in groups)
        {
            string? dir = group.Key;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            try
            {
                if (SHParseDisplayName(dir, IntPtr.Zero, out IntPtr pidlFolder, 0, out _) == 0)
                {
                    var filePidls = new List<IntPtr>();
                    foreach (string file in group)
                    {
                        if (SHParseDisplayName(file, IntPtr.Zero, out IntPtr pidlFile, 0, out _) == 0)
                        {
                            filePidls.Add(pidlFile);
                        }
                    }

                    if (filePidls.Count > 0)
                    {
                        SHOpenFolderAndSelectItems(pidlFolder, (uint)filePidls.Count, filePidls.ToArray(), 0);
                        foreach (IntPtr p in filePidls) CoTaskMemFree(p);
                    }
                    CoTaskMemFree(pidlFolder);
                }
                else
                {
                    // Fallback to opening folder directly
                    Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
            catch
            {
                Process.Start("explorer.exe", $"/select,\"{group.First()}\"");
            }
        }
    }

    /// <summary>
    /// Displays the Windows Shell standard file Properties window.
    /// </summary>
    public static void ShowProperties(IReadOnlyList<string> filePaths, IntPtr hwndOwner)
    {
        foreach (string path in filePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    SHObjectProperties(hwndOwner, SHOP_FILEPATH, path, null);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ShowProperties error for {path}: {ex.Message}");
                }
            }
        }
    }

    #region Shell Thumbnail Integration

    // IID_IShellItemImageFactory in shobjidl_core.h. The other published value
    // bcc82b79-4808-4161-967d-098852779423 is not this interface: shell returns E_NOINTERFACE
    // and every HEIC or MOV thumbnail becomes the gallery placeholder.
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    // Shell returns the file-type icon when many thumbnail requests run at once.
    // Two at a time keeps the handler on the real frame instead of that icon.
    private static readonly SemaphoreSlim ThumbnailSlots = new(2, 2);
    private static readonly ConcurrentDictionary<string, byte[]?> FileIconSamples = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to retrieve a native Windows Explorer thumbnail image for the specified file.
    /// Supports HEIC, MOV, MP4, RAW and standard images using registered shell thumbnail handlers.
    /// A per-type file icon is treated as no thumbnail, so the gallery does not cache it.
    /// </summary>
    public static ImageSource? GetShellThumbnail(string filePath, int width = 160, int height = 160, bool thumbnailOnly = false)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        bool limit = Application.Current?.Dispatcher.CheckAccess() != true;
        if (limit)
        {
            ThumbnailSlots.Wait();
        }

        try
        {
            ImageSource? image = GetShellImage(filePath, width, height, thumbnailOnly ? SIIGBF.SIIGBF_THUMBNAILONLY : 0);
            if (image is BitmapSource bitmap && IsFileIcon(bitmap, filePath))
            {
                return null;
            }

            return image;
        }
        finally
        {
            if (limit)
            {
                ThumbnailSlots.Release();
            }
        }
    }

    /// <summary>
    /// Decodes a HEIC or HEIF still with the system image codec and scales it down.
    /// Unlike the shell image factory, this never substitutes the Windows file icon.
    /// </summary>
    public static BitmapSource? DecodeHeifStill(string filePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        bool limit = Application.Current?.Dispatcher.CheckAccess() != true;
        if (limit)
        {
            ThumbnailSlots.Wait();
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            BitmapSource source = decoder.Frames[0];
            source = ApplyExifOrientation(source, ReadExifOrientation(source));
            if (decodeWidth > 0 && source.PixelWidth > decodeWidth)
            {
                double scale = (double)decodeWidth / source.PixelWidth;
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            if (source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (limit)
            {
                ThumbnailSlots.Release();
            }
        }
    }

    /// <summary>
    /// One frame from the system video pipeline. Shell's image factory returns the file icon for these
    /// HEVC clips, so the gallery uses this when that icon is rejected.
    /// </summary>
    public static async Task<BitmapSource?> GetVideoFrameAsync(string filePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || decodeWidth <= 0)
        {
            return null;
        }

        await ThumbnailSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            MediaClip clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            TimeSpan at = TimeSpan.FromMilliseconds(400);
            if (clip.OriginalDuration > TimeSpan.Zero && at >= clip.OriginalDuration)
            {
                at = TimeSpan.FromMilliseconds(clip.OriginalDuration.TotalMilliseconds / 2);
            }

            using var thumb = await composition.GetThumbnailAsync(at, decodeWidth, decodeWidth, VideoFramePrecision.NearestFrame);
            if (thumb == null || thumb.Size == 0)
            {
                return null;
            }

            using var stream = thumb.AsStreamForRead();
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            ThumbnailSlots.Release();
        }
    }

    public static bool IsJpegShellFileIcon(byte[] jpeg, string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if (!UsesShellThumbnail(extension))
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            BitmapFrame frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }

            return IsFileIcon(frame, filePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool UsesShellThumbnail(string extension)
    {
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource? GetShellImage(string filePath, int width, int height, SIIGBF extraFlags)
    {
        try
        {
            Guid uuid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref uuid, out var factory);
            if (hr != 0 || factory == null)
            {
                return null;
            }

            SIIGBF flags = SIIGBF.SIIGBF_RESIZETOFIT | extraFlags;
            hr = factory.GetImage(new SIZE(width, height), flags, out IntPtr hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return BitmapSourceFromShellBitmap(hBitmap);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFileIcon(BitmapSource image, string filePath)
    {
        int score = IconDifference(image, filePath);
        return score >= 0 && score < 18;
    }

    private static int IconDifference(BitmapSource image, string filePath)
    {
        if (!UsesShellThumbnail(Path.GetExtension(filePath)))
        {
            return -1;
        }

        byte[]? icon = FileIconSamples.GetOrAdd(Path.GetExtension(filePath).ToLowerInvariant(), _ => CaptureIconSample(filePath));
        if (icon == null)
        {
            return -1;
        }

        byte[] sample = SampleGray(image);
        if (sample.Length != icon.Length || sample.Length == 0)
        {
            return -1;
        }

        int difference = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            difference += Math.Abs(sample[i] - icon[i]);
        }

        return difference / sample.Length;
    }

    private static byte[]? CaptureIconSample(string filePath)
    {
        if (GetShellImage(filePath, 240, 240, SIIGBF.SIIGBF_ICONONLY) is not BitmapSource icon)
        {
            return null;
        }

        return SampleGray(icon);
    }

    private static byte[] SampleGray(BitmapSource source)
    {
        const int cells = 16;
        BitmapSource pixels = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = pixels.PixelWidth;
        int height = pixels.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        int stride = width * 4;
        byte[] buffer = new byte[stride * height];
        pixels.CopyPixels(buffer, stride, 0);
        byte[] sample = new byte[cells * cells];
        for (int y = 0; y < cells; y++)
        {
            int py = Math.Min(height - 1, y * height / cells);
            for (int x = 0; x < cells; x++)
            {
                int px = Math.Min(width - 1, x * width / cells);
                int index = py * stride + px * 4;
                sample[y * cells + x] = (byte)((buffer[index] + buffer[index + 1] + buffer[index + 2]) / 3);
            }
        }

        return sample;
    }

    private static int ReadExifOrientation(BitmapSource source)
    {
        if (source.Metadata is not BitmapMetadata metadata)
        {
            return 1;
        }

        try
        {
            return metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation ? orientation : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            6 => new RotateTransform(90),
            8 => new RotateTransform(270),
            _ => null
        };
        if (transform == null)
        {
            return source;
        }

        var oriented = new TransformedBitmap(source, transform);
        if (oriented.CanFreeze)
        {
            oriented.Freeze();
        }

        return oriented;
    }

    /// <summary>
    /// Shell video and HEIC thumbnails are often 32-bit bitmaps whose alpha channel is entirely 0.
    /// WPF treats that as fully transparent, so the gallery shows an empty card. Opaque RGB is kept.
    /// </summary>
    private static System.Windows.Media.ImageSource? BitmapSourceFromShellBitmap(IntPtr hBitmap)
    {
        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
            hBitmap,
            IntPtr.Zero,
            Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        if (source.Format != System.Windows.Media.PixelFormats.Bgra32 &&
            source.Format != System.Windows.Media.PixelFormats.Pbgra32)
        {
            source.Freeze();
            return source;
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        if (width <= 0 || height <= 0 || height > int.MaxValue / (width * 4))
        {
            source.Freeze();
            return source;
        }

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        bool anyVisible = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyVisible = true;
                break;
            }
        }

        if (anyVisible)
        {
            source.Freeze();
            return source;
        }

        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var repaired = System.Windows.Media.Imaging.BitmapSource.Create(
            width,
            height,
            source.DpiX > 0 ? source.DpiX : 96,
            source.DpiY > 0 ? source.DpiY : 96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        repaired.Freeze();
        return repaired;
    }

    #endregion
}
