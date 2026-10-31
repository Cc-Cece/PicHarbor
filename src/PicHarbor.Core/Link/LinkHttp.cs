using System.Text;

namespace PicHarbor.Core.Link;

internal sealed class LinkRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];

    public static async Task<LinkRequest> ReadAsync(Stream stream, int maxBody, CancellationToken cancellationToken)
    {
        var buffer = new LinkLineBuffer(stream);
        string? start = await buffer.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(start))
        {
            throw new IOException("空的连接请求。");
        }

        string[] parts = start.Split(' ');
        if (parts.Length < 2)
        {
            throw new IOException("无法识别的传输请求。");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? line = await buffer.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new IOException("传输请求不完整。");
            }

            if (line.Length == 0)
            {
                break;
            }

            int split = line.IndexOf(':');
            if (split > 0)
            {
                headers[line[..split].Trim()] = line[(split + 1)..].Trim();
            }
        }

        int length = 0;
        if (headers.TryGetValue("Content-Length", out string? text)
            && !int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length))
        {
            throw new IOException("传输长度无效。");
        }

        if (length < 0 || length > maxBody)
        {
            throw new IOException("传输内容过大。");
        }

        byte[] body = length == 0 ? [] : await buffer.ReadAsync(length, cancellationToken).ConfigureAwait(false);
        string path = parts[1];
        int query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        return new LinkRequest { Method = parts[0], Path = Uri.UnescapeDataString(path), Headers = headers, Body = body };
    }
}

internal static class LinkResponses
{
    public static Task WriteAsync(Stream stream, int status, string reason, string json, CancellationToken cancellationToken) =>
        WriteAsync(stream, status, reason, Encoding.UTF8.GetBytes(json), cancellationToken);

    public static async Task WriteAsync(Stream stream, int status, string reason, byte[] body, CancellationToken cancellationToken)
    {
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
