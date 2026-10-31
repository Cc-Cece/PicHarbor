using PicHarbor.Core.GooglePhotos;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.GooglePhotos;

public sealed class GooglePhotosConfigTests
{
    [Fact]
    public void Default_auth_method_is_OAuthCookie()
    {
        var config = new GooglePhotosSyncConfig();
        config.AuthMethod.ShouldBe(GooglePhotosAuthMethod.OAuthCookie);
        config.EffectiveAuthCredential.ShouldBe(string.Empty);
    }

    [Fact]
    public void EffectiveAuthCredential_uses_OAuthCookie_when_OAuthCookie_mode_and_not_exchanged()
    {
        var config = new GooglePhotosSyncConfig
        {
            AuthMethod = GooglePhotosAuthMethod.OAuthCookie,
            OAuthTokenCookie = "cookie_token_123",
            AuthData = ""
        };

        config.EffectiveAuthCredential.ShouldBe("cookie_token_123");
    }

    [Fact]
    public void EffectiveAuthCredential_prioritizes_reusable_AuthData_even_in_OAuthCookie_mode()
    {
        var config = new GooglePhotosSyncConfig
        {
            AuthMethod = GooglePhotosAuthMethod.OAuthCookie,
            OAuthTokenCookie = "cookie_token_123",
            AuthData = "androidId=123&Token=master_token_456"
        };

        config.EffectiveAuthCredential.ShouldBe("androidId=123&Token=master_token_456");
    }

    [Fact]
    public void EffectiveAuthCredential_prefers_AuthData_when_AndroidAuthData_mode()
    {
        var config = new GooglePhotosSyncConfig
        {
            AuthMethod = GooglePhotosAuthMethod.AndroidAuthData,
            OAuthTokenCookie = "cookie_token_123",
            AuthData = "androidId=123&Email=test@gmail.com"
        };

        config.EffectiveAuthCredential.ShouldBe("androidId=123&Email=test@gmail.com");
    }

    [Fact]
    public void EffectiveAuthCredential_falls_back_when_current_credential_empty()
    {
        var config = new GooglePhotosSyncConfig
        {
            AuthMethod = GooglePhotosAuthMethod.OAuthCookie,
            OAuthTokenCookie = "",
            AuthData = "androidId=123"
        };

        config.EffectiveAuthCredential.ShouldBe("androidId=123");
    }

    [Theory]
    [InlineData("7890", "http://127.0.0.1:7890")]
    [InlineData(":7890", "http://127.0.0.1:7890")]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("192.168.1.100:1080", "http://192.168.1.100:1080")]
    [InlineData("http://127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080")]
    [InlineData("socks5h://127.0.0.1:1080", "socks5h://127.0.0.1:1080")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeProxy_formats_proxy_correctly(string input, string expected)
    {
        GooglePhotosSyncEngine.NormalizeProxy(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("7890", "--proxy-server=http://127.0.0.1:7890")]
    [InlineData(":7890", "--proxy-server=http://127.0.0.1:7890")]
    [InlineData("http://127.0.0.1:7890", "--proxy-server=http://127.0.0.1:7890")]
    [InlineData("socks5://127.0.0.1:1080", "--proxy-server=socks5://127.0.0.1:1080")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void BuildWebView2ProxyArguments_formats_argument_correctly(string? input, string expected)
    {
        GooglePhotosSyncEngine.BuildWebView2ProxyArguments(input).ShouldBe(expected);
    }

    [Fact]
    public async Task TestProxyAsync_returns_error_when_local_proxy_not_running()
    {
        // Port 59999 is unlikely to be listening locally
        var result = await GooglePhotosSyncEngine.TestProxyAsync("http://127.0.0.1:59999", TestContext.Current.CancellationToken);
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("无法连接至本地代理服务");
    }

    [Fact]
    public void LocateBridgeScript_extracts_bundle_to_LocalApplicationData_runtime()
    {
        string bridgePath = GooglePhotosSyncEngine.LocateBridgeScript();
        File.Exists(bridgePath).ShouldBeTrue();
        bridgePath.ShouldStartWith(GooglePhotosSyncEngine.RuntimeDirectory);
        Directory.Exists(Path.Combine(GooglePhotosSyncEngine.RuntimeDirectory, "gpmc")).ShouldBeTrue();
        Directory.Exists(Path.Combine(GooglePhotosSyncEngine.RuntimeDirectory, "blackboxprotobuf")).ShouldBeTrue();
    }

    [Fact]
    public async Task Bundled_python_imports_gpmc_without_a_system_interpreter()
    {
        GooglePhotosSyncEngine.LocateBridgeScript();
        string pythonExe = GooglePhotosSyncEngine.LocatePythonExecutable();
        pythonExe.ShouldStartWith(GooglePhotosSyncEngine.PythonDirectory);
        File.Exists(pythonExe).ShouldBeTrue();

        string pthPath = Directory.GetFiles(GooglePhotosSyncEngine.PythonDirectory, "python*._pth").Single();
        string pth = File.ReadAllText(pthPath);
        pth.ShouldContain("Lib/site-packages");
        pth.ShouldContain("../");
        pth.ShouldNotContain("import site");

        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = "-c \"import requests, rich, gpmc, blackboxprotobuf, gpmc.auth; print('ok')\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        using var process = System.Diagnostics.Process.Start(start);
        process.ShouldNotBeNull();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        process.ExitCode.ShouldBe(0, $"{stdout}\n{stderr}");
        stdout.ShouldContain("ok");
    }
}
