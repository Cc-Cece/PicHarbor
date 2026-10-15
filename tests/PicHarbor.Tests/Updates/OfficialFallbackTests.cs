using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using PicHarbor.Core.Updates;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Updates;

public sealed class OfficialFallbackTests
{
    private static readonly Uri Official = new("https://api.github.com/repos/Cc-Cece/PicHarbor/releases/latest");

    [Fact]
    public async Task OfficialFailureFallsBackToProxyWithoutEverUsingMirrors()
    {
        var calls = new List<string>();
        using var direct = Client((request, _) => { calls.Add("direct:" + request.RequestUri!.Host); throw new HttpRequestException(); });
        using var outer = new HttpClient(new OfficialFallbackHandler(direct, () => Client((request, _) =>
        { calls.Add("proxy:" + request.RequestUri!.Host); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("official") }); }), _ => true));
        (await outer.GetStringAsync(Official, TestContext.Current.CancellationToken)).ShouldBe("official");
        calls.ShouldBe(new[] { "direct:api.github.com", "proxy:api.github.com" });
    }

    [Fact]
    public async Task RateLimitIsNotRetriedAndBlocksRepeatedRequestsUntilRetryAfter()
    {
        var clock = new FakeTimeProvider();
        int directCalls = 0;
        using var direct = Client((_, _) =>
        {
            directCalls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        });
        using var outer = new HttpClient(new OfficialFallbackHandler(direct, () => throw new Exception("Must not bypass rate limit"), _ => true, clock));
        await Should.ThrowAsync<UpdateRateLimitException>(() => outer.GetAsync(Official, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<UpdateRateLimitException>(() => outer.GetAsync(Official, TestContext.Current.CancellationToken));
        directCalls.ShouldBe(1);
        clock.Advance(TimeSpan.FromSeconds(61));
        await Should.ThrowAsync<UpdateRateLimitException>(() => outer.GetAsync(Official, TestContext.Current.CancellationToken));
        directCalls.ShouldBe(2);
    }

    [Fact]
    public async Task FiveSecondDirectBudgetAllowsProxyFallback()
    {
        var clock = new FakeTimeProvider();
        using var direct = Client(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); });
        using var outer = new HttpClient(new OfficialFallbackHandler(direct,
            () => Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("proxy") })), _ => true, clock));
        var pending = outer.GetStringAsync(Official, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));
        (await pending).ShouldBe("proxy");
    }

    [Fact]
    public async Task CooldownSurvivesCreationOfANewCheckClient()
    {
        var clock = new FakeTimeProvider();
        var cooldowns = new Dictionary<string, DateTimeOffset>();
        using (var first = new HttpClient(new OfficialFallbackHandler(Client((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        }), () => throw new Exception("No proxy retry"), _ => true, clock, cooldowns)))
            await Should.ThrowAsync<UpdateRateLimitException>(() => first.GetAsync(Official, TestContext.Current.CancellationToken));
        using var second = new HttpClient(new OfficialFallbackHandler(Client((_, _) => throw new Exception("No network during cooldown")),
            () => throw new Exception("No proxy retry"), _ => true, clock, cooldowns));
        await Should.ThrowAsync<UpdateRateLimitException>(() => second.GetAsync(Official, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExternalCancellationNeverTriggersFallback()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var direct = Client(async (_, ct) => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); });
        using var outer = new HttpClient(new OfficialFallbackHandler(direct, () => throw new Exception("Cancelled"), _ => true));
        await Should.ThrowAsync<OperationCanceledException>(() => outer.GetAsync(Official, cancel.Token));
    }

    [Fact]
    public async Task MetadataRejectsMirrorHostsAndRedirectsNeverForwardCredentialsOrDowngrade()
    {
        using var direct = Client((_, _) => throw new Exception("No request should be sent"));
        using var outer = new HttpClient(new OfficialFallbackHandler(direct, () => throw new Exception(), _ => false));
        await Should.ThrowAsync<HttpRequestException>(() => outer.GetAsync("https://gh-proxy.com/https://api.github.com/", TestContext.Current.CancellationToken));
        int calls = 0;
        using var redirects = new HttpClient(new HttpsRedirectHandler(new Handler((request, _) =>
        {
            request.Headers.Authorization.ShouldBeNull();
            request.Headers.Contains("Cookie").ShouldBeFalse();
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://unsafe.example/file");
            return Task.FromResult(response);
        })));
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://gh-proxy.com/file");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-for-mirrors");
        message.Headers.Add("Cookie", "private=value");
        await Should.ThrowAsync<HttpRequestException>(() => redirects.SendAsync(message, TestContext.Current.CancellationToken));
        calls.ShouldBe(1);
        using var handler = UpdateHttp.CreateDirectHandler();
        handler.UseProxy.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        handler.Credentials.ShouldBeNull();
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
