namespace PicHarbor.Core.Scope;

/// <summary>
/// Specifies the scope mode for filtering files during backup or restore operations.
/// </summary>
public enum ScopeMode
{
    /// <summary>Process all files.</summary>
    All,

    /// <summary>Process files created or modified within a specific date range.</summary>
    Date,

    /// <summary>Process files under a specific directory path.</summary>
    Folder,

    /// <summary>Process explicitly selected files.</summary>
    Manual
}
