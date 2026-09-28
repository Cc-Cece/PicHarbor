using GetAndSee.Core.Device;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Journal;

public sealed class TransferJournalTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly TransferJournal journal;

    public TransferJournalTests() => journal = TransferJournal.Open(directory.Path);

    public void Dispose()
    {
        journal.Dispose();
        directory.Dispose();
    }

    [Fact]
    public void Creates_visible_database_at_destination_root()
    {
        File.Exists(Path.Combine(directory.Path, "get-and-see.db")).ShouldBeTrue();
    }

    [Fact]
    public void Pending_then_in_progress_then_done()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_1.HEIC", 100, DateTimeOffset.UtcNow);

        journal.EnsurePending(file);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Pending);

        journal.MarkInProgress(file.Path, file.Size, DateTimeOffset.UtcNow);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.InProgress);

        journal.MarkDone(
            file.Path, file.Size, Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
            new MediaMetadata(new DateTime(2024, 8, 15), 35.6, 139.6, "Apple", "iPhone 12 Pro"),
            DateTimeOffset.UtcNow);
        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done);
    }

    [Fact]
    public void Identity_is_source_path_plus_size()
    {
        // Same path, different size ⇒ different identity (R17).
        var small = new RemoteFile("/DCIM/IMG.HEIC", 100, null);
        var large = new RemoteFile("/DCIM/IMG.HEIC", 200, null);
        journal.EnsurePending(small);
        journal.EnsurePending(large);

        journal.MarkDone(small.Path, small.Size, "p", MediaMetadata.Empty, DateTimeOffset.UtcNow);

        journal.GetState(small.Path, small.Size).ShouldBe(FileState.Done);
        journal.GetState(large.Path, large.Size).ShouldBe(FileState.Pending);
    }

    [Fact]
    public void Ensure_pending_is_idempotent()
    {
        var file = new RemoteFile("/DCIM/IMG.HEIC", 100, null);
        journal.EnsurePending(file);
        journal.MarkDone(file.Path, file.Size, "p", MediaMetadata.Empty, DateTimeOffset.UtcNow);

        journal.EnsurePending(file); // must not reset a completed row

        journal.GetState(file.Path, file.Size).ShouldBe(FileState.Done);
    }

    [Fact]
    public void Manifest_contains_only_done_rows()
    {
        var done = new RemoteFile("/DCIM/done.HEIC", 100, null);
        var pending = new RemoteFile("/DCIM/pending.HEIC", 50, null);
        journal.EnsurePending(done);
        journal.EnsurePending(pending);
        journal.MarkDone(done.Path, done.Size, Path.Combine("2024", "2024-08", "done.HEIC"), MediaMetadata.Empty, DateTimeOffset.UtcNow);

        IReadOnlyList<ManifestEntry> manifest = journal.ReadManifest();

        manifest.Count.ShouldBe(1);
        manifest[0].DestPath.ShouldBe(Path.Combine("2024", "2024-08", "done.HEIC"));
        manifest[0].SizeBytes.ShouldBe(100);
    }

    [Fact]
    public void GetUsedDestPaths_returns_recorded_destinations()
    {
        var file = new RemoteFile("/DCIM/x.HEIC", 100, null);
        journal.EnsurePending(file);
        string dest = Path.Combine("2024", "2024-08", "x.HEIC");
        journal.MarkDone(file.Path, file.Size, dest, MediaMetadata.Empty, DateTimeOffset.UtcNow);

        journal.GetUsedDestPaths().ShouldContain(dest);
    }

    [Fact]
    public void Counts_reflect_state()
    {
        journal.EnsurePending(new RemoteFile("/a", 1, null));
        var done = new RemoteFile("/b", 2, null);
        journal.EnsurePending(done);
        journal.MarkDone(done.Path, done.Size, "pb", MediaMetadata.Empty, DateTimeOffset.UtcNow);
        var failed = new RemoteFile("/c", 3, null);
        journal.EnsurePending(failed);
        journal.MarkFailed(failed.Path, failed.Size, "boom", DateTimeOffset.UtcNow);

        JournalCounts counts = journal.CountByState();

        counts.Pending.ShouldBe(1);
        counts.Done.ShouldBe(1);
        counts.Failed.ShouldBe(1);
    }

    [Fact]
    public void GetDestRelativePath_returns_recorded_dest_path()
    {
        var file = new RemoteFile("/DCIM/test.HEIC", 100, null);
        journal.EnsurePending(file);
        string dest = Path.Combine("2026-09", "test.HEIC");
        journal.MarkDone(file.Path, file.Size, dest, MediaMetadata.Empty, DateTimeOffset.UtcNow);

        journal.GetDestRelativePath(file.Path, file.Size).ShouldBe(dest);
    }
}
