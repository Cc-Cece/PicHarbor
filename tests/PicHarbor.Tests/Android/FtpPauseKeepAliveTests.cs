using System.Net;
using System.Net.Sockets;
using System.Text;
using PicHarbor.Core.Android;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Android;

public sealed class FtpPauseKeepAliveTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Paused_connection_uses_noop_and_reports_a_rejected_keepalive(bool accepted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        async Task Server()
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 Ready".AsMemory(), timeout.Token);
            (await reader.ReadLineAsync(timeout.Token)).ShouldBe("USER anonymous");
            await writer.WriteLineAsync("230 Logged in".AsMemory(), timeout.Token);
            (await reader.ReadLineAsync(timeout.Token)).ShouldBe("TYPE I");
            await writer.WriteLineAsync("200 Binary".AsMemory(), timeout.Token);
            (await reader.ReadLineAsync(timeout.Token)).ShouldBe("NOOP");
            await writer.WriteLineAsync((accepted ? "200 OK" : "500 Rejected").AsMemory(), timeout.Token);
        }
        var server = Server();
        using var ftp = new SimpleFtpClient();
        await ftp.ConnectAsync("127.0.0.1", port, "anonymous", "", timeout.Token);
        if (accepted) await ftp.KeepAliveAsync(timeout.Token);
        else await Should.ThrowAsync<IOException>(() => ftp.KeepAliveAsync(timeout.Token));
        await server;
    }
}
