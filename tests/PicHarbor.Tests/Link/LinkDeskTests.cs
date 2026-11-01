using PicHarbor.Core.Link;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Link;

public sealed class LinkDeskTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Android_without_a_file_receives_one_code()
    {
        var desk = new LinkDesk();
        LinkHelloResult first = desk.Hello("key", "Pixel", "android", null, LinkIdentityState.Missing, true, false, Now);
        LinkHelloResult second = desk.Hello("key", "Pixel", "android", null, LinkIdentityState.Missing, true, false, Now.AddSeconds(1));
        first.Deliver.ShouldStartWith("PH-");
        second.Deliver.ShouldBeNull();
        second.ArchiveId.ShouldBe(first.ArchiveId);
    }

    [Fact]
    public void Unreadable_android_directory_does_not_mint_a_code()
    {
        var desk = new LinkDesk();
        LinkHelloResult result = desk.Hello("key", "Pixel", "android", null, LinkIdentityState.Unreadable, false, false, Now);
        result.Deliver.ShouldBeNull();
        result.ArchiveId.ShouldBeNull();
    }

    [Fact]
    public void Iphone_write_waits_until_the_phone_echoes_the_udid()
    {
        var desk = new LinkDesk();
        desk.NoteIphoneUdid("ABCDEFGH1234");
        desk.Hello("key", "iPhone", "ios", null, LinkIdentityState.Missing, false, false, Now)
            .Deliver.ShouldBeNull();
        desk.IphoneWriteOutcome("key", Now).ShouldBe(LinkWriteOutcome.DirectoryMissing);
        desk.Hello("key", "iPhone", "ios", null, LinkIdentityState.Missing, true, false, Now)
            .Deliver.ShouldBe("ABCDEFGH1234");
        desk.Hello("key", "iPhone", "ios", "ABCDEFGH1234", LinkIdentityState.Ok, true, false, Now.AddSeconds(1));
        desk.IphoneWriteOutcome("key", Now.AddSeconds(1)).ShouldBe(LinkWriteOutcome.Written);
        desk.Hello("key", "iPhone", "ios", "OTHERPHONE999", LinkIdentityState.Ok, true, false, Now.AddSeconds(2))
            .Deliver.ShouldBe("ABCDEFGH1234");
    }

    [Fact]
    public void A_new_key_replaces_the_old_key_for_the_same_phone()
    {
        var desk = new LinkDesk();
        desk.Hello("old", "iPhone", "ios", "ABCDEFGH1234", LinkIdentityState.Ok, true, false, Now);
        desk.Hello("new", "iPhone", "ios", "ABCDEFGH1234", LinkIdentityState.Ok, true, false, Now);
        desk.ArchiveId("old").ShouldBeNull();
        desk.ArchiveId("new").ShouldBe("ABCDEFGH1234");
    }

    [Fact]
    public void An_offer_expires_and_does_not_allow_upload()
    {
        var desk = new LinkDesk();
        desk.Hello("key", "iPhone", "ios", "ABCDEFGH1234", LinkIdentityState.Ok, true, false, Now);
        LinkOffer offer = desk.CreateOffer("key", "phone-to-computer", "all", 2, Now).ShouldNotBeNull();
        desk.Accept(offer.Id, "key", Now.AddMinutes(3)).ShouldBeFalse();
        desk.UploadAllowed("key", Now.AddMinutes(3)).ShouldBeFalse();
    }

    [Fact]
    public void A_confirmed_transfer_stays_open_until_it_finishes()
    {
        var desk = new LinkDesk();
        desk.Hello("key", "iPhone", "ios", "ABCDEFGH1234", LinkIdentityState.Ok, true, false, Now);
        LinkOffer offer = desk.CreateOffer("key", "phone-to-computer", "all", 2, Now).ShouldNotBeNull();
        desk.Accept(offer.Id, "key", Now.AddSeconds(10)).ShouldBeTrue();
        desk.UploadAllowed("key", Now.AddMinutes(30)).ShouldBeTrue();
        desk.CreateOffer("key", "phone-to-computer", "all", 1, Now.AddMinutes(30)).ShouldBeNull();
        desk.Complete("key");
        desk.UploadAllowed("key", Now.AddMinutes(30)).ShouldBeFalse();
        desk.CreateOffer("key", "phone-to-computer", "all", 1, Now.AddMinutes(30)).ShouldNotBeNull();
    }

    [Fact]
    public void Same_subnet_address_is_tried_first()
    {
        var ordered = LinkDesk.OrderAddresses(["10.0.0.4", "192.168.1.20", "172.16.0.2"], "192.168.1.40");
        ordered[0].ShouldBe("192.168.1.20");
    }
}
