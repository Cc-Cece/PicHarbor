namespace GetAndSee.Core.Organize;

/// <summary>
/// The outcome of resolving an archive's effective <see cref="OrganizeScheme"/> for a <c>copy</c> run.
/// </summary>
/// <param name="Effective">The scheme the run must use.</param>
/// <param name="Warning">A user-facing warning when an explicit <c>--organize-by</c> was ignored, else <see langword="null"/>.</param>
/// <param name="ShouldRecord">
/// <see langword="true"/> when the archive has no recorded scheme yet and <see cref="Effective"/> must be
/// persisted so future runs are byte-stable.
/// </param>
public sealed record SchemeResolution(OrganizeScheme Effective, string? Warning, bool ShouldRecord);

/// <summary>
/// Decides which <see cref="OrganizeScheme"/> a <c>copy</c> run uses, given the archive's recorded scheme
/// (if any) and the <c>--organize-by</c> request. The layout is an <b>archive property</b>: once recorded
/// it is authoritative, so a re-run never silently re-shuffles an existing tree.
/// </summary>
/// <remarks>
/// Resolution rules:
/// <list type="bullet">
///   <item>
///     <b>A recorded scheme exists</b> → it is used. If <c>--organize-by</c> was passed <i>explicitly</i>
///     and differs, a warning is returned and the recorded scheme is still kept (use <c>reorganize</c> to
///     change a layout). A merely <i>defaulted</i> flag yields silently to the recorded scheme.
///   </item>
///   <item>
///     <b>No recorded scheme</b> (a brand-new archive — the v2→v3 migration only stamps a pre-existing
///     one) → the requested scheme is used and flagged to be recorded.
///   </item>
/// </list>
/// </remarks>
public static class OrganizeSchemeResolver
{
    /// <summary>Resolves the effective scheme for a run.</summary>
    /// <param name="recorded">The scheme already recorded for the archive, or <see langword="null"/> if none.</param>
    /// <param name="requested">The scheme from <c>--organize-by</c> (its default when not passed).</param>
    /// <param name="requestedIsExplicit"><see langword="true"/> when <c>--organize-by</c> was passed on the command line (not defaulted).</param>
    /// <returns>The <see cref="SchemeResolution"/> describing the effective scheme, any warning, and whether to record it.</returns>
    public static SchemeResolution Resolve(OrganizeScheme? recorded, OrganizeScheme requested, bool requestedIsExplicit)
    {
        if (recorded is not OrganizeScheme recordedScheme)
        {
            // Brand-new archive: honour the request (default or explicit) and record it.
            return new SchemeResolution(requested, Warning: null, ShouldRecord: true);
        }

        if (requestedIsExplicit && requested != recordedScheme)
        {
            string warning =
                $"This archive is organized as '{OrganizeSchemes.ToToken(recordedScheme)}'. " +
                $"Ignoring --organize-by '{OrganizeSchemes.ToToken(requested)}'. " +
                "Use 'reorganize' to change it.";
            return new SchemeResolution(recordedScheme, warning, ShouldRecord: false);
        }

        // Recorded scheme stands; a defaulted or matching flag needs neither a warning nor a re-record.
        return new SchemeResolution(recordedScheme, Warning: null, ShouldRecord: false);
    }
}
