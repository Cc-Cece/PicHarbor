using System.Net;

namespace PicHarbor.Core.Updates;

/// <summary>Shared transport for release metadata, assets and the installation worker.</summary>
public static class UpdateHttp
{
    /// <summary>Creates a client using an operation's proxy snapshot; blank preserves the system proxy defaults.</summary>
    public static HttpClient CreateClient(string? configuredProxy, TimeSpan timeout)
        => new(CreateHandler(configuredProxy)) { Timeout = timeout };

    /// <summary>Supports the existing proxy setting's shorthand forms and HTTP/HTTPS/SOCKS proxy URLs.</summary>
    public static SocketsHttpHandler CreateHandler(string? configuredProxy)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
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
