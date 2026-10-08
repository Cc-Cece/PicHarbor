namespace PicHarbor.Core.Organize;

/// <summary>
/// The shared filename-collision rule: keep a desired relative destination path if it is available,
/// otherwise append <c>_2</c>, <c>_3</c>, … to the stem until an available variant is found. Used by both
/// the copier (across-run name collisions during a copy) and <c>reorganize</c> (the new collisions a coarser
/// layout creates), so the disambiguation is identical and never re-implemented (R5 — never overwrite).
/// </summary>
/// <remarks>
/// The caller supplies the availability predicate, which owns what "available" means for its context (the
/// copier checks its in-run assignment set plus on-disk existence; the reorganizer checks the in-run
/// assignment set so a crash-interrupted move re-derives the same suffix deterministically). This type only
/// generates the candidate names in a stable order.
/// </remarks>
public static class CollisionSuffix
{
    /// <summary>
    /// Returns <paramref name="relativePath"/> if <paramref name="isAvailable"/> accepts it, otherwise the
    /// first <c>stem_N.ext</c> variant (N ≥ 2, ascending) it accepts, preserving the directory and extension.
    /// </summary>
    /// <param name="relativePath">The desired relative destination path.</param>
    /// <param name="isAvailable">Predicate returning <see langword="true"/> when a candidate path may be used.</param>
    /// <returns>The first available path — the input itself or a numeric-suffixed variant of its leaf.</returns>
    public static string Resolve(string relativePath, Func<string, bool> isAvailable)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ArgumentNullException.ThrowIfNull(isAvailable);

        if (isAvailable(relativePath))
        {
            return relativePath;
        }

        string directory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(relativePath);
        string extension = Path.GetExtension(relativePath);

        for (int suffix = 2; ; suffix++)
        {
            string candidate = Path.Combine(directory, $"{stem}_{suffix}{extension}");
            if (isAvailable(candidate))
            {
                return candidate;
            }
        }
    }
}
