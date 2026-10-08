using PicHarbor.Core.Search;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Search;

public sealed class MediaTypeClassifierTests
{
    [Theory]
    [InlineData("2024-08/IMG_0001.HEIC", MediaType.Photo)]
    [InlineData("2024-08/IMG_0001.heic", MediaType.Photo)]
    [InlineData("IMG.JPG", MediaType.Photo)]
    [InlineData("IMG.jpeg", MediaType.Photo)]
    [InlineData("IMG.DNG", MediaType.Photo)]
    [InlineData("2024-08/IMG_0002.MOV", MediaType.Video)]
    [InlineData("clip.mp4", MediaType.Video)]
    [InlineData("clip.m4v", MediaType.Video)]
    [InlineData("Screenshot.PNG", MediaType.Screenshot)]
    [InlineData("shot.png", MediaType.Screenshot)]
    [InlineData("ispRegDump.bin", MediaType.Other)]
    [InlineData("sidecar.aae", MediaType.Other)]
    [InlineData("noextension", MediaType.Other)]
    public void Classifies_by_extension(string path, MediaType expected)
    {
        MediaTypeClassifier.Classify(path).ShouldBe(expected);
    }
}
