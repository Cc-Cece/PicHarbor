using System.Net;

namespace PicHarbor.Core.Updates;

/// <summary>Official GitHub asks the client to wait; changing routes must not bypass this cooldown.</summary>
public sealed class UpdateRateLimitException : HttpRequestException
{
    /// <summary>Earliest advertised retry time.</summary>
    public DateTimeOffset RetryAfter { get; }
    /// <summary>Creates a retryable error without exposing any proxy configuration.</summary>
    public UpdateRateLimitException(DateTimeOffset retryAfter)
        : base($"GitHub rate limit: retry after {retryAfter:O}.", null, HttpStatusCode.TooManyRequests) => RetryAfter = retryAfter;
}

/// <summary>Bounds official metadata requests and never retries a rate limit through another exit IP.</summary>
public sealed class OfficialFallbackHandler(HttpClient direct, Func<HttpClient> proxyFactory,
    Func<Uri, bool> hasProxy, TimeProvider? clock = null, IDictionary<string, DateTimeOffset>? cooldowns = null) : HttpMessageHandler
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private DateTimeOffset blockedUntil;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (cooldowns?.TryGetValue(uri.Host, out var shared) == true && shared > blockedUntil) blockedUntil = shared;
        if (time.GetUtcNow() < blockedUntil)
            throw new UpdateRateLimitException(blockedUntil);
        if (uri.Scheme != "https" || (uri.Host != "api.github.com" && uri.Host != "github.com"))
            throw new HttpRequestException("Official update metadata must come from GitHub.");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(attempt == 0 ? 5 : 10), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            using var copy = new HttpRequestMessage(request.Method, uri);
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            HttpClient? proxy = null;
            try
            {
                var client = attempt == 0 ? direct : proxy = proxyFactory();
                var response = await client.SendAsync(copy, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || retry is not null ||
                        (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))
                    {
                        blockedUntil = retry?.Date ?? time.GetUtcNow() + (retry?.Delta ?? TimeSpan.FromMinutes(1));
                        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out long reset))
                            blockedUntil = DateTimeOffset.FromUnixTimeSeconds(reset) > blockedUntil ? DateTimeOffset.FromUnixTimeSeconds(reset) : blockedUntil;
                        if (cooldowns is not null) cooldowns[uri.Host] = blockedUntil;
                        response.Dispose();
                        throw new UpdateRateLimitException(blockedUntil);
                    }
                    return response;
                }
                if (attempt == 0 && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout) && hasProxy(uri))
                { response.Dispose(); continue; }
                return response;
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested &&
                ex is not UpdateRateLimitException && (ex is HttpRequestException or OperationCanceledException) && hasProxy(uri)) { }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            { throw new HttpRequestException("Official GitHub request timed out.", ex); }
            finally { proxy?.Dispose(); }
        }
        throw new HttpRequestException("Official GitHub connection failed.");
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing) direct.Dispose();
        base.Dispose(disposing);
    }
}
