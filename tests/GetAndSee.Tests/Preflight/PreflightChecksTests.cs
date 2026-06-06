using GetAndSee.Core.Errors;
using GetAndSee.Core.Preflight;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Preflight;

public sealed class PreflightChecksTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly PreflightChecks checks = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public void Writable_destination_passes()
    {
        Should.NotThrow(() => checks.EnsureDestinationWritable(directory.Path));
    }

    [Fact]
    public void Non_writable_destination_throws()
    {
        // Create a file, then try to use a path *inside* that file as a directory.
        string filePath = Path.Combine(directory.Path, "a-file");
        File.WriteAllText(filePath, "x");
        string impossible = Path.Combine(filePath, "child");

        Should.Throw<PreflightException>(() => checks.EnsureDestinationWritable(impossible));
    }

    [Fact]
    public void Sufficient_free_space_passes()
    {
        Should.NotThrow(() => checks.EnsureSufficientFreeSpace(directory.Path, 0));
    }

    [Fact]
    public void Insufficient_free_space_throws()
    {
        const long nineHundredTerabytes = 900L * 1024 * 1024 * 1024 * 1024;
        Should.Throw<PreflightException>(() => checks.EnsureSufficientFreeSpace(directory.Path, nineHundredTerabytes));
    }
}
