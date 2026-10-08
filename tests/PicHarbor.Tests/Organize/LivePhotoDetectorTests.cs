using PicHarbor.Core.Organize;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Organize;

public sealed class LivePhotoDetectorTests
{
    [Fact]
    public void Pairs_heic_and_mov_with_same_basename_in_same_folder()
    {
        string[] paths =
        [
            Path.Combine("2024", "2024-08", "IMG_1234.HEIC"),
            Path.Combine("2024", "2024-08", "IMG_1234.MOV"),
        ];

        var pairs = LivePhotoDetector.FindPairs(paths);

        pairs.Count.ShouldBe(1);
        pairs[0].Image.ShouldEndWith("IMG_1234.HEIC");
        pairs[0].Video.ShouldEndWith("IMG_1234.MOV");
    }

    [Fact]
    public void Does_not_pair_across_different_folders()
    {
        string[] paths =
        [
            Path.Combine("2024", "2024-08", "IMG_1234.HEIC"),
            Path.Combine("2024", "2024-09", "IMG_1234.MOV"),
        ];

        LivePhotoDetector.FindPairs(paths).ShouldBeEmpty();
    }

    [Fact]
    public void Standalone_photo_or_video_is_not_a_pair()
    {
        string[] paths =
        [
            Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
            Path.Combine("2024", "2024-08", "IMG_2.MOV"),
        ];

        LivePhotoDetector.FindPairs(paths).ShouldBeEmpty();
    }

    [Fact]
    public void Pairs_jpg_with_mov()
    {
        string[] paths =
        [
            Path.Combine("2024", "2024-08", "IMG_9.JPG"),
            Path.Combine("2024", "2024-08", "IMG_9.MOV"),
        ];

        LivePhotoDetector.FindPairs(paths).Count.ShouldBe(1);
    }

    [Fact]
    public void Counts_multiple_pairs()
    {
        string[] paths =
        [
            Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
            Path.Combine("2024", "2024-08", "IMG_1.MOV"),
            Path.Combine("2024", "2024-08", "IMG_2.HEIC"),
            Path.Combine("2024", "2024-08", "IMG_2.MOV"),
            Path.Combine("2024", "2024-08", "IMG_3.HEIC"), // photo only
        ];

        LivePhotoDetector.FindPairs(paths).Count.ShouldBe(2);
    }
}
