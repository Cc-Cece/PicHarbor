using PicHarbor.Core.Util;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Util;

public sealed class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741824, "1.0 GB")]
    public void Humanize_formats_binary_units(long bytes, string expected)
    {
        ByteSize.Humanize(bytes).ShouldBe(expected);
    }
}
