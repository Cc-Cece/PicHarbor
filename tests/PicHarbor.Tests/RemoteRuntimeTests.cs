using System.Text.Json.Nodes;
using PicHarbor.Core.Remote;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests;

public sealed class RemoteRuntimeTests
{
    [Fact]
    public async Task Packaged_python_round_trips_unicode_and_buffers_facts_without_builtin_collection_policy()
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
            (state["daily"]?.AsObject().Count ?? 0).ShouldBe(0);
            File.Exists(Path.Combine(directory, "harbor_telemetry.py")).ShouldBeTrue();
            File.Exists(Path.Combine(directory, "Remote", "telemetry.sqlite")).ShouldBeTrue();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
