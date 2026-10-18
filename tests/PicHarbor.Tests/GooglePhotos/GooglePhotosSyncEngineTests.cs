using PicHarbor.Core.Device;
using PicHarbor.Core.GooglePhotos;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Scope;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.GooglePhotos;

/// <summary>
/// Branch coverage for GooglePhotosSyncEngine.ResolveCandidateFiles (previously untested):
/// explicit-target bypass, custom target, and the archive branch with combination filters.
/// </summary>
public sealed class GooglePhotosSyncEngineTests : IDisposable
{
    private readonly TempDirectory archiveDir = new();
    private readonly TempDirectory externalDir = new();

    public void Dispose()
    {
        archiveDir.Dispose();
        externalDir.Dispose();
    }

    private void SeedManifest(params (string DestPath, long Size, string? ExifIso)[] entries)
    {
        using var journal = TransferJournal.Open(archiveDir.Path);
        foreach (var (destPath, size, exifIso) in entries)
        {
            string devicePath = $"/DCIM/{Path.GetFileName(destPath)}";
            string actualPath=Path.Combine(archiveDir.Path,destPath.Replace('/',Path.DirectorySeparatorChar));
            var rf = new RemoteFile(devicePath,File.Exists(actualPath) ? new FileInfo(actualPath).Length : size, null);
            journal.EnsurePending(rf);
            var metadata = exifIso is null
                ? MediaMetadata.Empty
                : new MediaMetadata(DateTime.Parse(exifIso), null, null, null, null);
            journal.MarkDone(rf.Path, rf.Size, destPath, metadata, DateTimeOffset.UtcNow);
        }
    }

    private string Materialize(string destPath, byte[] content)
    {
        string fullPath = Path.Combine(archiveDir.Path, destPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    [Fact]
    public void Preparation_progress_counts_checked_files_including_rejected_files()
    {
        Materialize("good.JPG", [1]);
        string changed = Materialize("changed.JPG", [2]);
        Materialize("sidecar.AAE", [3]);
        SeedManifest(("good.JPG", 1, null), ("changed.JPG", 1, null), ("sidecar.AAE", 1, null));
        File.WriteAllBytes(changed, [2, 3]);
        var updates = new List<GooglePhotosPreparationProgress>();

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path,
            new GooglePhotosSyncConfig(), cancellationToken: TestContext.Current.CancellationToken,
            preparationProgress: new InlinePreparationProgress(updates.Add));

        files.Count.ShouldBe(1);
        updates[0].TotalFiles.ShouldBeNull();
        updates[^1].ScannedFiles.ShouldBe(3);
        updates[^1].TotalFiles.ShouldBe(2);
        updates[^1].ProcessedFiles.ShouldBe(2);
    }

    [Fact]
    public void Custom_target_preparation_reports_discovered_file_count()
    {
        File.WriteAllBytes(Path.Combine(externalDir.Path, "one.JPG"), [1]);
        File.WriteAllBytes(Path.Combine(externalDir.Path, "two.JPG"), [2]);
        var updates = new List<GooglePhotosPreparationProgress>();

        GooglePhotosSyncEngine.ResolveCandidateFiles(null, new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.CustomTarget,
            CustomTargetPath = externalDir.Path
        }, cancellationToken: TestContext.Current.CancellationToken,
            preparationProgress: new InlinePreparationProgress(updates.Add));

        updates[0].TotalFiles.ShouldBeNull();
        updates[^1].ShouldBe(new GooglePhotosPreparationProgress(2, 2, "", 2));
    }

    private sealed class InlinePreparationProgress(Action<GooglePhotosPreparationProgress> report)
        : IProgress<GooglePhotosPreparationProgress>
    {
        public void Report(GooglePhotosPreparationProgress value) => report(value);
    }

    [Fact]
    public void Sync_record_lookup_reads_the_manifest_once_and_preserves_member_identity()
    {
        string rootA = Path.Combine(archiveDir.Path, "A");
        string rootB = Path.Combine(archiveDir.Path, "B");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        var first = new ManifestEntry("photo.JPG", 1, null, null) { ArchiveRoot = rootA, MediaId = "first" };
        var second = new ManifestEntry("photo.JPG", 1, null, null) { ArchiveRoot = rootB, MediaId = "second" };
        int reads = 0;
        IEnumerable<ManifestEntry> ReadOnce()
        {
            if (++reads > 1) throw new InvalidOperationException("Manifest was enumerated again");
            yield return first;
            yield return second;
        }

        var map = GooglePhotosSyncEngine.BuildSyncSelectionMap(archiveDir.Path, ReadOnce());

        reads.ShouldBe(1);
        map.Count.ShouldBe(2);
        map[Path.Combine(rootA, "photo.JPG")].ShouldBe(first.SelectionKey);
        map[Path.Combine(rootB, "photo.JPG")].ShouldBe(second.SelectionKey);
        // Repeated result lookups never read the library again.
        for (int i = 0; i < 100; i++) map.ContainsKey(Path.Combine(rootA, "photo.JPG")).ShouldBeTrue();
        reads.ShouldBe(1);
    }

    [Fact]
    public void Album_progress_event_preserves_stage_counts_without_representing_a_file_result()
    {
        var progress = System.Text.Json.JsonSerializer.Deserialize<GooglePhotosProgressEvent>(
            """{"phase":"archiving","phase_processed":2,"phase_total":5}""")!;

        progress.Phase.ShouldBe("archiving");
        progress.PhaseProcessed.ShouldBe(2);
        progress.PhaseTotal.ShouldBe(5);
        progress.Path.ShouldBeEmpty();
    }

    [Fact]
    public void Preparation_verifies_candidates_during_the_scan_before_reporting_filter_summary()
    {
        string photo = Materialize("photo.JPG", [1]);
        Materialize("photo.AAE", [2]);
        SeedManifest(("photo.JPG", 1, null), ("photo.AAE", 1, null));

        var plan = GooglePhotosSyncEngine.PrepareUpload(archiveDir.Path, new GooglePhotosSyncConfig(),
            onLog: _ => File.WriteAllBytes(photo, [3, 4]),
            cancellationToken: TestContext.Current.CancellationToken);

        // The candidate was already verified when the scan summary was emitted.
        plan.Files.Count.ShouldBe(1);
        plan.Files[0].Size.ShouldBe(1);
        new FileInfo(photo).Length.ShouldBe(2);
    }

    [Fact]
    public void Prepared_directory_plan_is_fixed_and_preserves_evidence_for_resume()
    {
        string first = Path.Combine(externalDir.Path, "first.JPG");
        File.WriteAllBytes(first, [1, 2]);
        var plan = GooglePhotosSyncEngine.PrepareUpload(null, new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.CustomTarget,
            CustomTargetPath = externalDir.Path
        }, cancellationToken: TestContext.Current.CancellationToken);

        string addedLater = Path.Combine(externalDir.Path, "later.JPG");
        File.WriteAllBytes(addedLater, [3]);
        File.WriteAllBytes(first, [4, 5, 6]);

        plan.Files.Count.ShouldBe(1);
        plan.TotalBytes.ShouldBe(2);
        var resumed = plan.SelectFiles([first]);
        resumed.Files[0].ShouldBeSameAs(plan.Files[0]);
        resumed.Files[0].Size.ShouldBe(2);
        Should.Throw<ArgumentException>(() => plan.SelectFiles([addedLater]));
        plan.SelectFiles([]).Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_reuses_prepared_plan_without_rescanning_the_scope()
    {
        string file = Path.Combine(externalDir.Path, "first.JPG");
        File.WriteAllBytes(file, [1]);
        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.CustomTarget,
            CustomTargetPath = externalDir.Path,
            AuthData = "test-credential"
        };
        var plan = GooglePhotosSyncEngine.PrepareUpload(null, config, cancellationToken: TestContext.Current.CancellationToken);
        // Resolving the scope again would now fail. Stop at process launch so no network is used.
        config.CustomTargetPath = Path.Combine(externalDir.Path, "does-not-exist");
        var updates = new List<GooglePhotosPreparationProgress>();

        var error = await Should.ThrowAsync<InvalidOperationException>(() => GooglePhotosSyncEngine.SyncAsync(
            archiveDir.Path, config,
            onLog: message =>
            {
                if (message.Contains("gpmc")) throw new InvalidOperationException("Stopped before process launch");
            },
            cancellationToken: TestContext.Current.CancellationToken,
            preparationProgress: new InlinePreparationProgress(updates.Add), preparedPlan: plan));

        error.Message.ShouldBe("Stopped before process launch");
        updates.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveCandidateFiles_stops_before_accessing_files_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            GooglePhotosSyncEngine.ResolveCandidateFiles(null, new GooglePhotosSyncConfig(), cancellationToken: cts.Token));
    }

    [Fact]
    public void ResolveCandidateFiles_honors_cancellation_before_finishing_preparation()
    {
        Materialize("photo.JPG", [1, 2, 3]);
        Materialize("photo.AAE", [4]);
        SeedManifest(("photo.JPG", 3, null), ("photo.AAE", 1, null));
        using var cts = new CancellationTokenSource();

        Should.Throw<OperationCanceledException>(() =>
            GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, new GooglePhotosSyncConfig(),
                onLog: _ => cts.Cancel(), cancellationToken: cts.Token));
    }

    [Fact]
    public void ExplicitTargetFiles_bypass_everything_and_filter_missing_files()
    {
        string existing = Materialize("A.JPG", [1]);
        var config = new GooglePhotosSyncConfig
        {
            ExplicitTargetFiles = [existing, Path.Combine(archiveDir.Path, "missing.JPG")]
        };

        var (files, targetDirectory, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Count.ShouldBe(1);
        files[0].ShouldBe(existing);
        targetDirectory.ShouldBeNull();
        totalBytes.ShouldBe(1);
    }

    [Fact]
    public void CustomTarget_directory_is_enumerated_recursively()
    {
        string dir = Path.Combine(externalDir.Path, "photos");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), [1, 2]);
        File.WriteAllBytes(Path.Combine(dir, "nested", "b.jpg"), [3, 4, 5]);

        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.CustomTarget,
            CustomTargetPath = dir
        };

        var (files, targetDirectory, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Count.ShouldBe(2);
        targetDirectory.ShouldBe(dir);
        totalBytes.ShouldBe(5);
    }

    [Fact]
    public void CustomTarget_single_file_is_supported()
    {
        string file = Path.Combine(externalDir.Path, "single.jpg");
        File.WriteAllBytes(file, [9]);

        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.CustomTarget,
            CustomTargetPath = file
        };

        var (files, _, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Count.ShouldBe(1);
        totalBytes.ShouldBe(1);
    }

    [Fact]
    public void CustomTarget_missing_path_throws()
    {
        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.CustomTarget,
            CustomTargetPath = Path.Combine(externalDir.Path, "nope")
        };

        Should.Throw<FileNotFoundException>(() =>
            GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void All_mode_returns_existing_media_files_and_skips_missing_ones()
    {
        string onDisk = Materialize("2024/2024-06/IMG_01.JPG", [1, 2, 3]);
        SeedManifest(
            ("2024/2024-06/IMG_01.JPG", 3, "2024-06-15T12:00:00+00:00"),
            ("2024/2024-06/IMG_MISSING.JPG", 999, "2024-06-15T12:00:00+00:00"));

        var config = new GooglePhotosSyncConfig { ScopeMode = GooglePhotosScopeMode.All };

        var (files, _, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Count.ShouldBe(1);
        Path.GetFullPath(files[0]).ShouldBe(Path.GetFullPath(onDisk));
        totalBytes.ShouldBe(3);
    }

    [Fact]
    public void DateRange_applies_precise_instant_bounds_with_undated_pass_through()
    {
        Materialize("in-range.JPG", [1]);
        Materialize("too-late.JPG", [2]);
        Materialize("undated.JPG", [3]);
        SeedManifest(
            ("in-range.JPG", 1, "2024-06-15T23:59:59+00:00"),
            ("too-late.JPG", 2, "2024-06-16T00:00:01+00:00"),
            ("undated.JPG", 3, null));

        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.DateRange,
            DateRangeStart = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
            DateRangeEnd = new DateTimeOffset(2024, 6, 15, 23, 59, 59, TimeSpan.Zero)
        };

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Select(Path.GetFileName).ShouldBe(new[] { "in-range.JPG", "undated.JPG" });
    }

    [Fact]
    public void Subfolder_multi_select_respects_prefix_boundary()
    {
        Materialize("2024-08/IMG_01.JPG", [1]);
        Materialize("2024-08x/IMG_02.JPG", [2]);
        Materialize("2024-08/sub/IMG_03.JPG", [3]);
        SeedManifest(
            ("2024-08/IMG_01.JPG", 1, null),
            ("2024-08x/IMG_02.JPG", 2, null),
            ("2024-08/sub/IMG_03.JPG", 3, null));

        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.Subfolder,
            SelectedSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024-08" }
        };

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Select(Path.GetFileName).ShouldBe(new[] { "IMG_01.JPG", "IMG_03.JPG" });
    }

    [Fact]
    public void Manual_selection_base_restricts_to_selected_paths()
    {
        Materialize("A.JPG", [1]);
        Materialize("B.JPG", [2]);
        SeedManifest(("A.JPG", 1, null), ("B.JPG", 2, null));

        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.ManualSelection,
            ManualSelectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A.JPG" }
        };

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Count.ShouldBe(1);
        files[0].ShouldEndWith("A.JPG");
    }

    [Fact]
    public void Combination_filters_intersect_subfolder_and_date()
    {
        Materialize("2024-06/both.JPG", [1]);
        Materialize("2024-06/wrong-date.JPG", [2]);
        Materialize("2024-07/wrong-folder.JPG", [3]);
        SeedManifest(
            ("2024-06/both.JPG", 1, "2024-06-15T12:00:00+00:00"),
            ("2024-06/wrong-date.JPG", 2, "2024-07-20T12:00:00+00:00"),
            ("2024-07/wrong-folder.JPG", 3, "2024-06-15T12:00:00+00:00"));

        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.All,
            Filters = new ScopeFilterSet
            {
                SelectedSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "2024-06" },
                DateFrom = new DateTime(2024, 6, 1),
                DateTo = new DateTime(2024, 6, 30)
            }
        };

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Count.ShouldBe(1);
        files[0].ShouldEndWith("both.JPG");
    }

    [Fact]
    public void Undated_policy_skip_excludes_entries_without_capture_date()
    {
        Materialize("dated.JPG", [1]);
        Materialize("undated.JPG", [2]);
        SeedManifest(("dated.JPG", 1, "2024-06-15T12:00:00+00:00"), ("undated.JPG", 2, null));

        var config = new GooglePhotosSyncConfig
        {
            ScopeMode = GooglePhotosScopeMode.All,
            Filters = new ScopeFilterSet
            {
                DateFrom = new DateTime(2024, 1, 1),
                DateTo = new DateTime(2024, 12, 31),
                UndatedPolicy = UndatedFilePolicy.Skip
            }
        };

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, cancellationToken: TestContext.Current.CancellationToken);

        files.Count.ShouldBe(1);
        files[0].ShouldEndWith("dated.JPG");
    }

    [Fact]
    public void Non_media_files_are_excluded_and_logged()
    {
        Materialize("IMG_01.AAE", [1]);
        Materialize("IMG_02.JPG", [2]);
        SeedManifest(("IMG_01.AAE", 1, null), ("IMG_02.JPG", 2, null));

        var logs = new List<string>();
        var config = new GooglePhotosSyncConfig { ScopeMode = GooglePhotosScopeMode.All };

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, onLog: logs.Add, cancellationToken: TestContext.Current.CancellationToken);

        files.Count.ShouldBe(1);
        files[0].ShouldEndWith("IMG_02.JPG");
        logs.ShouldContain(l => l.Contains("非图片/视频"));
    }
}
