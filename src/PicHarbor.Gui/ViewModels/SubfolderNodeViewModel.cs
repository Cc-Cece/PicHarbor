using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PicHarbor.Gui.ViewModels;

/// <summary>
/// Tree node of the unified multi-level subfolder picker (shared by iPhone / Android / Google Photos).
/// Carries the three-state checkbox logic: checking a parent checks all descendants; a parent shows
/// indeterminate when children are mixed; the user can never set indeterminate directly (it is normalized
/// to <c>false</c>, so a click cycles checked / unchecked only).
/// </summary>
public partial class SubfolderNodeViewModel : ObservableObject
{
    /// <summary>Last path segment display name (e.g. "10-巴黎").</summary>
    public string Name { get; }

    /// <summary>Full relative directory path with '/' separators (e.g. "2024/10-巴黎").</summary>
    public string RelativePath { get; }

    public SubfolderNodeViewModel? Parent { get; }

    public ObservableCollection<SubfolderNodeViewModel> Children { get; } = new();

    /// <summary>Gets a value indicating whether this node has child nodes.</summary>
    public bool HasChildren => Children.Count > 0;

    private bool _suppressSync;

    [ObservableProperty]
    private bool? isChecked = false;

    /// <summary>Raised whenever the effective selection anywhere below/including this node changed.</summary>
    public Action? OnCheckedChanged { get; set; }

    public SubfolderNodeViewModel(string name, string relativePath, SubfolderNodeViewModel? parent)
    {
        Name = name;
        RelativePath = relativePath;
        Parent = parent;
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suppressSync)
        {
            return;
        }

        // Indeterminate is display-only (computed from children); a user click landing on it
        // is normalized to unchecked, keeping the user-facing cycle true/false only.
        if (value is null)
        {
            IsChecked = false;
            return;
        }

        SetDescendants(value.Value);
        Parent?.RecomputeFromChildren();
        OnCheckedChanged?.Invoke();
    }

    /// <summary>Sets this node and all descendants to the given state without raising per-node callbacks.</summary>
    public void SetDescendants(bool state)
    {
        _suppressSync = true;
        IsChecked = state;
        foreach (var child in Children)
        {
            child.SetDescendants(state);
        }
        _suppressSync = false;
    }

    /// <summary>Sets this node's own state silently (no cascade, no callback); used by selection restore.</summary>
    public void SetStateSilently(bool? state)
    {
        _suppressSync = true;
        IsChecked = state;
        _suppressSync = false;
    }

    /// <summary>
    /// Restores this subtree's state from a stored checked-path set. Leaves take their state from the
    /// set; parents are recomputed from their children (full / none / mixed).
    /// </summary>
    public void RestoreFromStored(HashSet<string> storedCheckedPaths)
    {
        foreach (var child in Children)
        {
            child.RestoreFromStored(storedCheckedPaths);
        }

        if (Children.Count == 0)
        {
            SetStateSilently(storedCheckedPaths.Contains(RelativePath));
        }
        else
        {
            bool? state = Children.All(c => c.IsChecked == true)
                ? true
                : Children.All(c => c.IsChecked == false)
                    ? false
                    : (bool?)null;
            SetStateSilently(state);
        }
    }

    /// <summary>Recomputes this node's display state from its children (true / false / indeterminate).</summary>
    private void RecomputeFromChildren()
    {
        if (Children.Count == 0)
        {
            return;
        }

        bool? state = Children.All(c => c.IsChecked == true)
            ? true
            : Children.All(c => c.IsChecked == false)
                ? false
                : (bool?)null;

        if (IsChecked != state)
        {
            _suppressSync = true;
            IsChecked = state;
            _suppressSync = false;
        }

        Parent?.RecomputeFromChildren();
        OnCheckedChanged?.Invoke();
    }
}
