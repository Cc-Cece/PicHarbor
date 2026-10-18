using System.Net;
using System.Net.Http.Headers;

namespace PicHarbor.Core.Updates;

/// <summary>Stages exposed to the update card.</summary>
public enum UpdateDownloadStage
{
    /// <summary>Resolving the region hint.</summary>
    Locating,
    /// <summary>Measuring actual attachment routes.</summary>
    Probing,
    /// <summary>Receiving executable bytes.</summary>
    Downloading,
    /// <summary>Changing route after failure or sustained low throughput.</summary>
    Switching,
    /// <summary>Verifying the completed executable against official metadata.</summary>
    Verifying
}

/// <summary>A route's public identity and transport address. Credentials are never included.</summary>
/// <param name="Id">UI/log-safe route identifier.</param>
/// <param name="Address">Transport URL; the canonical release URL is kept separately.</param>
/// <param name="IsProxy">Use the configured/system proxy only for the final fallback.</param>
/// <param name="Order">Deterministic tie-break order.</param>
public sealed record UpdateDownloadRoute(string Id, Uri Address, bool IsProxy, int Order);

/// <summary>A bounded successful sample, independent of any formal download attempt.</summary>
/// <param name="Route">Route that produced the sample.</param>
/// <param name="BytesPerSecond">Throughput including connection and first-byte cost.</param>
/// <param name="SupportsRanges">The sample received a valid partial-content response.</param>
public sealed record UpdateRouteSample(UpdateDownloadRoute Route, double BytesPerSecond, bool SupportsRanges);

/// <summary>Observable download state without private URLs or credentials.</summary>
/// <param name="Stage">Current phase.</param>
/// <param name="Route">Public route identifier.</param>
/// <param name="Received">Bytes currently retained in the temporary download files.</param>
/// <param name="Total">Official expected executable length.</param>
/// <param name="BytesPerSecond">Recent transfer rate.</param>
public sealed record UpdateDownloadProgress(UpdateDownloadStage Stage, string Route, long Received, long Total, double BytesPerSecond)
{
    /// <summary>Current maximum simultaneous segment requests; one denotes sequential download.</summary>
    public int Connections { get; init; } = 1;
}

/// <summary>Bounded timing/size policy; injectable to test failures without depending on public mirrors.</summary>
public sealed class UpdateDownloadOptions
{
    /// <summary>Initial segment concurrency, bounded to four connections.</summary>
    public int ParallelConnections { get; init; } = 4;
    /// <summary>Files smaller than this use sequential download.</summary>
    public long ParallelMinimumBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Maximum bytes per independently retried segment.</summary>
    public int SegmentBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>Maximum sample bytes per candidate.</summary>
    public int SampleBytes { get; init; } = 512 * 1024;
    /// <summary>Sample time until first byte.</summary>
    public TimeSpan FirstByteTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Total sample budget.</summary>
    public TimeSpan SampleTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Direct-first profile qualification threshold.</summary>
    public double DirectMinimumSpeed { get; init; } = 1024 * 1024;
    /// <summary>Window for sustained low-speed evaluation.</summary>
    public TimeSpan SlowWindow { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Rate below which another route is tried.</summary>
    public double SlowMinimumSpeed { get; init; } = 200 * 1024;
    /// <summary>Idle timeout on non-proxy routes.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Idle timeout on the final proxy route.</summary>
    public TimeSpan ProxyIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Total operation budget including reselections.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>Measures direct/mirror routes, resumes valid ranges, and verifies every completed candidate.</summary>
public sealed partial class UpdateDownloadRouter : IDisposable
{
    /// <summary>Fixed candidate roots, in deterministic order; never supplied by release metadata.</summary>
    public static IReadOnlyList<string> Mirrors { get; } = Array.AsReadOnly(new[]
    { "https://edgeone.gh-proxy.com/", "https://hk.gh-proxy.com/", "https://gh-proxy.com/", "https://gh.llkk.cc/" });
    private readonly Func<UpdateDownloadRoute, HttpClient> clients;
    private readonly Func<Uri, bool> hasProxy;
    private readonly IUpdateRegionResolver regions;
    private readonly TimeProvider time;
    private readonly UpdateDownloadOptions options;
    private readonly Func<string, ReleaseUpdate, string, CancellationToken, Task> verify;
    private readonly object sync = new();
    private CancellationTokenSource? active;
    private int generation;
    private UpdateRegionMode mode;

    /// <summary>Creates a router with fresh per-route clients; custom clocks/transports/verifiers are test seams.</summary>
    public UpdateDownloadRouter(IUpdateRegionResolver regions, Func<UpdateDownloadRoute, HttpClient> clients,
        Func<Uri, bool> hasProxy, TimeProvider? time = null, UpdateDownloadOptions? options = null,
        Func<string, ReleaseUpdate, string, CancellationToken, Task>? verify = null)
    {
        this.regions = regions;
        this.clients = clients;
        this.hasProxy = hasProxy;
        this.time = time ?? TimeProvider.System;
        this.options = options ?? new();
        this.verify = verify ?? UpdatePolicy.VerifyAsync;
        if (this.options.SampleBytes < 2) throw new ArgumentOutOfRangeException(nameof(options));
        if (this.options.ParallelConnections is < 1 or > 4 || this.options.SegmentBytes < 2 || this.options.ParallelMinimumBytes < 2)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    /// <summary>Ranks by effective speed, with direct preferred when within 20 percent of the fastest route.</summary>
    public static IReadOnlyList<UpdateRouteSample> Rank(IEnumerable<UpdateRouteSample> samples)
    {
        var sorted = samples.OrderByDescending(s => s.BytesPerSecond).ThenBy(s => s.Route.Order).ToList();
        var direct = sorted.FirstOrDefault(s => s.Route.Id == "direct");
        if (direct is not null && direct.BytesPerSecond >= sorted[0].BytesPerSecond * 0.8)
        { sorted.Remove(direct); sorted.Insert(0, direct); }
        return sorted;
    }

    /// <summary>Interrupts only the route-selection cycle, retaining safe partial bytes and the operation budget.</summary>
    public void RequestReselect(UpdateRegionMode? newMode = null)
    {
        regions.Invalidate();
        lock (sync)
        {
            if (newMode is { } value) mode = value;
            generation++;
            active?.Cancel();
        }
    }

    /// <summary>Downloads and verifies an executable. Only successful verification permits the caller to mark it ready.</summary>
    public async Task DownloadAsync(ReleaseUpdate release, string path, string officialHash, UpdateRegionMode regionMode,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(options.TotalTimeout, time);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        var attempts = new Dictionary<string, int>();
        using var segments = new SegmentedDownloadSession(this, release, path);
        lock (sync) mode = regionMode;
        var direct = new UpdateDownloadRoute("direct", release.Download, false, 0);
        var mirrors = Mirrors.Select((root, index) => new UpdateDownloadRoute(new Uri(root).Host,
            new Uri(root + release.Download.AbsoluteUri), false, index + 1)).ToArray();
        var proxy = new UpdateDownloadRoute("proxy", release.Download, true, 5);
        int totalAttempts = 0;
        while (totalAttempts < 12)
        {
            ct.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested) throw new TimeoutException("Update download exceeded its total time budget.");
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            int before;
            UpdateRegionMode currentMode;
            lock (sync) { active = cycle; before = generation; currentMode = mode; }
            void Report(UpdateDownloadStage stage, string route = "", double speed = 0)
                => progress?.Report(new(stage, route, segments.RetainedBytes ?? (File.Exists(path) ? new FileInfo(path).Length : 0), release.Size, speed));
            try
            {
                Report(UpdateDownloadStage.Locating);
                var region = await regions.ResolveAsync(currentMode, cycle.Token).ConfigureAwait(false);
                Report(UpdateDownloadStage.Probing);
                var samples = new List<UpdateRouteSample>();
                bool mirrorProbed = region != UpdateNetworkRegion.Other;
                if (!mirrorProbed)
                {
                    var sample = await ProbeAsync(direct, release, cycle.Token).ConfigureAwait(false);
                    if (sample is not null) samples.Add(sample);
                    mirrorProbed = sample is null || sample.BytesPerSecond < options.DirectMinimumSpeed;
                    if (mirrorProbed) samples.AddRange(await ProbeManyAsync(mirrors, release, cycle.Token).ConfigureAwait(false));
                }
                else samples.AddRange(await ProbeManyAsync(new[] { direct }.Concat(mirrors), release, cycle.Token).ConfigureAwait(false));
                var queue = new Queue<UpdateDownloadRoute>(Rank(samples).Select(s => s.Route));
                var tried = new HashSet<string>();
                var slowRoutes = new List<UpdateDownloadRoute>();
                string? lastSlowRoute = null;
                bool proxyQueued = false;
                while (true)
                {
                    if (queue.Count == 0 && !mirrorProbed)
                    {
                        Report(UpdateDownloadStage.Probing);
                        queue = new(Rank(await ProbeManyAsync(mirrors, release, cycle.Token).ConfigureAwait(false)).Select(s => s.Route));
                        mirrorProbed = true;
                    }
                    if (queue.Count == 0 && !proxyQueued)
                    {
                        proxyQueued = true;
                        if (hasProxy(release.Download)) queue.Enqueue(proxy);
                        else if (slowRoutes.Count > 0)
                        {
                            // No fallback actually exists: keep a working slow route rather than fail solely for speed.
                            var slow = slowRoutes[0];
                            lastSlowRoute = slow.Id;
                            tried.Remove(slow.Id);
                            queue.Enqueue(slow);
                        }
                    }
                    if (queue.Count == 0) throw new IOException("All download routes failed. Retry or open GitHub releases.");
                    var route = queue.Dequeue();
                    if (!tried.Add(route.Id) || attempts.GetValueOrDefault(route.Id) >= 2) continue;
                    attempts[route.Id] = attempts.GetValueOrDefault(route.Id) + 1;
                    totalAttempts++;
                    try
                    {
                        if (!File.Exists(path) || new FileInfo(path).Length != release.Size)
                        {
                            bool switchOnSlow = !route.IsProxy && route.Id != lastSlowRoute;
                            if (!await segments.TryDownloadAsync(route, progress, switchOnSlow, cycle.Token).ConfigureAwait(false))
                                await DownloadOneAsync(route, release, path, progress, switchOnSlow, cycle.Token).ConfigureAwait(false);
                        }
                        Report(UpdateDownloadStage.Verifying, route.Id);
                        await verify(path, release, officialHash, cycle.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (!cycle.IsCancellationRequested && (ex is HttpRequestException or IOException or InvalidDataException or TimeoutException or OperationCanceledException))
                    {
                        if (ex is SlowDownloadException) slowRoutes.Add(route);
                        // A complete but invalid candidate can contain bad bytes from any preceding route.
                        if (ex is InvalidDataException || (File.Exists(path) && new FileInfo(path).Length >= release.Size))
                        {
                            segments.Reset();
                            File.Delete(path);
                        }
                        Report(UpdateDownloadStage.Switching, route.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (!operation.IsCancellationRequested && before != Volatile.Read(ref generation)) { }
            catch (OperationCanceledException ex) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            { throw new TimeoutException("Update download exceeded its total time budget.", ex); }
            finally { lock (sync) { if (ReferenceEquals(active, cycle)) active = null; } }
        }
        throw new IOException("Download route attempt limit reached.");
    }

    private async Task<IReadOnlyList<UpdateRouteSample>> ProbeManyAsync(IEnumerable<UpdateDownloadRoute> routes, ReleaseUpdate release, CancellationToken ct)
        => (await Task.WhenAll(routes.Select(route => ProbeAsync(route, release, ct))).ConfigureAwait(false))
            .Where(sample => sample is not null).Select(sample => sample!).ToArray();

    /// <summary>Reads at most the bounded sample and validates an actual executable response.</summary>
    public async Task<UpdateRouteSample?> ProbeAsync(UpdateDownloadRoute route, ReleaseUpdate release, CancellationToken ct)
    {
        long started = time.GetTimestamp();
        using var budget = new CancellationTokenSource(options.SampleTimeout, time);
        using var sampleToken = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        using var firstByte = new CancellationTokenSource(options.FirstByteTimeout, time);
        using var firstToken = CancellationTokenSource.CreateLinkedTokenSource(sampleToken.Token, firstByte.Token);
        try
        {
            using var client = clients(route);
            using var request = Request(route.Address, 0, Math.Min(options.SampleBytes, release.Size) - 1);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, firstToken.Token).ConfigureAwait(false);
            long expected = ValidateResponse(response, 0, release.Size);
            long goal = Math.Min(options.SampleBytes, release.Size);
            if (expected < goal) throw new InvalidDataException("Incomplete route sample range.");
            await using var input = await response.Content.ReadAsStreamAsync(sampleToken.Token).ConfigureAwait(false);
            byte[] buffer = new byte[Math.Min(options.SampleBytes, 81920)];
            long received = 0;
            byte first = 0;
            while (received < goal)
            {
                int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, goal - received)),
                    received == 0 ? firstToken.Token : sampleToken.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("Truncated route sample.");
                ValidatePrefix(buffer, count, received, ref first);
                received += count;
            }
            double elapsed = Math.Max(time.GetElapsedTime(started).TotalSeconds, 0.001);
            return new(route, received / elapsed, response.StatusCode == HttpStatusCode.PartialContent);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException))
        { return null; }
    }

    private async Task DownloadOneAsync(UpdateDownloadRoute route, ReleaseUpdate release, string path,
        IProgress<UpdateDownloadProgress>? progress, bool switchOnSlow, CancellationToken ct)
    {
        using var client = clients(route);
        await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 81920, true);
        if (output.Length > release.Size) output.SetLength(0);
        long windowStarted = time.GetTimestamp();
        long windowBytes = 0;
        double speed = 0;
        while (output.Length < release.Size)
        {
            long offset = output.Length;
            using var request = Request(route.Address, offset, null);
            using var headerBudget = new CancellationTokenSource(route.IsProxy ? TimeSpan.FromSeconds(10) : options.FirstByteTimeout, time);
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct, headerBudget.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            long expected = ValidateResponse(response, offset, release.Size);
            if (response.StatusCode == HttpStatusCode.OK) { output.SetLength(0); offset = 0; }
            output.Position = offset;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] buffer = new byte[81920];
            long segment = 0;
            byte first = 0;
            while (true)
            {
                using var idle = new CancellationTokenSource(route.IsProxy ? options.ProxyIdleTimeout : options.IdleTimeout, time);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
                int count = await input.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (segment + count > expected || output.Position + count > release.Size)
                    throw new InvalidDataException("Download exceeds the official executable length.");
                if (offset == 0) ValidatePrefix(buffer, count, segment, ref first);
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                segment += count;
                windowBytes += count;
                double elapsed = time.GetElapsedTime(windowStarted).TotalSeconds;
                speed = windowBytes / Math.Max(elapsed, 0.001);
                progress?.Report(new(UpdateDownloadStage.Downloading, route.Id, output.Position, release.Size, speed));
                if (time.GetElapsedTime(windowStarted) >= options.SlowWindow)
                {
                    if (switchOnSlow && speed < options.SlowMinimumSpeed) throw new SlowDownloadException();
                    windowStarted = time.GetTimestamp();
                    windowBytes = 0;
                }
            }
            if (segment != expected) throw new IOException("Truncated executable segment.");
        }
    }

    private static HttpRequestMessage Request(Uri address, long from, long? to)
    {
        if (address.Scheme != "https") throw new InvalidDataException("Only HTTPS download routes are allowed.");
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.UserAgent.ParseAdd("PicHarbor-Updater/1.0");
        return request;
    }

    private static long ValidateResponse(HttpResponseMessage response, long offset, long total)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json")
            throw new InvalidDataException("Download route returned an error page.");
        long expected;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.Unit != "bytes" || range.From != offset || range.Length != total || range.To is not { } end || end < offset || end >= total)
                throw new InvalidDataException("Invalid executable content range.");
            expected = end - offset + 1;
        }
        else if (response.StatusCode == HttpStatusCode.OK) expected = total;
        else throw new InvalidDataException("Unexpected executable response status.");
        if (response.Content.Headers.ContentLength is { } length && length != expected)
            throw new InvalidDataException("Unexpected executable response length.");
        return expected;
    }

    private static void ValidatePrefix(byte[] buffer, int count, long received, ref byte first)
    {
        if (received == 0) first = buffer[0];
        if (received == 0 && count >= 2 && (first != 'M' || buffer[1] != 'Z') ||
            received == 1 && (first != 'M' || buffer[0] != 'Z'))
            throw new InvalidDataException("Download route did not return an executable.");
    }

    private sealed class SlowDownloadException() : IOException("Sustained low download speed.");

    /// <inheritdoc/>
    public void Dispose() { lock (sync) { active?.Cancel(); } }
}
