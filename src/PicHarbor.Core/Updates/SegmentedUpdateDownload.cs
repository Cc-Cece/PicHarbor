using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace PicHarbor.Core.Updates;

public sealed partial class UpdateDownloadRouter
{
    // Checkpoints belong to this download operation and survive route reselection. The GUI owns
    // the temporary destination, so cancellation/restart does not reuse an unverified old session.
    private sealed class SegmentedDownloadSession(UpdateDownloadRouter owner, ReleaseUpdate release, string destination) : IDisposable
    {
        private readonly string prefix = destination + ".segment-" + Guid.NewGuid().ToString("N") + "-";
        private readonly object reportLock = new();
        private string? routeId;
        private EntityTagHeaderValue? validator;
        private bool[] complete = [];
        private long[] received = [];
        private int concurrency;
        private long networkBytes;
        private long windowBytes;
        private long windowStarted;
        private long lastReport;
        private double speed;

        public long? RetainedBytes => routeId is null ? null : ReceivedBytes();

        private long ReceivedBytes()
        {
            long total = 0;
            for (int index = 0; index < received.Length; index++) total += Interlocked.Read(ref received[index]);
            return total;
        }

        private string PartPath(int index) => prefix + index;
        private long PartLength(int index) => Math.Min(owner.options.SegmentBytes, release.Size - (long)index * owner.options.SegmentBytes);

        public async Task<bool> TryDownloadAsync(UpdateDownloadRoute route, IProgress<UpdateDownloadProgress>? progress,
            bool switchOnSlow, CancellationToken ct)
        {
            if (owner.options.ParallelConnections == 1 || release.Size < owner.options.ParallelMinimumBytes) return false;
            using var client = owner.clients(route);
            // Require an actual bounded 206 response and a strong identity before combining requests.
            using var probe = Request(route.Address, 0, 1);
            using var timeout = new CancellationTokenSource(route.IsProxy ? TimeSpan.FromSeconds(10) : owner.options.FirstByteTimeout, owner.time);
            using var qualification = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var response = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, qualification.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent || response.Headers.ETag is not { IsWeak: false } tag)
            {
                Reset();
                return false;
            }
            if (ValidateResponse(response, 0, release.Size) != 2 || response.Content.Headers.ContentRange?.To != 1)
                throw new InvalidDataException("Invalid segmented download qualification range.");
            byte[] signature = new byte[2];
            await using (var input = await response.Content.ReadAsStreamAsync(qualification.Token).ConfigureAwait(false))
                await input.ReadExactlyAsync(signature, qualification.Token).ConfigureAwait(false);
            if (signature[0] != 'M' || signature[1] != 'Z') throw new InvalidDataException("Not an executable download.");

            if (routeId != route.Id || validator?.Tag != tag.Tag)
            {
                Reset();
                routeId = route.Id;
                validator = tag;
                int count = checked((int)((release.Size + owner.options.SegmentBytes - 1) / owner.options.SegmentBytes));
                complete = new bool[count];
                received = new long[count];
            }
            concurrency = Math.Min(owner.options.ParallelConnections, complete.Length);
            windowStarted = owner.time.GetTimestamp();
            lastReport = windowStarted;
            windowBytes = Interlocked.Read(ref networkBytes);
            speed = 0;
            Report(progress, route, switchOnSlow: false, force: true);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var pending = new ConcurrentQueue<int>(Enumerable.Range(0, complete.Length).Where(index => !complete[index]));
                using var wave = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Exception? failure = null;
                TimeSpan retryDelay = TimeSpan.FromMilliseconds(250);
                async Task Worker()
                {
                    await Task.Yield();
                    try
                    {
                        while (pending.TryDequeue(out int index))
                        {
                            wave.Token.ThrowIfCancellationRequested();
                            await DownloadPartAsync(client, route, index, progress, switchOnSlow, wave.Token).ConfigureAwait(false);
                            complete[index] = true;
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                    {
                        if (!wave.IsCancellationRequested)
                        {
                            Interlocked.CompareExchange(ref failure, ex, null);
                            if (ex is SegmentThrottledException limited) retryDelay = limited.Delay;
                            wave.Cancel();
                        }
                    }
                }
                // Yielding workers make concurrency explicit even with a synchronously completing test transport.
                await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Worker())).ConfigureAwait(false);
                // Only fully checked segments are checkpoints; never count aborted or retried bytes twice.
                for (int index = 0; index < received.Length; index++)
                    if (!complete[index]) Interlocked.Exchange(ref received[index], 0);
                ct.ThrowIfCancellationRequested();
                if (failure is null)
                {
                    await AssembleAsync(complete.Length, ct).ConfigureAwait(false);
                    Report(progress, route, switchOnSlow: false, force: true);
                    return true;
                }
                if (failure is InvalidSegmentException or InvalidDataException)
                {
                    // A server ignoring If-Range may have changed entities. No bytes may survive this fallback.
                    Reset();
                    File.Delete(destination);
                    return false;
                }
                if (failure is SlowDownloadException or SegmentCooldownException)
                {
                    await SavePrefixAsync(ct).ConfigureAwait(false);
                    Reset();
                    throw failure;
                }
                if (concurrency == 1)
                {
                    await SavePrefixAsync(ct).ConfigureAwait(false);
                    Reset();
                    return false;
                }
                concurrency = Math.Max(1, concurrency / 2);
                Report(progress, route, switchOnSlow: false, force: true);
                await Task.Delay(retryDelay, owner.time, ct).ConfigureAwait(false);
            }
        }

        private async Task DownloadPartAsync(HttpClient client, UpdateDownloadRoute route, int index,
            IProgress<UpdateDownloadProgress>? progress, bool switchOnSlow, CancellationToken ct)
        {
            long from = (long)index * owner.options.SegmentBytes;
            long length = PartLength(index);
            using var request = Request(route.Address, from, from + length - 1);
            request.Headers.IfRange = new RangeConditionHeaderValue(validator!);
            using var timeout = new CancellationTokenSource(route.IsProxy ? TimeSpan.FromSeconds(10) : owner.options.FirstByteTimeout, owner.time);
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                TimeSpan delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - owner.time.GetUtcNow() : TimeSpan.FromMilliseconds(250));
                if (delay > TimeSpan.FromSeconds(30)) throw new SegmentCooldownException();
                throw new SegmentThrottledException(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(250));
            }
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable))
                response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent || response.Headers.ETag?.IsWeak != false ||
                response.Headers.ETag.Tag != validator!.Tag)
                throw new InvalidSegmentException();
            try
            {
                if (ValidateResponse(response, from, release.Size) != length || response.Content.Headers.ContentRange?.To != from + length - 1 ||
                    response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidSegmentException();
            }
            catch (InvalidDataException) { throw new InvalidSegmentException(); }
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(PartPath(index), FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true);
            byte[] buffer = new byte[81920];
            long total = 0;
            byte first = 0;
            while (true)
            {
                using var idle = new CancellationTokenSource(route.IsProxy ? owner.options.ProxyIdleTimeout : owner.options.IdleTimeout, owner.time);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
                int count = await input.ReadAsync(buffer, read.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (total + count > length) throw new InvalidSegmentException();
                if (from == 0) ValidatePrefix(buffer, count, total, ref first);
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                total += count;
                Interlocked.Exchange(ref received[index], total);
                Interlocked.Add(ref networkBytes, count);
                Report(progress, route, switchOnSlow);
            }
            if (total != length) throw new IOException("Truncated update segment.");
        }

        private void Report(IProgress<UpdateDownloadProgress>? progress, UpdateDownloadRoute route, bool switchOnSlow, bool force = false)
        {
            lock (reportLock)
            {
                long now = owner.time.GetTimestamp();
                double seconds = owner.time.GetElapsedTime(windowStarted, now).TotalSeconds;
                speed = (Interlocked.Read(ref networkBytes) - windowBytes) / Math.Max(seconds, 0.001);
                if (switchOnSlow && seconds >= owner.options.SlowWindow.TotalSeconds && speed < owner.options.SlowMinimumSpeed)
                    throw new SlowDownloadException();
                if (seconds >= owner.options.SlowWindow.TotalSeconds)
                {
                    windowStarted = now;
                    windowBytes = Interlocked.Read(ref networkBytes);
                }
                if (!force && owner.time.GetElapsedTime(lastReport, now).TotalMilliseconds < 150) return;
                lastReport = now;
                progress?.Report(new(UpdateDownloadStage.Downloading, route.Id, ReceivedBytes(), release.Size, speed)
                { Connections = concurrency });
            }
        }

        private async Task SavePrefixAsync(CancellationToken ct)
        {
            int count = 0;
            while (count < complete.Length && complete[count]) count++;
            await AssembleAsync(count, ct).ConfigureAwait(false);
        }

        private async Task AssembleAsync(int count, CancellationToken ct)
        {
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true);
            for (int index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                await using var input = new FileStream(PartPath(index), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                if (input.Length != PartLength(index)) throw new InvalidDataException("Update segment checkpoint is incomplete.");
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }

        public void Reset()
        {
            for (int index = 0; index < complete.Length; index++) File.Delete(PartPath(index));
            complete = [];
            received = [];
            validator = null;
            routeId = null;
        }

        public void Dispose() => Reset();
    }

    private sealed class InvalidSegmentException() : IOException("The server did not return the requested update segment.");
    private sealed class SegmentCooldownException() : HttpRequestException("Update route has a long retry cooldown.");
    private sealed class SegmentThrottledException(TimeSpan delay) : IOException("Update segment rate limited.")
    {
        public TimeSpan Delay { get; } = delay;
    }
}
