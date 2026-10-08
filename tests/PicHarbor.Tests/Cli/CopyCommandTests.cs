using System.CommandLine;
using PicHarbor.Cli.Commands;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Cli;

public sealed class CopyCommandTests
{
    [Fact]
    public void Organize_by_defaults_to_month_when_not_passed()
    {
        Command copy = CopyCommand.Build();

        ParseResult result = copy.Parse(["--dest", "X"]);

        result.GetValue(CopyCommand.OrganizeByOption).ShouldBe(OrganizeSchemes.MonthToken);
        // A defaulted flag is reported as implicit — this is how copy tells a defaulted flag from an
        // explicit one so it can silently yield to an archive's recorded scheme.
        (result.GetResult(CopyCommand.OrganizeByOption) is { Implicit: true }).ShouldBeTrue();
    }

    [Theory]
    [InlineData("month")]
    [InlineData("year-month")]
    [InlineData("year")]
    [InlineData("flat")]
    public void Organize_by_is_explicit_when_passed(string token)
    {
        Command copy = CopyCommand.Build();

        ParseResult result = copy.Parse(["--dest", "X", "--organize-by", token]);

        result.Errors.ShouldBeEmpty();
        result.GetValue(CopyCommand.OrganizeByOption).ShouldBe(token);
        (result.GetResult(CopyCommand.OrganizeByOption) is { Implicit: false }).ShouldBeTrue();
    }

    [Fact]
    public void Organize_by_rejects_an_unknown_value()
    {
        Command copy = CopyCommand.Build();

        ParseResult result = copy.Parse(["--dest", "X", "--organize-by", "weekly"]);

        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Dry_run_scheme_preview_treats_a_pre_v3_archive_as_year_month()
    {
        using TempDirectory dir = new();
        // A pre-v3 archive with a copied file: the real run's v2→v3 migration will stamp year-month, so the
        // dry-run preview must report year-month too (not the flat-month default) — otherwise it misleads
        // exactly the upgrade population the per-archive scheme protects.
        var file = new RemoteFile("/DCIM/100APPLE/IMG_1.HEIC", 100, new DateTimeOffset(2024, 8, 15, 9, 0, 0, TimeSpan.Zero));
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.EnsurePending(file);
            journal.MarkDone(file.Path, file.Size, Path.Combine("2024", "2024-08", "IMG_1.HEIC"), MediaMetadata.Empty, DateTimeOffset.UtcNow);
        }

        JournalFixtures.DowngradeToV2(dir.Path);

        CopyCommand.TryReadRecordedScheme(dir.Path).ShouldBe(OrganizeScheme.YearMonth);
    }

    [Fact]
    public void Dry_run_scheme_preview_returns_a_recorded_scheme()
    {
        using TempDirectory dir = new();
        using (TransferJournal journal = TransferJournal.Open(dir.Path))
        {
            journal.SetOrganizeScheme(OrganizeScheme.Flat);
        }

        CopyCommand.TryReadRecordedScheme(dir.Path).ShouldBe(OrganizeScheme.Flat);
    }

    [Fact]
    public void Dry_run_scheme_preview_is_null_without_an_archive()
    {
        using TempDirectory dir = new();

        CopyCommand.TryReadRecordedScheme(dir.Path).ShouldBeNull();
    }

    [Fact]
    public void Incomplete_reorganize_guard_returns_an_actionable_error_when_a_migration_is_unfinished()
    {
        using TempDirectory dir = new();
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        journal.SetOrganizeScheme(OrganizeScheme.YearMonth);
        journal.SetReorganizeTarget(OrganizeScheme.Month); // a reorganize toward month is mid-flight

        string? error = CopyCommand.IncompleteReorganizeError(journal, dir.Path);

        error.ShouldNotBeNull();
        error.ShouldContain("unfinished reorganize");
        error.ShouldContain("--organize-by month"); // the actionable command to finish it
    }

    [Fact]
    public void Incomplete_reorganize_guard_allows_copy_when_no_migration_is_pending()
    {
        using TempDirectory dir = new();
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        journal.SetOrganizeScheme(OrganizeScheme.Month);

        CopyCommand.IncompleteReorganizeError(journal, dir.Path).ShouldBeNull();
    }

    [Fact]
    public void Incomplete_reorganize_guard_allows_copy_in_the_benign_stamp_then_clear_window()
    {
        // The tiny window where the scheme is already stamped to the target but the marker was not yet
        // cleared: marker == recorded scheme, so copy is not blocked.
        using TempDirectory dir = new();
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        journal.SetOrganizeScheme(OrganizeScheme.Month);
        journal.SetReorganizeTarget(OrganizeScheme.Month);

        CopyCommand.IncompleteReorganizeError(journal, dir.Path).ShouldBeNull();
    }
}
