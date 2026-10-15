using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PicHarbor.Core.Updates;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Updates;

public sealed class ReleaseUpdateTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2.3+abcdef", "1.2.3.0")]
    [InlineData("2.13.1.1", "2.13.1.1")]
    [InlineData("v1.2.3-beta.1", null)]
    [InlineData("garbage", null)]
    [InlineData("1.2", null)]
    public void VersionsAreNormalizedAndPreviewsExcluded(string tag, string? expected)
        => (UpdatePolicy.ParseVersion(tag)?.ToString()).ShouldBe(expected);

    [Fact]
    public void ComparisonIsNumericAndDevelopmentVersionIsRecognized()
    {
        (UpdatePolicy.ParseVersion("v1.10.0") > UpdatePolicy.ParseVersion("v1.9.0")).ShouldBeTrue();
        UpdatePolicy.ParseVersion("1.2.3-alpha.1+abc", false).ShouldBe(new Version(1, 2, 3, 0));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void OnlyReadyScheduledElevatedExitShowsDialog(bool ready, bool scheduled, bool elevated, bool expected)
        => UpdatePolicy.ShowExitPrompt(ready, scheduled, elevated).ShouldBe(expected);

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public void ConsentRetainsBothChoicesEvenWhenCancelled(bool accepted, bool skip, bool disable, bool installs)
    {
        var result = UpdatePolicy.ApplyConsent(UpdateMode.Automatic, "v1.0.0", "v1.2.3", accepted, skip, disable);
        result.Install.ShouldBe(installs);
        result.Mode.ShouldBe(disable ? UpdateMode.Manual : UpdateMode.Automatic);
        result.SkippedVersion.ShouldBe(skip ? "v1.2.3" : "v1.0.0");
    }

    [Fact]
    public void ExactPairFromSameReleaseIsRequired()
    {
        var release = ReleaseClient.Parse(ReleaseJson());
        release.ShouldNotBeNull();
        release.Tag.ShouldBe("v1.2.3");
        Should.Throw<InvalidOperationException>(() => ReleaseClient.Parse(ReleaseJson(includeChecksum: false)));
        Should.Throw<InvalidDataException>(() => ReleaseClient.Parse(ReleaseJson(host: "evil.example")));
        ReleaseClient.Parse(ReleaseJson(prerelease: true)).ShouldBeNull();
        ReleaseClient.Parse(ReleaseJson(draft: true)).ShouldBeNull();
    }

    [Fact]
    public async Task VersionCheckDoesNotRequireAnInstallableAttachmentPair()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent(ReleaseJson(includeChecksum: false).GetRawText(), Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var release = await new ReleaseClient(http).LatestReleaseAsync(TestContext.Current.CancellationToken);
        release.ShouldNotBeNull();
        release.Tag.ShouldBe("v1.2.3");
        release.Version.ShouldBe(new Version(1, 2, 3, 0));
        Should.Throw<InvalidOperationException>(() => release.GetUpdate());
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public void ChecksumMustIdentifyGuiExecutable()
    {
        string hash = new('a', 64);
        UpdatePolicy.ParseChecksum(hash + "  PicHarbor.Gui.exe\n").ShouldBe(hash);
        Should.Throw<InvalidDataException>(() => UpdatePolicy.ParseChecksum(hash + "  picharbor.exe"));
        Should.Throw<InvalidDataException>(() => UpdatePolicy.ParseChecksum("bad  PicHarbor.Gui.exe"));
    }

    [Fact]
    public async Task RateLimitIsNotRetriedAndNoTokenIsSent()
    {
        using var handler = new Handler(request =>
        {
            request.Headers.Authorization.ShouldBeNull();
            request.Headers.UserAgent.ToString().ShouldContain("PicHarbor");
            return new(HttpStatusCode.Forbidden);
        });
        using var http = new HttpClient(handler);
        await Should.ThrowAsync<HttpRequestException>(() => new ReleaseClient(http).LatestAsync(default));
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task DownloadRejectsTruncationAndOverrun()
    {
        foreach (int actual in new[] { 2, 4 })
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".partial");
            try
            {
                using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[actual]) });
                using var http = new HttpClient(handler);
                var release = ReleaseClient.Parse(ReleaseJson())!;
                await Should.ThrowAsync<InvalidDataException>(() => new ReleaseClient(http).DownloadAsync(release, path, null, default));
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public async Task DownloadCancellationPropagatesWithoutInstallingAnything()
    {
        using var handler = new Handler(_ => throw new OperationCanceledException());
        using var http = new HttpClient(handler);
        await Should.ThrowAsync<OperationCanceledException>(() => new ReleaseClient(http).LatestAsync(default));
    }

    [Fact]
    public async Task VerificationChecksEmbeddedVersionAsWellAsHashAndSize()
    {
        string path = typeof(ReleaseUpdate).Assembly.Location;
        var version = UpdatePolicy.ParseVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion, false)!;
        string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
        var release = new ReleaseUpdate("v" + version, version, new("https://github.com"), new("https://github.com"), new FileInfo(path).Length);
        // Development metadata is not accepted as a stable release binary.
        if (UpdatePolicy.ParseVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion) is not null)
            await UpdatePolicy.VerifyAsync(path, release, hash, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidDataException>(() => UpdatePolicy.VerifyAsync(path, release, new string('0', 64), default));
        await Should.ThrowAsync<InvalidDataException>(() => UpdatePolicy.VerifyAsync(path, release with { Size = 1 }, hash, default));
        await Should.ThrowAsync<InvalidDataException>(() => UpdatePolicy.VerifyAsync(path, release with { Version = new Version(999, 0, 0, 0) }, hash, default));
    }

    private static JsonElement ReleaseJson(bool includeChecksum = true, string host = "github.com", bool prerelease = false, bool draft = false)
    {
        var assets = new List<object> { new { name = ReleaseUpdate.AssetName, size = 3, browser_download_url = $"https://{host}/Cc-Cece/PicHarbor/releases/download/v1.2.3/PicHarbor.Gui.exe" } };
        if (includeChecksum) assets.Add(new { name = ReleaseUpdate.AssetName + ".sha256", size = 85, browser_download_url = $"https://{host}/Cc-Cece/PicHarbor/releases/download/v1.2.3/PicHarbor.Gui.exe.sha256" });
        return JsonSerializer.SerializeToElement(new { tag_name = "v1.2.3", draft, prerelease, assets });
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(respond(request)); }
    }
}
