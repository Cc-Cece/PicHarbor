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
    public void ExplicitTargetFiles_bypass_everything_and_filter_missing_files()
    {
        string existing = Materialize("A.JPG", [1]);
        var config = new GooglePhotosSyncConfig
        {
            ExplicitTargetFiles = [existing, Path.Combine(archiveDir.Path, "missing.JPG")]
        };

        var (files, targetDirectory, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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

        var (files, targetDirectory, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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

        var (files, _, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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
            GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config));
    }

    [Fact]
    public void All_mode_returns_existing_media_files_and_skips_missing_ones()
    {
        string onDisk = Materialize("2024/2024-06/IMG_01.JPG", [1, 2, 3]);
        SeedManifest(
            ("2024/2024-06/IMG_01.JPG", 3, "2024-06-15T12:00:00+00:00"),
            ("2024/2024-06/IMG_MISSING.JPG", 999, "2024-06-15T12:00:00+00:00"));

        var config = new GooglePhotosSyncConfig { ScopeMode = GooglePhotosScopeMode.All };

        var (files, _, totalBytes) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config);

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

        var (files, _, _) = GooglePhotosSyncEngine.ResolveCandidateFiles(archiveDir.Path, config, onLog: logs.Add);

        files.Count.ShouldBe(1);
        files[0].ShouldEndWith("IMG_02.JPG");
        logs.ShouldContain(l => l.Contains("非图片/视频"));
    }
}
