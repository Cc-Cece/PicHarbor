using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using PicHarbor.Core.Journal;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui.Util;

/// <summary>
/// Debounced persistence of the subfolder-tree checked selection into the journal table
/// <c>scope_subfolder_selections</c>. One instance per scope-bearing VM.
/// Semantics: a change schedules a debounced overwrite-write (300 ms); rebuilding the tree
/// flushes the previous tree against the archive it was loaded from, then restores the stored
/// selection (no stored rows = first run, keep all-checked default; a stored empty selection
/// is persisted as a sentinel row meaning "filter intentionally disabled").
/// </summary>
public sealed class ScopeSelectionPersistence
{
    /// <summary>Stored row that represents an intentionally empty selection (subfolder filter off).</summary>
    public const string NoneSentinel = "__PicHarborSelectionNone__";

    private readonly Func<string?> archivePathProvider;
    private readonly Func<string> scopeKindProvider;
    private readonly Func<string> deviceKeyProvider;
    private readonly Func<ObservableCollection<SubfolderNodeViewModel>> treeProvider;
    private readonly DispatcherTimer saveTimer;
    private string? loadedArchivePath;
    private bool hasLiveTree;

    /// <summary>Creates the persistence helper bound to the VM's archive path, scope kind, device key and tree.</summary>
    public ScopeSelectionPersistence(
        Func<string?> archivePathProvider,
        Func<string> scopeKindProvider,
        Func<string> deviceKeyProvider,
        Func<ObservableCollection<SubfolderNodeViewModel>> treeProvider)
    {
        this.archivePathProvider = archivePathProvider;
        this.scopeKindProvider = scopeKindProvider;
        this.deviceKeyProvider = deviceKeyProvider;
        this.treeProvider = treeProvider;
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            Flush();
        };
    }

    /// <summary>Schedules a debounced save; safe to call from every checkbox toggle.</summary>
    public void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// <summary>Marks the currently tracked tree as dead (before clearing it); later flushes become no-ops.</summary>
    public void Invalidate()
    {
        saveTimer.Stop();
        loadedArchivePath = null;
        hasLiveTree = false;
    }

    /// <summary>
    /// Writes the current tree selection to the journal the tree was loaded from (a no-op when the
    /// tree is not live, e.g. after the archive path became invalid or the tree was cleared).
    /// </summary>
    public void Flush()
    {
        saveTimer.Stop();
        string? path = loadedArchivePath;
        if (!hasLiveTree || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            using var journal = TransferJournal.Open(path);
            var selected = SubfolderTreeBuilder.CollectSelected(treeProvider());
            if (selected.Count == 0)
            {
                selected.Add(NoneSentinel);
            }

            journal.SetScopeSubfolderSelections(scopeKindProvider(), deviceKeyProvider(), selected);
        }
        catch
        {
            // Persistence is best-effort; a failed write must never break the UI flow.
        }
    }

    /// <summary>
    /// Restores the stored selection into a freshly built tree and records the owning archive path.
    /// Call after <see cref="SubfolderTreeBuilder.Build"/> and before wiring node callbacks.
    /// </summary>
    public void RestoreInto(string archivePath, ObservableCollection<SubfolderNodeViewModel> tree)
    {
        loadedArchivePath = archivePath;
        hasLiveTree = tree.Count > 0;
        if (!hasLiveTree)
        {
            return;
        }

        try
        {
            using var journal = TransferJournal.OpenReadOnly(archivePath);
            var stored = journal.GetScopeSubfolderSelections(scopeKindProvider(), deviceKeyProvider());
            if (stored.Count == 0)
            {
                return; // First run for this device: keep the all-checked default.
            }

            foreach (var root in tree)
            {
                root.RestoreFromStored(stored);
            }
        }
        catch
        {
            // Restore is best-effort; the all-checked default remains on failure.
        }
    }
}
