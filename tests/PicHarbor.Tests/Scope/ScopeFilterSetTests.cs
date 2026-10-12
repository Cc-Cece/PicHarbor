using PicHarbor.Core.Android;
using PicHarbor.Core.iPhone;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Scope;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Scope;

/// <summary>
/// Unit tests for the unified combination filter (ScopeFilterSet) shared by the
/// iPhone / Android / Google Photos ends. Day-granularity date comparisons use
/// mid-day UTC timestamps so the asserted local calendar date is timezone-stable.
/// </summary>
public sealed class ScopeFilterSetTests
{
    private static ManifestEntry Entry(string destPath, string? exifIso = null, string? mtimeIso = null) =>
        new(destPath, 100, exifIso, mtimeIso);

    private static readonly string ExifMidDay = "2024-06-15T12:00:00+00:00";

    // ---------- MatchSubfolder ----------

    [Fact]
    public void MatchSubfolder_null_or_empty_selection_matches_everything()
    {
        ScopeFilterSet.MatchSubfolder("2024/2024-08/IMG_01.JPG", null).ShouldBeTrue();
        ScopeFilterSet.MatchSubfolder("2024/2024-08/IMG_01.JPG", new HashSet<string>()).ShouldBeTrue();
    }

    [Fact]
    public void MatchSubfolder_exact_directory_match()
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024/2024-08" };
        ScopeFilterSet.MatchSubfolder("2024/2024-08/IMG_01.JPG", selected).ShouldBeTrue();
        ScopeFilterSet.MatchSubfolder("2024/2024-08x/IMG_02.JPG", selected).ShouldBeFalse();
        ScopeFilterSet.MatchSubfolder("2024/2024-07/IMG_03.JPG", selected).ShouldBeFalse();
    }

    [Fact]
    public void MatchSubfolder_deep_prefix_boundary()
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024-08" };
        ScopeFilterSet.MatchSubfolder("2024-08/IMG_01.JPG", selected).ShouldBeTrue();
        ScopeFilterSet.MatchSubfolder("2024-08/sub/deep/IMG_02.JPG", selected).ShouldBeTrue();
        ScopeFilterSet.MatchSubfolder("2024-08x/IMG_03.JPG", selected).ShouldBeFalse();
        ScopeFilterSet.MatchSubfolder("2024-08extra/sub/IMG_04.JPG", selected).ShouldBeFalse();
    }

    [Fact]
    public void MatchSubfolder_normalizes_backslashes_and_is_case_insensitive()
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vacation" };
        ScopeFilterSet.MatchSubfolder(@"Vacation\Day1\IMG_01.JPG", selected).ShouldBeTrue();
        ScopeFilterSet.MatchSubfolder("vacation/IMG_02.JPG", selected).ShouldBeTrue();
    }

    // ---------- IsEntryIncluded: combination semantics ----------

    [Fact]
    public void IsEntryIncluded_empty_filters_include_everything()
    {
        var filters = new ScopeFilterSet();
        filters.IsEntryIncluded(Entry("any/path/IMG_01.JPG", ExifMidDay)).ShouldBeTrue();
        filters.IsEntryIncluded(Entry("any/path/IMG_02.JPG")).ShouldBeTrue();
    }

    [Fact]
    public void IsEntryIncluded_subfolder_and_date_combine_as_intersection()
    {
        var filters = new ScopeFilterSet
        {
            SelectedSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024/2024-06" },
            DateFrom = new DateTime(2024, 6, 1),
            DateTo = new DateTime(2024, 6, 30)
        };

        // In the selected folder AND in range -> included
        filters.IsEntryIncluded(Entry("2024/2024-06/IMG_01.JPG", ExifMidDay)).ShouldBeTrue();

        // In range but different folder -> excluded
        filters.IsEntryIncluded(Entry("2024/2024-07/IMG_02.JPG", ExifMidDay)).ShouldBeFalse();

        // In the selected folder but out of range -> excluded
        filters.IsEntryIncluded(Entry("2024/2024-06/IMG_03.JPG", "2025-01-01T12:00:00+00:00")).ShouldBeFalse();
    }

    [Fact]
    public void IsEntryIncluded_undated_files_follow_policy_when_date_filter_active()
    {
        var include = new ScopeFilterSet { DateFrom = new DateTime(2024, 1, 1), DateTo = new DateTime(2024, 12, 31) };
        include.IsEntryIncluded(Entry("2024/2024-06/IMG_01.JPG")).ShouldBeTrue();

        var skip = new ScopeFilterSet { DateFrom = new DateTime(2024, 1, 1), DateTo = new DateTime(2024, 12, 31), UndatedPolicy = UndatedFilePolicy.Skip };
        skip.IsEntryIncluded(Entry("2024/2024-06/IMG_01.JPG")).ShouldBeFalse();
    }

    [Fact]
    public void IsEntryIncluded_undated_files_pass_when_date_filter_disabled()
    {
        var filters = new ScopeFilterSet { UndatedPolicy = UndatedFilePolicy.Skip };
        filters.IsEntryIncluded(Entry("2024/2024-06/IMG_01.JPG")).ShouldBeTrue();
    }

    [Fact]
    public void IsEntryIncluded_date_bounds_are_day_granular_and_inclusive()
    {
        var filters = new ScopeFilterSet
        {
            DateFrom = new DateTime(2024, 6, 1),
            DateTo = new DateTime(2024, 6, 30)
        };

        filters.IsEntryIncluded(Entry("a.JPG", "2024-06-01T12:00:00+00:00")).ShouldBeTrue();
        filters.IsEntryIncluded(Entry("a.JPG", "2024-06-30T12:00:00+00:00")).ShouldBeTrue();
        filters.IsEntryIncluded(Entry("a.JPG", "2024-05-31T12:00:00+00:00")).ShouldBeFalse();
        filters.IsEntryIncluded(Entry("a.JPG", "2024-07-01T12:00:00+00:00")).ShouldBeFalse();
    }

    // ---------- ParseCaptureDate ----------

    [Fact]
    public void ParseCaptureDate_prefers_exif_over_mtime()
    {
        var entry = Entry("a.JPG", "2020-01-01T00:00:00+00:00", "1999-12-31T00:00:00");
        ScopeFilterSet.ParseCaptureDate(entry).ShouldBe(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ParseCaptureDate_falls_back_to_mtime_when_no_exif()
    {
        var entry = Entry("a.JPG", null, "1999-12-31T00:00:00");
        ScopeFilterSet.ParseCaptureDate(entry).ShouldNotBeNull();
    }

    [Fact]
    public void ParseCaptureDate_returns_null_without_any_date()
    {
        ScopeFilterSet.ParseCaptureDate(Entry("a.JPG")).ShouldBeNull();
    }

    // ---------- Config-level combination semantics (iPhone / Android) ----------

    [Fact]
    public void IPhoneExportConfig_filters_null_keeps_legacy_scope_mode_behavior()
    {
        var config = new IPhoneExportConfig
        {
            ScopeMode = IPhoneRestoreScopeMode.DateRange,
            DateFrom = new DateTime(2024, 1, 1),
            DateTo = new DateTime(2024, 12, 31)
        };

        config.IsEntryIncluded(Entry("2024/2024-06/IMG_01.JPG", ExifMidDay)).ShouldBeTrue();
        config.IsEntryIncluded(Entry("2023/2023-01/IMG_02.JPG", "2023-01-15T12:00:00+00:00")).ShouldBeFalse();
    }

    [Fact]
    public void IPhoneExportConfig_filters_combine_with_manual_base_scope()
    {
        var config = new IPhoneExportConfig
        {
            ScopeMode = IPhoneRestoreScopeMode.ManualSelection,
            ManualSelectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024/2024-06/IMG_01.JPG", "2024/2024-07/IMG_02.JPG" },
            Filters = new ScopeFilterSet { DateFrom = new DateTime(2024, 7, 1), DateTo = new DateTime(2024, 7, 31) }
        };

        // Manual base + July date filter -> only the July one survives
        config.IsEntryIncluded(Entry("2024/2024-06/IMG_01.JPG", ExifMidDay)).ShouldBeFalse();
        config.IsEntryIncluded(Entry("2024/2024-07/IMG_02.JPG", "2024-07-10T12:00:00+00:00")).ShouldBeTrue();
    }

    [Fact]
    public void IPhoneExportConfig_filters_treat_non_manual_scope_mode_as_all_base()
    {
        // Legacy mode value DateRange must NOT double-filter when Filters is present.
        var config = new IPhoneExportConfig
        {
            ScopeMode = IPhoneRestoreScopeMode.DateRange,
            DateFrom = new DateTime(2020, 1, 1),
            Filters = new ScopeFilterSet { DateFrom = new DateTime(2024, 1, 1), DateTo = new DateTime(2024, 12, 31) }
        };

        config.IsEntryIncluded(Entry("2024/2024-06/IMG_01.JPG", ExifMidDay)).ShouldBeTrue();
    }

    [Fact]
    public void AndroidSyncConfig_filters_combine_with_subfolder_and_date()
    {
        var config = new AndroidSyncConfig
        {
            ScopeMode = IPhoneRestoreScopeMode.All,
            Filters = new ScopeFilterSet
            {
                SelectedSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024-06" },
                DateFrom = new DateTime(2024, 6, 10),
                DateTo = new DateTime(2024, 6, 20)
            }
        };

        config.IsEntryIncluded(Entry("2024-06/IMG_01.JPG", "2024-06-15T12:00:00+00:00")).ShouldBeTrue();
        config.IsEntryIncluded(Entry("2024-06/IMG_02.JPG", "2024-07-15T12:00:00+00:00")).ShouldBeFalse();
        config.IsEntryIncluded(Entry("2024-07/IMG_03.JPG", "2024-06-15T12:00:00+00:00")).ShouldBeFalse();
    }
}
