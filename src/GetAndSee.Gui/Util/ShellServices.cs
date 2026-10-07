using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.System;

namespace GetAndSee.Gui.Util;

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
                        MessageBox.Show($"无法打开文件 \"{Path.GetFileName(path)}\"：\n{ex.Message}", "打开文件失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    [Guid("bcc82b79-4808-4161-967d-098852779423")]
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

    /// <summary>
    /// Attempts to retrieve a native Windows Explorer thumbnail image for the specified file.
    /// Supports HEIC, MOV, MP4, RAW and standard images using registered shell thumbnail handlers.
    /// </summary>
    public static System.Windows.Media.ImageSource? GetShellThumbnail(string filePath, int width = 160, int height = 160, bool thumbnailOnly = false)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            Guid uuid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref uuid, out var factory);
            if (hr == 0 && factory != null)
            {
                SIIGBF flags = SIIGBF.SIIGBF_RESIZETOFIT;
                if (thumbnailOnly)
                {
                    flags |= SIIGBF.SIIGBF_THUMBNAILONLY;
                }

                hr = factory.GetImage(new SIZE(width, height), flags, out IntPtr hBitmap);
                if (hr == 0 && hBitmap != IntPtr.Zero)
                {
                    try
                    {
                        var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap,
                            IntPtr.Zero,
                            Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        bitmap.Freeze();
                        return bitmap;
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    #endregion
}
