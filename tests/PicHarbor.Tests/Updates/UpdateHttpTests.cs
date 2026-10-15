using System.Net;
using System.Net.Sockets;
using System.Text;
using PicHarbor.Core.Updates;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Updates;

public sealed class UpdateHttpTests
{
    [Theory]
    [InlineData("7890", "http://127.0.0.1:7890/")]
    [InlineData(":7890", "http://127.0.0.1:7890/")]
    [InlineData(" 127.0.0.1:7890 ", "http://127.0.0.1:7890/")]
    [InlineData("http://localhost:8080", "http://localhost:8080/")]
    [InlineData("https://localhost:8080", "https://localhost:8080/")]
    [InlineData("socks5://localhost:1080", "socks5://localhost:1080/")]
    [InlineData("socks5h://localhost:1080", "socks5://localhost:1080/")]
    public void ExistingProxyFormatsAreApplied(string setting, string expected)
    {
        using var handler = UpdateHttp.CreateHandler(setting);
        handler.UseProxy.ShouldBeTrue();
        handler.Proxy.ShouldBeOfType<WebProxy>().Address!.AbsoluteUri.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UnconfiguredProxyKeepsSystemDefaults(string? setting)
    {
        using var handler = UpdateHttp.CreateHandler(setting);
        handler.UseProxy.ShouldBeTrue();
        handler.Proxy.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("ftp://localhost:1234")]
    [InlineData("http://localhost:99999")]
    public void InvalidExplicitProxyIsNotSilentlyBypassed(string setting)
        => Should.Throw<ArgumentException>(() => UpdateHttp.CreateHandler(setting));

    [Theory]
    [InlineData("check", "api.github.com:443")]
    [InlineData("checksum", "github.com:443")]
    [InlineData("download", "github.com:443")]
    public async Task EveryUpdateRequestUsesConfiguredProxyWithoutContactingGithub(string operation, string destination)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        async Task<string> ReceiveConnect()
        {
            using var connection = await listener.AcceptTcpClientAsync(ct);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string line = await reader.ReadLineAsync(ct) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Test proxy\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
            return line;
        }
        Task<string> received = ReceiveConnect();
        using var http = UpdateHttp.CreateClient($"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5));
        var client = new ReleaseClient(http);
        var release = new ReleaseUpdate("v1.2.3", new(1, 2, 3, 0),
            new("https://github.com/Cc-Cece/PicHarbor/releases/download/v1.2.3/PicHarbor.Gui.exe"),
            new("https://github.com/Cc-Cece/PicHarbor/releases/download/v1.2.3/PicHarbor.Gui.exe.sha256"), 3);
        string temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".partial");
        try
        {
            await Should.ThrowAsync<HttpRequestException>(async () =>
            {
                if (operation == "check") await client.LatestAsync(ct);
                else if (operation == "checksum") await client.ChecksumAsync(release, ct);
                else await client.DownloadAsync(release, temporary, null, ct);
            });
            (await received).ShouldBe($"CONNECT {destination} HTTP/1.1");
        }
        finally { File.Delete(temporary); }
    }
}
