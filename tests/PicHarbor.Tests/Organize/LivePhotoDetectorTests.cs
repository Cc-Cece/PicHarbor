using PicHarbor.Core.Organize;
using PicHarbor.Core.Search;
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

    [Fact]
    public void All_view_hides_paired_mov_and_keeps_the_still_and_standalone_video()
    {
        string[] paths =
        [
            Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
            Path.Combine("2024", "2024-08", "IMG_1.MOV"),
            Path.Combine("2024", "2024-08", "VID_9.MOV"),
            Path.Combine("2024", "2024-09", "IMG_1.MOV"),
        ];

        var keys = LivePhotoDetector.FindLivePairKeys(paths);

        LivePhotoDetector.ShowInAllView(paths[0], MediaType.Photo, keys).ShouldBeTrue();
        LivePhotoDetector.ShowInAllView(paths[1], MediaType.Video, keys).ShouldBeFalse();
        LivePhotoDetector.ShowInAllView(paths[2], MediaType.Video, keys).ShouldBeTrue();
        LivePhotoDetector.ShowInAllView(paths[3], MediaType.Video, keys).ShouldBeTrue();
    }

    [Fact]
    public void All_view_hides_paired_mp4_with_jpg()
    {
        string still = Path.Combine("2024", "2024-08", "IMG_9.JPG");
        string video = Path.Combine("2024", "2024-08", "IMG_9.MP4");
        var keys = LivePhotoDetector.FindLivePairKeys([still, video]);

        LivePhotoDetector.ShowInAllView(still, MediaType.Photo, keys).ShouldBeTrue();
        LivePhotoDetector.ShowInAllView(video, MediaType.Video, keys).ShouldBeFalse();
    }

    [Fact]
    public void Displayed_item_count_folds_a_live_pair_into_one()
    {
        string[] paths =
        [
            Path.Combine("2024", "2024-08", "IMG_1.HEIC"),
            Path.Combine("2024", "2024-08", "IMG_1.MOV"),
            Path.Combine("2024", "2024-08", "VID_9.MOV"),
            Path.Combine("2024", "2024-09", "IMG_2.JPG"),
        ];

        LivePhotoDetector.CountDisplayedItems(paths).ShouldBe(3);
    }
}
