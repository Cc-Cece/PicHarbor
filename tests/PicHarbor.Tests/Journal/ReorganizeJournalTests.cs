using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Journal;

/// <summary>
/// Tests the journal operations <c>reorganize</c> relies on: the destination-path update, the deterministic
/// done-row enumerator with its parsed timestamps, and the in-flight <c>reorganize_target</c> marker.
/// </summary>
public sealed class ReorganizeJournalTests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    private static RemoteFile Photo(string path, DateTimeOffset mtime) => new(path, 100, mtime);

    private static MediaMetadata ExifAt(DateTime original) => new(original, null, null, "Apple", "iPhone");

    [Fact]
    public void UpdateDestPath_changes_only_the_destination_and_keeps_the_row_done()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        RemoteFile file = Photo("/DCIM/100APPLE/IMG_1.HEIC", new DateTimeOffset(2024, 8, 15, 9, 0, 0, TimeSpan.Zero));
        journal.EnsurePending(file);
        journal.MarkDone(
            file.Path, file.Size, Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
            ExifAt(new DateTime(2024, 8, 15, 10, 0, 0)), DateTimeOffset.UtcNow);

        journal.UpdateDestPath(file.Path, file.Size, Path.Combine("2024-08", "IMG_1.HEIC"));

        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done);
        ReorganizeEntry entry = journal.EnumerateDoneForReorganize().ShouldHaveSingleItem();
        entry.DestPath.ShouldBe(Path.Combine("2024-08", "IMG_1.HEIC"));
        // Everything else survives the move: identity, the resolved EXIF date, and the source mtime.
        entry.SourcePath.ShouldBe(file.Path);
        entry.SourceSize.ShouldBe(file.Size);
        entry.ExifDateTimeOriginal.ShouldBe(new DateTime(2024, 8, 15, 10, 0, 0));
        entry.SourceMtime.ShouldBe(new DateTimeOffset(2024, 8, 15, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void EnumerateDoneForReorganize_is_ordered_by_source_path_and_excludes_non_done_rows()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        // Insert out of order; a pending row and a failed row must be excluded.
        RemoteFile c = Photo("/DCIM/100APPLE/IMG_C.HEIC", new DateTimeOffset(2024, 8, 3, 0, 0, 0, TimeSpan.Zero));
        RemoteFile a = Photo("/DCIM/100APPLE/IMG_A.HEIC", new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero));
        RemoteFile b = Photo("/DCIM/100APPLE/IMG_B.HEIC", new DateTimeOffset(2024, 8, 2, 0, 0, 0, TimeSpan.Zero));
        RemoteFile pending = Photo("/DCIM/100APPLE/IMG_PENDING.HEIC", new DateTimeOffset(2024, 8, 4, 0, 0, 0, TimeSpan.Zero));
        RemoteFile failed = Photo("/DCIM/100APPLE/IMG_FAILED.HEIC", new DateTimeOffset(2024, 8, 5, 0, 0, 0, TimeSpan.Zero));
        foreach (RemoteFile file in new[] { c, a, b, pending, failed })
        {
            journal.EnsurePending(file);
        }

        journal.MarkDone(c.Path, c.Size, "c", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        journal.MarkDone(a.Path, a.Size, "a", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        journal.MarkDone(b.Path, b.Size, "b", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        journal.MarkFailed(failed.Path, failed.Size, "boom", DateTimeOffset.UtcNow);

        IReadOnlyList<ReorganizeEntry> rows = journal.EnumerateDoneForReorganize();

        rows.Select(row => row.SourcePath).ShouldBe([a.Path, b.Path, c.Path]);
    }

    [Fact]
    public void EnumerateDoneForReorganize_yields_null_dates_for_an_unsorted_file()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        // An unsorted file: no capture date at all (null mtime), no EXIF.
        RemoteFile file = new("/DCIM/100APPLE/NODATE.DAT", 42, null);
        journal.EnsurePending(file);
        journal.MarkDone(file.Path, file.Size, Path.Combine("unsorted", "NODATE.DAT"), MediaMetadata.Empty, DateTimeOffset.UtcNow);

        ReorganizeEntry entry = journal.EnumerateDoneForReorganize().ShouldHaveSingleItem();

        entry.ExifDateTimeOriginal.ShouldBeNull();
        entry.SourceMtime.ShouldBeNull();
    }

    [Fact]
    public void Reorganize_target_marker_defaults_to_null_then_round_trips_and_clears()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);

        journal.GetReorganizeTarget().ShouldBeNull();

        journal.SetReorganizeTarget(OrganizeScheme.Month);
        journal.GetReorganizeTarget().ShouldBe(OrganizeScheme.Month);

        // Overwriting the in-flight target is allowed (e.g. a fresh reorganize toward a different scheme).
        journal.SetReorganizeTarget(OrganizeScheme.Year);
        journal.GetReorganizeTarget().ShouldBe(OrganizeScheme.Year);

        journal.ClearReorganizeTarget();
        journal.GetReorganizeTarget().ShouldBeNull();
    }

    [Fact]
    public void Reorganize_target_marker_is_independent_of_the_recorded_scheme()
    {
        using TransferJournal journal = TransferJournal.Open(dir.Path);
        journal.SetOrganizeScheme(OrganizeScheme.YearMonth);

        journal.SetReorganizeTarget(OrganizeScheme.Month);

        // The two settings keys do not interfere: the recorded (current) scheme and the in-flight target
        // coexist while a migration is underway.
        journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.YearMonth);
        journal.GetReorganizeTarget().ShouldBe(OrganizeScheme.Month);

        journal.ClearReorganizeTarget();
        journal.GetOrganizeScheme().ShouldBe(OrganizeScheme.YearMonth);
    }
}
