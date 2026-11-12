using System.Text.Json;
using PicHarbor.Core.Link;
using Shouldly;
using Xunit;
namespace PicHarbor.Tests.Link;
public class LinkScopeTests
{
    [Fact] public void Structured_scope_roundtrips_without_losing_open_dates_or_collection_ids()
    {
        var scope = new LinkScopeSpec { Mode = "advanced", Albums = ["primary|DCIM/Camera"], DateEnabled = true, To = "2026-11-01", Videos = false };
        scope.Validate().ShouldBeNull();
        scope.ToWire().ShouldStartWith("scope2:");
        var restored = JsonSerializer.Deserialize<LinkScopeSpec>(scope.ToWire()[7..], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        restored.Albums.ShouldBe(scope.Albums); restored.To.ShouldBe(scope.To); restored.From.ShouldBeNull(); restored.Videos.ShouldBeFalse();
    }
    [Fact] public void Invalid_scopes_are_rejected_before_a_phone_is_asked()
    {
        (new LinkScopeSpec { Mode = "advanced" }).Validate().ShouldNotBeNull();
        (new LinkScopeSpec { Photos = false, Videos = false }).Validate().ShouldNotBeNull();
        (new LinkScopeSpec { DateEnabled = true, From = "2026-02-30" }).Validate().ShouldNotBeNull();
        (new LinkScopeSpec { DateEnabled = true, From = "2026-11-02", To = "2026-11-01" }).Validate().ShouldNotBeNull();
        (new LinkScopeSpec { MinImageKb = 1 }).Validate().ShouldNotBeNull();
    }
}
