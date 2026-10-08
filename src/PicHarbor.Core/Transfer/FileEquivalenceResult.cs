namespace PicHarbor.Core.Transfer;

/// <summary>
/// Represents the result of comparing a source file against a target destination file for equivalence.
/// </summary>
public enum FileEquivalenceResult
{
    /// <summary>
    /// Confirmed equivalent. The target file is guaranteed or highly confident to be identical.
    /// Safe to skip.
    /// </summary>
    Equivalent,

    /// <summary>
    /// Confirmed not equivalent. The target file is different from the source file.
    /// Needs copy/update.
    /// </summary>
    NotEquivalent,

    /// <summary>
    /// Cannot reliably confirm equivalence.
    /// MUST NOT assume equivalent or skip. Treat as needing copy or collision resolution.
    /// </summary>
    Unknown
}
