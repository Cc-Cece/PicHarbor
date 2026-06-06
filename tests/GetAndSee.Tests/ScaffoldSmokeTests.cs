using GetAndSee.Core.Device;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests;

/// <summary>Sanity check that the test toolchain (xUnit v3 + Shouldly) is wired up.</summary>
public sealed class ScaffoldSmokeTests
{
    [Fact]
    public void RemoteFile_carries_its_values()
    {
        var file = new RemoteFile("/DCIM/100APPLE/IMG_0001.HEIC", 1234, null);

        file.Path.ShouldBe("/DCIM/100APPLE/IMG_0001.HEIC");
        file.Size.ShouldBe(1234);
        file.ModifiedAt.ShouldBeNull();
    }
}
