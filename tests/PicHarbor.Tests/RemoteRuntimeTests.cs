using System.Text.Json.Nodes;
using PicHarbor.Core.Remote;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests;

public sealed class RemoteRuntimeTests
{
    [Fact]
    public async Task Packaged_python_round_trips_unicode_and_deduplicates_events_offline()
    {
        string directory = Path.Combine(Path.GetTempPath(), "picharbor-remote-" + Guid.NewGuid());
        try
        {
            var runtime = new RemoteRuntime(directory);
            var result = await runtime.CallAsync("resolve", new JsonObject
            {
                ["scope"] = "本机测试",
                ["values"] = new JsonObject { ["CurrentLanguage"] = "zh-CN", ["GooglePhotosThreads"] = 4, ["GooglePhotosAccountEmail"] = "never-forward@example.com" }
            }, TestContext.Current.CancellationToken);
            result["ok"]!.GetValue<bool>().ShouldBeTrue();
            result["values"]!["CurrentLanguage"]!.GetValue<string>().ShouldBe("zh-CN");
            result["values"]!.AsObject().ContainsKey("GooglePhotosAccountEmail").ShouldBeFalse();
            for (int i = 0; i < 2; i++)
            {
                var recorded = await runtime.CallAsync("event", new JsonObject { ["name"] = "backup_success", ["id"] = "local-task", ["duration_ms"] = 90000, ["bytes"] = 20000000 }, TestContext.Current.CancellationToken);
                recorded["ok"]!.GetValue<bool>().ShouldBeTrue();
            }
            var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "Remote", "state.json"), TestContext.Current.CancellationToken))!;
            var counts = state["daily"]!.AsObject().First().Value!["events"]!;
            counts["backup_success"]!.GetValue<int>().ShouldBe(1);
            counts["duration_1m_10m"]!.GetValue<int>().ShouldBe(1);
            counts["size_10mb_100mb"]!.GetValue<int>().ShouldBe(1);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
