using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PicHarbor.Core.Link;

internal sealed class LinkClient : IAsyncDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly string fingerprint;
    private string? token;

    public LinkClient(string host, int port, string fingerprint)
    {
        this.host = host;
        this.port = port;
        this.fingerprint = fingerprint;
    }

    public async Task PairAsync(string deviceId, string name, string code, string publicKey, CancellationToken cancellationToken)
    {
        string nonce = LinkBytes.Hex(RandomNumberGenerator.GetBytes(16));
        string mac = LinkProtocol.PairMac(code, deviceId, nonce, fingerprint);
        string body = LinkJson.Text(new { deviceId, deviceName = name, platform = "test", nonce, mac, publicKey });
        using var json = await SendAsync("POST", "/v1/pair", Encoding.UTF8.GetBytes(body), null, cancellationToken).ConfigureAwait(false);
        token = json.RootElement.GetProperty("token").GetString();
    }

    public async Task<JsonDocument> ManifestAsync(string model, object[] items, CancellationToken cancellationToken) =>
        await SendAsync("POST", "/v1/manifest", Encoding.UTF8.GetBytes(LinkJson.Text(new { model, items })), token, cancellationToken).ConfigureAwait(false);

    public async Task UploadAsync(string id, string name, byte[] content, long created, CancellationToken cancellationToken)
    {
        string sha = LinkBytes.Hex(SHA256.HashData(content));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-File-Name"] = Uri.EscapeDataString(name),
            ["X-Size"] = content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Offset"] = "0",
            ["X-Created"] = created.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Sha256"] = sha,
        };
        using var json = await SendAsync("PUT", "/v1/blobs/" + id, content, token, cancellationToken, headers).ConfigureAwait(false);
        if (!json.RootElement.GetProperty("complete").GetBoolean())
        {
            throw new IOException("文件没有传完。");
        }
    }

    public async ValueTask DisposeAsync() => await Task.CompletedTask;

    private async Task<JsonDocument> SendAsync(string method, string path, byte[] body, string? bearer, CancellationToken cancellationToken, Dictionary<string, string>? extra = null)
    {
        using var tcp = await LinkTcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var raw = tcp.GetStream();
        await using var ssl = new SslStream(raw, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(host, null, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, checkCertificateRevocation: false).ConfigureAwait(false);
        string actual = LinkBytes.Hex(SHA256.HashData(ssl.RemoteCertificate?.GetRawCertData() ?? []));
        if (!string.Equals(actual, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationException("电脑证书和二维码不一致。");
        }

        var head = new StringBuilder();
        head.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\nHost: ").Append(host).Append("\r\nContent-Length: ").Append(body.Length).Append("\r\nConnection: close\r\n");
        if (!string.IsNullOrEmpty(bearer))
        {
            head.Append("Authorization: Bearer ").Append(bearer).Append("\r\n");
        }

        if (extra is not null)
        {
            foreach (var header in extra)
            {
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
        }

        head.Append("\r\n");
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await ssl.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        var response = await LinkRequest.ReadAsync(ssl, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (response.Path != "200")
        {
            throw new IOException(Encoding.UTF8.GetString(response.Body));
        }

        return JsonDocument.Parse(response.Body);
    }
}
