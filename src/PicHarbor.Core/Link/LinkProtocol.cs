using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PicHarbor.Core.Link;

/// <summary>Shared constants for the phone-to-computer transfer.</summary>
public static class LinkProtocol
{
    /// <summary>Protocol version spoken by this build.</summary>
    public const int Version = 1;

    /// <summary>UDP multicast group used to announce a listening computer.</summary>
    public const string BeaconGroup = "239.255.77.77";

    /// <summary>UDP port used by the announcement.</summary>
    public const int BeaconPort = 47877;

    /// <summary>Builds the HMAC input that binds a pairing code to this computer's certificate.</summary>
    public static string PairMessage(string deviceId, string nonce, string fingerprint) =>
        $"picharbor-link-v1\npair\n{deviceId}\n{nonce}\n{fingerprint}";

    /// <summary>Bytes signed by a returning phone.</summary>
    public static byte[] SessionMessage(string nonce, string fingerprint) =>
        Encoding.UTF8.GetBytes($"picharbor-link-v1\nsession\n{nonce}\n{fingerprint}");

    /// <summary>Computes the lowercase hex HMAC for a pairing code.</summary>
    public static string PairMac(string code, string deviceId, string nonce, string fingerprint)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(code.Trim()));
        byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(PairMessage(deviceId, nonce, fingerprint)));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>True when <paramref name="id"/> is safe to use as a blob name.</summary>
    public static bool IsSafeId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 80 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
}

internal static class LinkJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Text(object value) => JsonSerializer.Serialize(value, Options);
}

internal static class LinkBytes
{
    public static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    public static bool FixedTimeEquals(string left, string right)
    {
        byte[] a = Encoding.UTF8.GetBytes(left.Trim());
        byte[] b = Encoding.UTF8.GetBytes(right.Trim());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

internal sealed class LinkLineBuffer
{
    private readonly Stream stream;
    private readonly byte[] buffer = new byte[8192];
    private int count;
    private int offset;

    public LinkLineBuffer(Stream stream) => this.stream = stream;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            if (offset >= count)
            {
                offset = 0;
                count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return builder.Length == 0 ? null : builder.ToString();
                }
            }

            byte value = buffer[offset++];
            if (value == (byte)'\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }

            builder.Append((char)value);
            if (builder.Length > 64 * 1024)
            {
                throw new IOException("请求头过长。");
            }
        }
    }

    public async Task<byte[]> ReadAsync(int length, CancellationToken cancellationToken)
    {
        byte[] body = new byte[length];
        int filled = 0;
        int pending = Math.Min(count - offset, length);
        if (pending > 0)
        {
            Buffer.BlockCopy(buffer, offset, body, 0, pending);
            offset += pending;
            filled = pending;
        }

        while (filled < length)
        {
            int read = await stream.ReadAsync(body.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("传输内容中断。");
            }

            filled += read;
        }

        return body;
    }
}

internal static class LinkTcp
{
    public static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
