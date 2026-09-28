using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Organize;

public sealed class MediaPairMatcherTests
{
    private static ManifestEntry CreateManifestEntry(string destPath)
    {
        return new ManifestEntry(destPath, 1024, null, null);
    }

    [Fact]
    public void Finds_missing_live_photo_mov_pair()
    {
        var manifest = new[]
        {
            CreateManifestEntry("2024/2024-08/IMG_9130.HEIC"),
            CreateManifestEntry("2024/2024-08/IMG_9130.MOV")
        };

        var selected = new[] { "2024/2024-08/IMG_9130.HEIC" };

        var suggestions = MediaPairMatcher.FindMissingPairs(selected, manifest, includeLivePhoto: true);

        suggestions.Count.ShouldBe(1);
        suggestions[0].SourceRelativePath.ShouldBe("2024/2024-08/IMG_9130.HEIC");
        suggestions[0].SuggestedRelativePath.ShouldBe("2024/2024-08/IMG_9130.MOV");
        suggestions[0].Reason.ShouldBe(MediaPairReason.LivePhoto);
    }

    [Fact]
    public void Finds_missing_aae_sidecar()
    {
        var manifest = new[]
        {
            CreateManifestEntry("2024/2024-08/IMG_9130.HEIC"),
            CreateManifestEntry("2024/2024-08/IMG_9130.AAE")
        };

        var selected = new[] { "2024/2024-08/IMG_9130.HEIC" };

        var suggestions = MediaPairMatcher.FindMissingPairs(selected, manifest, includeLivePhoto: false, includeAae: true);

        suggestions.Count.ShouldBe(1);
        suggestions[0].SuggestedRelativePath.ShouldBe("2024/2024-08/IMG_9130.AAE");
        suggestions[0].Reason.ShouldBe(MediaPairReason.AaeSidecar);
    }

    [Fact]
    public void Finds_missing_raw_jpg_pair()
    {
        var manifest = new[]
        {
            CreateManifestEntry("2024/2024-08/IMG_9130.DNG"),
            CreateManifestEntry("2024/2024-08/IMG_9130.JPG")
        };

        var selected = new[] { "2024/2024-08/IMG_9130.DNG" };

        var suggestions = MediaPairMatcher.FindMissingPairs(selected, manifest, includeLivePhoto: false, includeAae: false, includeRawJpg: true);

        suggestions.Count.ShouldBe(1);
        suggestions[0].SuggestedRelativePath.ShouldBe("2024/2024-08/IMG_9130.JPG");
        suggestions[0].Reason.ShouldBe(MediaPairReason.RawJpgPair);
    }

    [Fact]
    public void Ignores_already_selected_pairs()
    {
        var manifest = new[]
        {
            CreateManifestEntry("2024/2024-08/IMG_9130.HEIC"),
            CreateManifestEntry("2024/2024-08/IMG_9130.MOV")
        };

        var selected = new[] { "2024/2024-08/IMG_9130.HEIC", "2024/2024-08/IMG_9130.MOV" };

        var suggestions = MediaPairMatcher.FindMissingPairs(selected, manifest, includeLivePhoto: true);

        suggestions.ShouldBeEmpty();
    }
}
