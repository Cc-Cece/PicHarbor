using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

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
}
