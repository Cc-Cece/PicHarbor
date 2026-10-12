using System.Collections.ObjectModel;
using System.IO;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui.Util;

/// <summary>
/// Builds the multi-level subfolder tree from journal manifest destination paths, and collects
/// the checked selection back into a path set. Path contract: directories are compared with '/'
/// separators, OrdinalIgnoreCase; segments starting with '.' are excluded (existing convention,
/// e.g. ".AppleSync" or macOS resource forks never appear as folders).
/// </summary>
public static class SubfolderTreeBuilder
{
    /// <summary>
    /// Builds the directory tree from the given manifest destination paths. All nodes default to
    /// checked (matches the legacy first-level picker behavior). Destinations without a directory
    /// part (archive-root files) are ignored.
    /// </summary>
    public static ObservableCollection<SubfolderNodeViewModel> Build(IEnumerable<string> destPaths)
    {
        var roots = new ObservableCollection<SubfolderNodeViewModel>();
        var index = new Dictionary<string, SubfolderNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (string destPath in destPaths)
        {
            string normalized = destPath.Replace('\\', '/');
            string? dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            string[] segments = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(s => s.StartsWith('.')))
            {
                continue;
            }

            SubfolderNodeViewModel? current = null;
            string path = string.Empty;
            foreach (string segment in segments)
            {
                path = path.Length == 0 ? segment : path + "/" + segment;
                if (!index.TryGetValue(path, out var node))
                {
                    node = new SubfolderNodeViewModel(segment, path, current);
                    index[path] = node;
                    (current?.Children ?? roots).Add(node);
                }
                current = node;
            }
        }

        foreach (var root in roots)
        {
            root.SetDescendants(true);
        }

        return roots;
    }

    /// <summary>
    /// Collects the RelativePath of every fully-checked node (a checked parent covers all its
    /// descendants, which are checked by invariant). Returns an empty set when nothing is checked
    /// (= subfolder filter disabled).
    /// </summary>
    public static HashSet<string> CollectSelected(IEnumerable<SubfolderNodeViewModel> roots)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(SubfolderNodeViewModel node)
        {
            if (node.IsChecked == true)
            {
                result.Add(node.RelativePath);
            }
            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        foreach (var root in roots)
        {
            Walk(root);
        }

        return result;
    }
}
