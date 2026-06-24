namespace GetAndSee.Core.Organize;

/// <summary>
/// The date-folder layout strategy for an archive. Chosen once when an archive is first created and then
/// recorded in the journal so a re-run never silently re-shuffles an existing tree (see
/// <see cref="OrganizeSchemeResolver"/>).
/// </summary>
public enum OrganizeScheme
{
    /// <summary>Flat <c>YYYY-MM\name.ext</c> — one click to any month, sorts chronologically. The default.</summary>
    Month,

    /// <summary>Nested <c>YYYY\YYYY-MM\name.ext</c> — the original v1.0 layout, now opt-in.</summary>
    YearMonth,

    /// <summary>Flat <c>YYYY\name.ext</c> — coarse, few folders.</summary>
    Year,

    /// <summary>Everything in one folder (<c>name.ext</c>) — tiny libraries / search-only users.</summary>
    Flat,
}

/// <summary>
/// Maps <see cref="OrganizeScheme"/> values to and from their stable string tokens — the form used on the
/// <c>--organize-by</c> command line and persisted in the journal's <c>organize_scheme</c> setting.
/// </summary>
public static class OrganizeSchemes
{
    /// <summary>The default scheme for a brand-new archive: flat <c>YYYY-MM</c>.</summary>
    public const OrganizeScheme Default = OrganizeScheme.Month;

    /// <summary>Token for <see cref="OrganizeScheme.Month"/>.</summary>
    public const string MonthToken = "month";

    /// <summary>Token for <see cref="OrganizeScheme.YearMonth"/>.</summary>
    public const string YearMonthToken = "year-month";

    /// <summary>Token for <see cref="OrganizeScheme.Year"/>.</summary>
    public const string YearToken = "year";

    /// <summary>Token for <see cref="OrganizeScheme.Flat"/>.</summary>
    public const string FlatToken = "flat";

    /// <summary>All accepted tokens, in presentation order, for help text and option validation.</summary>
    public static readonly IReadOnlyList<string> AllTokens = [MonthToken, YearMonthToken, YearToken, FlatToken];

    /// <summary>Returns the stable token for a scheme.</summary>
    /// <param name="scheme">The scheme.</param>
    /// <returns>Its command-line / persisted token.</returns>
    public static string ToToken(OrganizeScheme scheme) => scheme switch
    {
        OrganizeScheme.Month => MonthToken,
        OrganizeScheme.YearMonth => YearMonthToken,
        OrganizeScheme.Year => YearToken,
        OrganizeScheme.Flat => FlatToken,
        _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unknown organize scheme."),
    };

    /// <summary>Parses a token to its scheme, throwing on an unknown value.</summary>
    /// <param name="token">A scheme token (case-insensitive).</param>
    /// <returns>The matching <see cref="OrganizeScheme"/>.</returns>
    /// <exception cref="ArgumentException">The token does not name a known scheme.</exception>
    public static OrganizeScheme Parse(string token) =>
        TryParse(token) ?? throw new ArgumentException($"Unknown organize scheme '{token}'.", nameof(token));

    /// <summary>Parses a token to its scheme, returning <see langword="null"/> for an unknown or missing value.</summary>
    /// <param name="token">A scheme token (case-insensitive), or <see langword="null"/>.</param>
    /// <returns>The matching scheme, or <see langword="null"/> when unrecognized — tolerant of a foreign value persisted by a newer build.</returns>
    public static OrganizeScheme? TryParse(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        MonthToken => OrganizeScheme.Month,
        YearMonthToken => OrganizeScheme.YearMonth,
        YearToken => OrganizeScheme.Year,
        FlatToken => OrganizeScheme.Flat,
        _ => null,
    };
}
