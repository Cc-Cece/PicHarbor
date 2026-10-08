using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Organize;

public sealed class ExportPreflightCheckTests
{
    private static ManifestEntry CreateManifestEntry(string destPath)
    {
        return new ManifestEntry(destPath, 1024, null, null);
    }

    [Fact]
    public void Preflight_passes_when_all_files_exist_and_pairs_complete()
    {
        using var tempDir = new TempDirectory();
        string photoPath = Path.Combine(tempDir.Path, "2024", "IMG_1.HEIC");
        string videoPath = Path.Combine(tempDir.Path, "2024", "IMG_1.MOV");

        Directory.CreateDirectory(Path.GetDirectoryName(photoPath)!);
        File.WriteAllText(photoPath, "photo");
        File.WriteAllText(videoPath, "video");

        var manifest = new[]
        {
            CreateManifestEntry("2024/IMG_1.HEIC"),
            CreateManifestEntry("2024/IMG_1.MOV")
        };

        var exportFiles = new[] { "2024/IMG_1.HEIC", "2024/IMG_1.MOV" };

        var result = ExportPreflightCheck.ValidateBeforeExport(tempDir.Path, exportFiles, manifest);

        result.IsSuccess.ShouldBeTrue();
        result.BrokenLivePhotoPairs.ShouldBeEmpty();
        result.MissingPhysicalFiles.ShouldBeEmpty();
    }

    [Fact]
    public void Preflight_detects_broken_live_photo_pair()
    {
        using var tempDir = new TempDirectory();
        string photoPath = Path.Combine(tempDir.Path, "2024", "IMG_1.HEIC");
        Directory.CreateDirectory(Path.GetDirectoryName(photoPath)!);
        File.WriteAllText(photoPath, "photo");

        var manifest = new[]
        {
            CreateManifestEntry("2024/IMG_1.HEIC"),
            CreateManifestEntry("2024/IMG_1.MOV")
        };

        // Only HEIC is in export list, but MOV exists in manifest
        var exportFiles = new[] { "2024/IMG_1.HEIC" };

        var result = ExportPreflightCheck.ValidateBeforeExport(tempDir.Path, exportFiles, manifest);

        result.IsSuccess.ShouldBeFalse();
        result.BrokenLivePhotoPairs.Count.ShouldBe(1);
        result.BrokenLivePhotoPairs[0].ShouldBe("2024/IMG_1.HEIC");
    }

    [Fact]
    public void Preflight_detects_missing_physical_file()
    {
        using var tempDir = new TempDirectory();
        // Do not create physical file on disk

        var manifest = new[]
        {
            CreateManifestEntry("2024/IMG_9999.JPG")
        };

        var exportFiles = new[] { "2024/IMG_9999.JPG" };

        var result = ExportPreflightCheck.ValidateBeforeExport(tempDir.Path, exportFiles, manifest);

        result.IsSuccess.ShouldBeFalse();
        result.MissingPhysicalFiles.Count.ShouldBe(1);
        result.MissingPhysicalFiles[0].ShouldBe("2024/IMG_9999.JPG");
    }
}
