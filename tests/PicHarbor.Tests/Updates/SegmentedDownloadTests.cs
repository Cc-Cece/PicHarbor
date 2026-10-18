using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using PicHarbor.Core.Updates;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Updates;

public sealed class SegmentedDownloadTests
{
    [Fact]
    public async Task FourRequestsOverlapAndOutOfOrderSegmentsProduceTheExactExecutable()
    {
        using var fixture = new Fixture();
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximum = 0;
        fixture.Part = async (request, ct) =>
        {
            int current = Interlocked.Increment(ref active);
            lock (fourStarted) maximum = Math.Max(maximum, current);
            if (current == 4) fourStarted.TrySetResult();
            await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.Delay(TimeSpan.FromMilliseconds(20 - fixture.Index(request)), ct);
            Interlocked.Decrement(ref active);
            request.Headers.IfRange!.EntityTag!.Tag.ShouldBe("\"release-v1\"");
            request.Headers.AcceptEncoding.Single().Value.ShouldBe("identity");
            return fixture.Bytes(request);
        };

        await fixture.Download();

        maximum.ShouldBe(4);
        fixture.Parts.Count.ShouldBe(8);
        fixture.Calls.ShouldAllBe(call => call.Route == "direct");
        fixture.Progress.ShouldContain(p => p.Connections == 4 && p.Received == 0);
        fixture.AssertComplete();
    }

    [Fact]
    public async Task ATruncatedSegmentIsRetriedWithoutRedownloadingCompletedSegments()
    {
        using var fixture = new Fixture();
        var attempts = new ConcurrentDictionary<int, int>();
        fixture.Part = async (request, ct) =>
        {
            int index = fixture.Index(request);
            int attempt = attempts.AddOrUpdate(index, 1, (_, previous) => previous + 1);
            if (index == 1 && attempt == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                return fixture.Truncated(request);
            }
            return fixture.Bytes(request);
        };

        await fixture.Download();

        attempts[1].ShouldBe(2);
        attempts.Where(pair => pair.Key != 1).ShouldAllBe(pair => pair.Value == 1);
        fixture.Progress.ShouldContain(p => p.Connections == 2);
        fixture.AssertComplete();
    }

    [Fact]
    public async Task RateLimitingReducesFourToTwoToOneAndKeepsFinishedSegments()
    {
        using var fixture = new Fixture();
        int failures = 0;
        fixture.Part = async (request, ct) =>
        {
            if (fixture.Index(request) == 1 && Interlocked.Increment(ref failures) <= 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return response;
            }
            return fixture.Bytes(request);
        };

        await fixture.Download();

        fixture.Progress.Where(p => p.Stage == UpdateDownloadStage.Downloading).Select(p => p.Connections).Distinct()
            .ShouldBe([4, 2, 1]);
        fixture.Parts.Count(c => c.From == 16).ShouldBe(3);
        fixture.AssertComplete();
    }

    [Theory]
    [InlineData("ignored")]
    [InlineData("wrong-range")]
    [InlineData("different-version")]
    [InlineData("encoded")]
    public async Task UnsafeSegmentResponsesRestartSequentiallyWithoutRetainingTheirBytes(string kind)
    {
        using var fixture = new Fixture();
        fixture.Part = (request, _) =>
        {
            var response = fixture.Bytes(request);
            switch (kind)
            {
                case "ignored":
                    response.Dispose();
                    response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Payload) };
                    break;
                case "wrong-range":
                    long from = request.Headers.Range!.Ranges.Single().From!.Value;
                    response.Content.Headers.ContentRange = new(from + 1, from + 15, fixture.Payload.Length);
                    break;
                case "different-version": response.Headers.ETag = new("\"different\""); break;
                case "encoded": response.Content.Headers.ContentEncoding.Add("gzip"); break;
            }
            return Task.FromResult(response);
        };

        await fixture.Download();

        fixture.Sequential.Single().From.ShouldBe(0);
        fixture.AssertComplete();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    public async Task MissingOrWeakVersionIdentityUsesSequentialDownload(string? tag)
    {
        using var fixture = new Fixture { QualificationTag = tag };

        await fixture.Download();

        fixture.Parts.ShouldBeEmpty();
        fixture.Sequential.Count.ShouldBe(1);
        fixture.AssertComplete();
    }

    [Fact]
    public async Task ExhaustedSegmentRetriesFallBackToSequentialDownloadOnTheSameRoute()
    {
        using var fixture = new Fixture();
        fixture.Part = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));

        await fixture.Download();

        fixture.Progress.Where(p => p.Stage == UpdateDownloadStage.Downloading).Select(p => p.Connections).Distinct()
            .ShouldBe([4, 2, 1]);
        fixture.Sequential.Count.ShouldBe(1);
        fixture.Calls.ShouldAllBe(call => call.Route == "direct");
        fixture.AssertComplete();
    }

    [Fact]
    public async Task UserCancellationWaitsForWorkersAndCleansAllSegmentCheckpoints()
    {
        using var fixture = new Fixture();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Part = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        };
        var pending = fixture.Download(cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancel.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);

        fixture.Verifications.ShouldBe(0);
        fixture.Sequential.ShouldBeEmpty();
        Directory.GetFiles(fixture.DirectoryPath, "*.segment-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task ReselectionRetainsOnlyFullyCheckedSegmentsFromTheSameRouteAndVersion()
    {
        using var fixture = new Fixture();
        var checkpointReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateDownloadRouter? activeRouter = null;
        fixture.RouterCreated = router => activeRouter = router;
        int reselected = 0;
        fixture.Part = async (request, ct) =>
        {
            int index = fixture.Index(request);
            if (Volatile.Read(ref reselected) == 0 && index != 0)
            {
                // Segment 4 can only start once segment 0 was written, closed, and checkpointed.
                if (index >= 4) checkpointReady.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return fixture.Bytes(request);
        };
        var pending = fixture.Download();
        await checkpointReady.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Write(ref reselected, 1);
        activeRouter!.RequestReselect();
        await pending;

        fixture.Parts.Count(call => call.From == 0).ShouldBe(1);
        fixture.Progress.ShouldContain(p => p.Stage == UpdateDownloadStage.Locating && p.Received >= 16);
        fixture.AssertComplete();
    }

    [Fact]
    public async Task ReselectionWithANewStrongVersionDiscardsAllOldCheckpoints()
    {
        using var fixture = new Fixture();
        var checkpointReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateDownloadRouter? activeRouter = null;
        fixture.RouterCreated = router => activeRouter = router;
        int reselected = 0;
        fixture.Part = async (request, ct) =>
        {
            int index = fixture.Index(request);
            if (Volatile.Read(ref reselected) == 0)
            {
                if (index == 0)
                {
                    byte[] stale = fixture.Payload[..16];
                    stale[5] ^= 255;
                    var old = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(stale) };
                    old.Headers.ETag = new("\"release-v1\"");
                    old.Content.Headers.ContentRange = new(0, 15, fixture.Payload.Length);
                    return old;
                }
                if (index >= 4) checkpointReady.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            var response = fixture.Bytes(request);
            response.Headers.ETag = new("\"release-v2\"");
            return response;
        };
        var pending = fixture.Download();
        await checkpointReady.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fixture.QualificationTag = "\"release-v2\"";
        Volatile.Write(ref reselected, 1);
        activeRouter!.RequestReselect();
        await pending;

        fixture.Parts.Count(call => call.From == 0).ShouldBe(2);
        fixture.AssertComplete();
    }

    [Fact]
    public async Task TheTotalTimeBudgetAlsoCancelsSegmentWorkersAndCleansCheckpoints()
    {
        var clock = new FakeTimeProvider();
        using var fixture = new Fixture { Clock = clock, TotalTimeout = TimeSpan.FromSeconds(2) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Part = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        };
        var pending = fixture.Download();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));

        await Should.ThrowAsync<TimeoutException>(() => pending);

        fixture.Verifications.ShouldBe(0);
        Directory.GetFiles(fixture.DirectoryPath, "*.segment-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoredQualificationRangeUsesTheExistingSequentialDownloader()
    {
        using var fixture = new Fixture { IgnoreQualificationRange = true };

        await fixture.Download();

        fixture.Parts.ShouldBeEmpty();
        fixture.Sequential.Count.ShouldBe(1);
        fixture.AssertComplete();
    }

    [Fact]
    public async Task ACorruptCompletedDownloadCannotPassVerificationOrLeaveReusableSegments()
    {
        using var fixture = new Fixture();
        fixture.Part = (request, _) =>
        {
            var response = fixture.Bytes(request);
            if (fixture.Index(request) == 1)
            {
                var bytes = fixture.Payload[16..32];
                bytes[0] ^= 255;
                response.Content = new ByteArrayContent(bytes);
                response.Content.Headers.ContentRange = new(16, 31, fixture.Payload.Length);
            }
            return Task.FromResult(response);
        };

        await Should.ThrowAsync<IOException>(() => fixture.Download());

        fixture.Verifications.ShouldBeGreaterThan(0);
        File.Exists(fixture.Path).ShouldBeFalse();
        Directory.GetFiles(fixture.DirectoryPath, "*.segment-*").ShouldBeEmpty();
    }

    private sealed record Call(string Route, long From, long? To);
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory directory = new();
        public string DirectoryPath => directory.Path;
        public string Path => System.IO.Path.Combine(directory.Path, "update.partial");
        public byte[] Payload { get; } = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        public ConcurrentQueue<Call> Calls { get; } = new();
        public ConcurrentQueue<UpdateDownloadProgress> Progress { get; } = new();
        public List<Call> Parts => Calls.Where(call => call.To is not null && call.To != 1 && call.To != 3).ToList();
        public List<Call> Sequential => Calls.Where(call => call.To is null).ToList();
        public int Verifications;
        public string? QualificationTag { get; set; } = "\"release-v1\"";
        public bool IgnoreQualificationRange { get; init; }
        public TimeProvider Clock { get; init; } = TimeProvider.System;
        public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(15);
        public Action<UpdateDownloadRouter>? RouterCreated { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Part { get; set; }

        public Fixture() { Payload[0] = (byte)'M'; Payload[1] = (byte)'Z'; }
        public int Index(HttpRequestMessage request) => (int)(request.Headers.Range!.Ranges.Single().From!.Value / 16);
        public async Task Download(CancellationToken? cancellationToken = null)
        {
            var release = new ReleaseUpdate("v1.2.3", new(1, 2, 3, 0), new("https://github.com/file.exe"), new("https://github.com/file.sha256"), Payload.Length);
            using var router = new UpdateDownloadRouter(new Region(), route => new HttpClient(new Handler(async (request, ct) =>
            {
                var range = request.Headers.Range!.Ranges.Single();
                Calls.Enqueue(new(route.Id, range.From!.Value, range.To));
                if (route.Id != "direct") return new(HttpStatusCode.BadGateway);
                if (range.To == 1)
                {
                    if (IgnoreQualificationRange) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
                    var response = Bytes(request);
                    response.Headers.ETag = QualificationTag is null ? null : EntityTagHeaderValue.Parse(QualificationTag);
                    return response;
                }
                if (range.To is not null && range.To != 3 && Part is not null) return await Part(request, ct);
                return Bytes(request);
            })), _ => false, time: Clock, options: new UpdateDownloadOptions
            {
                SampleBytes = 4, DirectMinimumSpeed = 0, SegmentBytes = 16, ParallelMinimumBytes = 32,
                TotalTimeout = TotalTimeout
            }, verify: async (path, _, hash, ct) =>
            {
                Interlocked.Increment(ref Verifications);
                var bytes = await File.ReadAllBytesAsync(path, ct);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != hash) throw new InvalidDataException("Hash mismatch");
            });
            RouterCreated?.Invoke(router);
            await router.DownloadAsync(release, Path, Convert.ToHexString(SHA256.HashData(Payload)), UpdateRegionMode.Automatic,
                new InlineProgress(Progress.Enqueue), cancellationToken ?? TestContext.Current.CancellationToken);
        }
        public HttpResponseMessage Bytes(HttpRequestMessage request)
        {
            var range = request.Headers.Range!.Ranges.Single();
            long from = range.From!.Value;
            long to = range.To ?? Payload.Length - 1;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Payload[(int)from..((int)to + 1)]) };
            response.Headers.ETag = new("\"release-v1\"");
            response.Content.Headers.ContentRange = new(from, to, Payload.Length);
            return response;
        }
        public HttpResponseMessage Truncated(HttpRequestMessage request)
        {
            var range = request.Headers.Range!.Ranges.Single();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new StreamContent(new MemoryStream(Payload[(int)range.From!.Value..(int)range.To!.Value])) };
            response.Headers.ETag = new("\"release-v1\"");
            response.Content.Headers.ContentRange = new(range.From.Value, range.To.Value, Payload.Length);
            response.Content.Headers.ContentLength = range.To.Value - range.From.Value + 1;
            return response;
        }
        public void AssertComplete()
        {
            File.ReadAllBytes(Path).ShouldBe(Payload);
            Verifications.ShouldBe(1);
            Progress.ShouldAllBe(p => p.Received >= 0 && p.Received <= p.Total);
            Directory.GetFiles(directory.Path, "*.segment-*").ShouldBeEmpty();
        }
        public void Dispose() => directory.Dispose();
    }
    private sealed class Region : IUpdateRegionResolver
    {
        public void Invalidate() { }
        public Task<UpdateNetworkRegion> ResolveAsync(UpdateRegionMode mode, CancellationToken ct) => Task.FromResult(UpdateNetworkRegion.Other);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class InlineProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
