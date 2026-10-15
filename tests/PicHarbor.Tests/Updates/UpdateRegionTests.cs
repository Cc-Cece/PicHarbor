using Microsoft.Extensions.Time.Testing;
using PicHarbor.Core.Updates;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Updates;

public sealed class UpdateRegionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PicHarborRegion-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(directory, "country.json");
    private readonly FakeTimeProvider clock = new();

    [Theory]
    [InlineData("CN", UpdateNetworkRegion.Restricted)]
    [InlineData("US", UpdateNetworkRegion.Other)]
    [InlineData("HK", UpdateNetworkRegion.Other)]
    [InlineData("XX", UpdateNetworkRegion.Unknown)]
    [InlineData("", UpdateNetworkRegion.Unknown)]
    [InlineData("not a country", UpdateNetworkRegion.Unknown)]
    public async Task CountryOnlyDeterminesTheInitialProfile(string country, UpdateNetworkRegion expected)
    {
        using var resolver = new UpdateRegionResolver(Cache, _ => Task.FromResult(country), clock, false);
        (await resolver.ResolveAsync(UpdateRegionMode.Automatic, TestContext.Current.CancellationToken)).ShouldBe(expected);
        if (File.Exists(Cache)) File.ReadAllText(Cache).ShouldNotContain("ip=");
    }

    [Fact]
    public async Task OverrideNeverLocatesAndCacheExpiresAfterSevenDays()
    {
        int calls = 0;
        using var resolver = new UpdateRegionResolver(Cache, _ => { calls++; return Task.FromResult(calls == 1 ? "CN" : "US"); }, clock, false);
        var ct = TestContext.Current.CancellationToken;
        (await resolver.ResolveAsync(UpdateRegionMode.Restricted, ct)).ShouldBe(UpdateNetworkRegion.Restricted);
        (await resolver.ResolveAsync(UpdateRegionMode.Other, ct)).ShouldBe(UpdateNetworkRegion.Other);
        calls.ShouldBe(0);
        (await resolver.ResolveAsync(UpdateRegionMode.Automatic, ct)).ShouldBe(UpdateNetworkRegion.Restricted);
        clock.Advance(TimeSpan.FromDays(6));
        (await resolver.ResolveAsync(UpdateRegionMode.Automatic, ct)).ShouldBe(UpdateNetworkRegion.Restricted);
        calls.ShouldBe(1);
        clock.Advance(TimeSpan.FromDays(1));
        (await resolver.ResolveAsync(UpdateRegionMode.Automatic, ct)).ShouldBe(UpdateNetworkRegion.Other);
        calls.ShouldBe(2);
        resolver.Invalidate();
        File.Exists(Cache).ShouldBeFalse();
        await resolver.ResolveAsync(UpdateRegionMode.Automatic, ct);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task CacheIsDeviceLocalAndContainsNoRawTrace()
    {
        using (var first = new UpdateRegionResolver(Cache, _ => Task.FromResult("CN"), clock, false))
            await first.ResolveAsync(UpdateRegionMode.Automatic, TestContext.Current.CancellationToken);
        using var next = new UpdateRegionResolver(Cache, _ => throw new Exception("Cache should have been read"), clock, false);
        (await next.ResolveAsync(UpdateRegionMode.Automatic, TestContext.Current.CancellationToken)).ShouldBe(UpdateNetworkRegion.Restricted);
        var content = File.ReadAllText(Cache);
        content.ShouldContain("CN");
        content.ShouldNotContain("https");
        content.ShouldNotContain("ip=");
    }

    [Fact]
    public async Task FailedOrTimedOutLookupUsesUnknownWithoutBlockingDownload()
    {
        using var resolver = new UpdateRegionResolver(Cache, async ct =>
        { await Task.Delay(Timeout.Infinite, ct); return "CN"; }, clock, false);
        var resolving = resolver.ResolveAsync(UpdateRegionMode.Automatic, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        (await resolving).ShouldBe(UpdateNetworkRegion.Unknown);
        File.Exists(Cache).ShouldBeFalse();
    }

    [Fact]
    public async Task NetworkChangeDuringLookupDoesNotRepopulateAStaleCache()
    {
        var country = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resolver = new UpdateRegionResolver(Cache, _ => country.Task, clock, false);
        var resolving = resolver.ResolveAsync(UpdateRegionMode.Automatic, TestContext.Current.CancellationToken);
        resolver.Invalidate();
        country.SetResult("CN");
        (await resolving).ShouldBe(UpdateNetworkRegion.Unknown);
        File.Exists(Cache).ShouldBeFalse();
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
