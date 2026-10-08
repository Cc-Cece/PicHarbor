using System.ComponentModel;
using System.Diagnostics;

namespace PicHarbor.Cli.Commands;

/// <summary>
/// Opens destination folders in the host file manager. An injectable seam so the <c>search --open</c> path
/// is unit-testable without actually launching Explorer.
/// </summary>
internal interface IFolderOpener
{
    /// <summary>Opens each of the given absolute folder paths.</summary>
    /// <param name="absoluteFolderPaths">Distinct absolute folder paths to reveal.</param>
    void OpenFolders(IReadOnlyList<string> absoluteFolderPaths);
}

/// <summary>
/// Opens folders in Windows Explorer. Best-effort and Windows-only — failing to open a window is never a
/// search failure, and on a non-Windows host it is a no-op.
/// </summary>
internal sealed class ExplorerFolderOpener : IFolderOpener
{
    /// <inheritdoc />
    public void OpenFolders(IReadOnlyList<string> absoluteFolderPaths)
    {
        ArgumentNullException.ThrowIfNull(absoluteFolderPaths);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (string folder in absoluteFolderPaths)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folder}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                // Opening a file-manager window is a convenience; if it fails the search results still stand.
            }
        }
    }
}
