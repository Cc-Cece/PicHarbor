using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using PicHarbor.Core.Updates;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Updates;

public sealed class DownloadRoutingTests
{
    [Fact]
    public void DirectWinsOnlyWithinTwentyPercentAndOtherTiesUseListOrder()
    {
        var direct = new UpdateDownloadRoute("direct", new("https://github.com/file"), false, 0);
        var first = direct with { Id = "mirror1", Order = 1 };
        var second = direct with { Id = "mirror2", Order = 2 };
        UpdateDownloadRouter.Rank(new[] { new UpdateRouteSample(first, 100, true), new(direct, 80, true) })[0].Route.Id.ShouldBe("direct");
        UpdateDownloadRouter.Rank(new[] { new UpdateRouteSample(first, 100, true), new(direct, 79, true) })[0].Route.Id.ShouldBe("mirror1");
        UpdateDownloadRouter.Rank(new[] { new UpdateRouteSample(second, 100, true), new(first, 100, true) })[0].Route.Id.ShouldBe("mirror1");
    }

    [Fact]
    public async Task OtherRegionUsesQualifiedDirectWithoutContactingMirrorsOrProxy()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        using var router = scenario.Router();
        await scenario.Download(router);
        scenario.Calls.ShouldAllBe(call => call.Route == "direct");
        scenario.FormalCalls.Count.ShouldBe(1);
        File.ReadAllBytes(scenario.Path).ShouldBe(scenario.Payload);
    }

    [Theory]
    [InlineData(UpdateNetworkRegion.Restricted)]
    [InlineData(UpdateNetworkRegion.Unknown)]
    public async Task RestrictedAndUnknownRaceAllFiveAndSelectFastest(UpdateNetworkRegion region)
    {
        using var scenario = new Scenario(region);
        scenario.SampleMilliseconds = route => route.Id == "direct" ? 40 : route.Order == 2 ? 1 : 20;
        using var router = scenario.Router();
        await scenario.Download(router);
        scenario.Calls.Count(c => c.Sample).ShouldBe(5);
        scenario.FormalCalls.Single().Route.ShouldBe("hk.gh-proxy.com");
    }

    [Fact]
    public async Task SlowButReachableDirectIsComparedWithMirrors()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        scenario.SampleMilliseconds = route => route.Id == "direct" ? 100 : route.Order == 1 ? 1 : 40;
        using var router = scenario.Router();
        await scenario.Download(router);
        scenario.Calls.Count(c => c.Sample).ShouldBe(5);
        scenario.FormalCalls.Single().Route.ShouldBe("edgeone.gh-proxy.com");
    }

    [Fact]
    public async Task InterruptedDirectResumesOnAMirrorAtTheExactOffset()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        scenario.Formal = (route, request, _) => route.Id == "direct"
            ? scenario.PartialStream(new FailAfterPrefix(scenario.Payload, 16), 0, scenario.Payload.Length - 1)
            : scenario.Bytes(request);
        using var router = scenario.Router();
        await scenario.Download(router);
        scenario.FormalCalls[0].Route.ShouldBe("direct");
        scenario.FormalCalls[1].From.ShouldBe(16);
        File.ReadAllBytes(scenario.Path).ShouldBe(scenario.Payload);
    }

    [Fact]
    public async Task ServerIgnoringResumeRestartsRatherThanAppendingAFullFile()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        File.WriteAllBytes(scenario.Path, scenario.Payload[..16]);
        scenario.Formal = (_, _, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(scenario.Payload) };
        using var router = scenario.Router();
        await scenario.Download(router);
        scenario.FormalCalls.Single().From.ShouldBe(16);
        File.ReadAllBytes(scenario.Path).ShouldBe(scenario.Payload);
    }

    [Fact]
    public async Task BadRangeIsNotAppendedAndTheNextRouteStartsClean()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        File.WriteAllBytes(scenario.Path, scenario.Payload[..16]);
        scenario.Formal = (route, request, _) => route.Id == "direct" ? scenario.BytesFrom(0, scenario.Payload.Length - 1) : scenario.Bytes(request);
        using var router = scenario.Router();
        await scenario.Download(router);
        scenario.FormalCalls[0].From.ShouldBe(16);
        scenario.FormalCalls[1].From.ShouldBe(0);
    }

    [Fact]
    public async Task HashMismatchDiscardsAllContributingBytesBeforeAnotherRoute()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        scenario.Formal = (route, request, _) =>
        {
            if (route.Id != "direct") return scenario.Bytes(request);
            byte[] corrupt = scenario.Payload.ToArray();
            corrupt[5] ^= 255;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(corrupt) };
            response.Content.Headers.ContentRange = new(0, corrupt.Length - 1, corrupt.Length);
            return response;
        };
        using var router = scenario.Router();
        await scenario.Download(router);
        scenario.FormalCalls.Count.ShouldBe(2);
        scenario.FormalCalls[1].From.ShouldBe(0);
        scenario.Verifications.ShouldBe(2);
    }

    [Fact]
    public async Task HtmlSamplesAreRejectedAndConfiguredProxyIsLast()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Restricted) { ProxyAvailable = true };
        scenario.Sample = (_, _) => new(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>", System.Text.Encoding.UTF8, "text/html") };
        using var router = scenario.Router();
        await scenario.Download(router);
        scenario.Calls.Take(5).ShouldAllBe(c => c.Sample);
        scenario.FormalCalls.Single().Route.ShouldBe("proxy");
    }

    [Fact]
    public async Task UnavailableMirrorsNeverAllowAnUnverifiedCandidate()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Restricted);
        scenario.Sample = (_, _) => new(HttpStatusCode.BadGateway);
        using var router = scenario.Router();
        await Should.ThrowAsync<IOException>(() => scenario.Download(router));
        scenario.Verifications.ShouldBe(0);
        scenario.FormalCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReselectionCancelsOnlyTheCurrentCycleAndHasBoundedAttempts()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        using var router = scenario.Router();
        bool reselected = false;
        scenario.Formal = (_, request, ct) =>
        {
            if (!reselected) { reselected = true; router.RequestReselect(); ct.ThrowIfCancellationRequested(); }
            return scenario.Bytes(request);
        };
        await scenario.Download(router);
        scenario.Region.Invalidations.ShouldBe(1);
        scenario.Region.Resolutions.ShouldBe(2);
        scenario.FormalCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CancellationNeverFallsThroughToOtherRoutesOrVerification()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        using var router = scenario.Router();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        scenario.Formal = (_, request, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return scenario.Bytes(request); };
        await Should.ThrowAsync<OperationCanceledException>(() => scenario.Download(router, cancel.Token));
        scenario.FormalCalls.Count.ShouldBe(1);
        scenario.Verifications.ShouldBe(0);
    }

    [Fact]
    public async Task SustainedLowThroughputSwitchesAndKeepsOnlyValidPartialBytes()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        scenario.Formal = (route, request, _) => route.Id == "direct"
            ? scenario.PartialStream(new TimedChunks(scenario.Payload, scenario.Clock), 0, scenario.Payload.Length - 1)
            : scenario.Bytes(request);
        using var router = scenario.Router(new UpdateDownloadOptions { SampleBytes = 16, DirectMinimumSpeed = 1000,
            SlowWindow = TimeSpan.FromSeconds(1), SlowMinimumSpeed = 200 * 1024 });
        await scenario.Download(router);
        scenario.FormalCalls[1].From.ShouldBe(8);
    }

    [Fact]
    public async Task ValidRangeSamplesReadOnlyTheirBudgetAndIgnoreBadPrefixes()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Restricted);
        using var router = scenario.Router();
        var route = new UpdateDownloadRoute("direct", scenario.Release.Download, false, 0);
        var sample = await router.ProbeAsync(route, scenario.Release, TestContext.Current.CancellationToken);
        sample.ShouldNotBeNull();
        sample.SupportsRanges.ShouldBeTrue();
        scenario.Sample = (_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[scenario.Payload.Length]) };
        (await router.ProbeAsync(route, scenario.Release, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task NonProxyIdleTimeoutSwitchesAfterFiveSeconds()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        using var stalled = new HangingStream();
        scenario.Formal = (route, request, _) => route.Id == "direct"
            ? scenario.PartialStream(stalled, 0, scenario.Payload.Length - 1) : scenario.Bytes(request);
        using var router = scenario.Router();
        var pending = scenario.Download(router);
        await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        scenario.Clock.Advance(TimeSpan.FromSeconds(5));
        await pending;
        scenario.FormalCalls.Count.ShouldBe(2);
        scenario.Verifications.ShouldBe(1);
    }

    [Fact]
    public async Task ProxyIsAllowedThirtySecondsIdleButDoesNotHangForever()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Restricted) { ProxyAvailable = true };
        scenario.Sample = (_, _) => new(HttpStatusCode.BadGateway);
        using var stalled = new HangingStream();
        scenario.Formal = (_, _, _) => scenario.PartialStream(stalled, 0, scenario.Payload.Length - 1);
        using var router = scenario.Router();
        var pending = scenario.Download(router);
        await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        scenario.Clock.Advance(TimeSpan.FromSeconds(29));
        pending.IsCompleted.ShouldBeFalse();
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await Should.ThrowAsync<IOException>(() => pending);
        scenario.FormalCalls.Single().Route.ShouldBe("proxy");
    }

    [Fact]
    public async Task TotalBudgetIsReportedAsTimeoutInsteadOfUserCancellation()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        using var stalled = new HangingStream();
        scenario.Formal = (_, _, _) => scenario.PartialStream(stalled, 0, scenario.Payload.Length - 1);
        using var router = scenario.Router(new UpdateDownloadOptions { SampleBytes = 16, DirectMinimumSpeed = 1000, TotalTimeout = TimeSpan.FromSeconds(2) });
        var pending = scenario.Download(router);
        await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        scenario.Clock.Advance(TimeSpan.FromSeconds(2));
        await Should.ThrowAsync<TimeoutException>(() => pending);
        scenario.Verifications.ShouldBe(0);
    }

    [Fact]
    public async Task RepeatedReselectionCannotResetAttemptLimits()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other) { ProxyAvailable = true };
        using var router = scenario.Router();
        scenario.Formal = (_, request, ct) => { router.RequestReselect(); ct.ThrowIfCancellationRequested(); return scenario.Bytes(request); };
        await Should.ThrowAsync<IOException>(() => scenario.Download(router));
        scenario.FormalCalls.Count.ShouldBeLessThanOrEqualTo(12);
        scenario.FormalCalls.GroupBy(call => call.Route).ShouldAllBe(group => group.Count() <= 2);
    }

    [Fact]
    public async Task NoAvailableBackupKeepsAWorkingSlowRoute()
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Other);
        scenario.Sample = (route, request) => route.Id == "direct" ? scenario.Bytes(request) : new(HttpStatusCode.BadGateway);
        scenario.Formal = (_, request, _) =>
        {
            int from = (int)request.Headers.Range!.Ranges.Single().From!.Value;
            return scenario.PartialStream(new TimedChunks(scenario.Payload[from..], scenario.Clock), from, scenario.Payload.Length - 1);
        };
        using var router = scenario.Router(new UpdateDownloadOptions { SampleBytes = 16, DirectMinimumSpeed = 1000,
            SlowWindow = TimeSpan.FromSeconds(1), SlowMinimumSpeed = 200 * 1024 });
        await scenario.Download(router);
        scenario.FormalCalls.Count.ShouldBe(2);
        scenario.FormalCalls.ShouldAllBe(call => call.Route == "direct");
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 5)]
    public async Task ProbeHasSeparateFirstByteAndWholeSampleBudgets(bool sendsPrefix, int seconds)
    {
        using var scenario = new Scenario(UpdateNetworkRegion.Restricted);
        using var stream = new PrefixThenHang(sendsPrefix);
        scenario.Sample = (_, _) => scenario.PartialStream(stream, 0, 15);
        using var router = scenario.Router();
        var pending = router.ProbeAsync(new("direct", scenario.Release.Download, false, 0), scenario.Release, TestContext.Current.CancellationToken);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        scenario.Clock.Advance(TimeSpan.FromSeconds(seconds));
        (await pending).ShouldBeNull();
        scenario.FormalCalls.ShouldBeEmpty();
    }

    private sealed record Call(string Route, bool Sample, long From);

    private sealed class Scenario : IDisposable
    {
        public readonly FakeTimeProvider Clock = new();
        public readonly Region Region;
        public readonly byte[] Payload = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        public readonly List<Call> Calls = [];
        public List<Call> FormalCalls => Calls.Where(c => !c.Sample).ToList();
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PicHarborRoute-" + Guid.NewGuid().ToString("N"));
        public ReleaseUpdate Release { get; }
        public bool ProxyAvailable { get; set; }
        public int Verifications { get; private set; }
        public Func<UpdateDownloadRoute, double> SampleMilliseconds { get; set; } = _ => 10;
        public Func<UpdateDownloadRoute, HttpRequestMessage, HttpResponseMessage>? Sample { get; set; }
        public Func<UpdateDownloadRoute, HttpRequestMessage, CancellationToken, HttpResponseMessage>? Formal { get; set; }
        public Scenario(UpdateNetworkRegion region)
        {
            Region = new(region);
            Payload[0] = (byte)'M'; Payload[1] = (byte)'Z';
            Release = new("v1.2.3", new(1, 2, 3, 0), new("https://github.com/Cc-Cece/PicHarbor/releases/download/v1.2.3/PicHarbor.Gui.exe"),
                new("https://github.com/Cc-Cece/PicHarbor/releases/download/v1.2.3/PicHarbor.Gui.exe.sha256"), Payload.Length);
        }
        public UpdateDownloadRouter Router(UpdateDownloadOptions? options = null) => new(Region, route => new HttpClient(new Handler((request, ct) =>
        {
            bool sample = request.Headers.Range?.Ranges.Single().To is not null;
            Calls.Add(new(route.Id, sample, request.Headers.Range!.Ranges.Single().From!.Value));
            request.Headers.Authorization.ShouldBeNull();
            request.Headers.Contains("Cookie").ShouldBeFalse();
            if (route.Order is > 0 and < 5) request.RequestUri!.AbsoluteUri.ShouldBe(UpdateDownloadRouter.Mirrors[route.Order - 1] + Release.Download.AbsoluteUri);
            if (sample)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(SampleMilliseconds(route)));
                return Task.FromResult(Sample?.Invoke(route, request) ?? Bytes(request));
            }
            return Task.FromResult(Formal?.Invoke(route, request, ct) ?? Bytes(request));
        })), _ => ProxyAvailable, Clock, options ?? new UpdateDownloadOptions { SampleBytes = 16, DirectMinimumSpeed = 1000 },
            async (path, release, hash, ct) =>
            {
                Verifications++;
                byte[] bytes = await File.ReadAllBytesAsync(path, ct);
                if (bytes.Length != release.Size || Convert.ToHexString(SHA256.HashData(bytes)) != hash) throw new InvalidDataException("Official hash mismatch");
            });

        public Task Download(UpdateDownloadRouter router, CancellationToken? token = null)
            => router.DownloadAsync(Release, Path, Convert.ToHexString(SHA256.HashData(Payload)), UpdateRegionMode.Automatic, null,
                token ?? TestContext.Current.CancellationToken);
        public HttpResponseMessage Bytes(HttpRequestMessage request)
        {
            var range = request.Headers.Range!.Ranges.Single();
            return BytesFrom(range.From!.Value, range.To ?? Payload.Length - 1);
        }
        public HttpResponseMessage BytesFrom(long from, long to)
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Payload[(int)from..((int)to + 1)]) };
            response.Content.Headers.ContentRange = new(from, to, Payload.Length);
            return response;
        }
        public HttpResponseMessage PartialStream(Stream stream, long from, long to)
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentRange = new(from, to, Payload.Length);
            response.Content.Headers.ContentLength = to - from + 1;
            return response;
        }
        public void Dispose() => File.Delete(Path);
    }

    private sealed class Region(UpdateNetworkRegion region) : IUpdateRegionResolver
    {
        public int Invalidations;
        public int Resolutions;
        public void Invalidate() => Invalidations++;
        public Task<UpdateNetworkRegion> ResolveAsync(UpdateRegionMode mode, CancellationToken ct) { Resolutions++; return Task.FromResult(region); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class FailAfterPrefix(byte[] bytes, int limit) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= limit) throw new IOException("Disconnected");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, limit - (int)Position)], cancellationToken);
        }
    }
    private sealed class TimedChunks(byte[] bytes, FakeTimeProvider time) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], cancellationToken);
        }
    }
    private sealed class HangingStream : MemoryStream
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
    private sealed class PrefixThenHang(bool sendsPrefix) : MemoryStream
    {
        private bool sent;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sendsPrefix && !sent) { sent = true; buffer.Span[0] = (byte)'M'; buffer.Span[1] = (byte)'Z'; return 2; }
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
