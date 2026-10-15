using System.Net;

namespace PicHarbor.Core.Updates;

/// <summary>Shared transport for release metadata, assets and the installation worker.</summary>
public static class UpdateHttp
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> OfficialCooldowns = new();
    /// <summary>Creates a client using an operation's proxy snapshot; blank preserves the system proxy defaults.</summary>
    public static HttpClient CreateClient(string? configuredProxy, TimeSpan timeout)
        => new(new HttpsRedirectHandler(CreateHandler(configuredProxy))) { Timeout = timeout };

    /// <summary>Creates a true direct connection, without application, environment or system proxies.</summary>
    public static HttpClient CreateDirectClient()
        => new(new HttpsRedirectHandler(CreateDirectHandler())) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Exposes the direct transport policy for validation without making network requests.</summary>
    public static SocketsHttpHandler CreateDirectHandler()
        => new() { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };

    /// <summary>Creates the official-only metadata transport with bounded direct/proxy fallback.</summary>
    public static HttpClient CreateOfficialClient(string? configuredProxy)
        => new(new OfficialFallbackHandler(CreateDirectClient(),
            () => CreateClient(configuredProxy, Timeout.InfiniteTimeSpan), uri => HasProxy(configuredProxy, uri), cooldowns: OfficialCooldowns))
        { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Tests whether an explicit or system proxy is available, without contacting it.</summary>
    public static bool HasProxy(string? configuredProxy, Uri destination)
        => !string.IsNullOrWhiteSpace(configuredProxy) ||
            (!HttpClient.DefaultProxy.IsBypassed(destination) && HttpClient.DefaultProxy.GetProxy(destination) != destination);

    /// <summary>Supports the existing proxy setting's shorthand forms and HTTP/HTTPS/SOCKS proxy URLs.</summary>
    public static SocketsHttpHandler CreateHandler(string? configuredProxy)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = false, UseCookies = false };
        string normalized = GooglePhotos.GooglePhotosSyncEngine.NormalizeProxy(configuredProxy);
        if (normalized.Length == 0) return handler;
        if (normalized.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
            normalized = "socks5://" + normalized["socks5h://".Length..];
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var address) || string.IsNullOrEmpty(address.Host) ||
            address.Port is < 1 or > 65535 ||
            address.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            handler.Dispose();
            throw new ArgumentException("Invalid network proxy address.", nameof(configuredProxy));
        }
        var proxy = new WebProxy(address);
        if (address.UserInfo.Length > 0)
        {
            string[] credentials = address.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(credentials[0]),
                credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "");
        }
        handler.Proxy = proxy;
        handler.UseProxy = true;
        return handler;
    }
}

/// <summary>Bounds HTTPS redirects and strips end-server credentials from public update requests.</summary>
public sealed class HttpsRedirectHandler(HttpMessageHandler handler) : DelegatingHandler(handler)
{
    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Uri destination = request.RequestUri!;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (destination.Scheme != "https") throw new HttpRequestException("An update route attempted an insecure redirect.");
            using var next = new HttpRequestMessage(request.Method, destination);
            foreach (var header in request.Headers)
                if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await base.SendAsync(next, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null) return response;
            destination = new Uri(destination, response.Headers.Location);
            response.Dispose();
        }
        throw new HttpRequestException("Too many update redirects.");
    }
}
