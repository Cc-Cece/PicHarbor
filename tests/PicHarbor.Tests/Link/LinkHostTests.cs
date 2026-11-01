using System.Security.Cryptography;
using PicHarbor.Core.GooglePhotos;
using PicHarbor.Core.Link;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Link;

public sealed class LinkHostTests
{
    [Fact]
    public async Task Paired_phone_file_is_written_into_the_archive()
    {
        string root = Path.Combine(Path.GetTempPath(), "picharbor-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = LinkHost.Start(new LinkHostOptions
            {
                ArchiveRoot = root,
                FixedCode = "123456",
                Beacon = false,
                EphemeralCertificate = true,
            });
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecdsa.ExportParameters(false);
            byte[] x = parameters.Q.X ?? throw new InvalidOperationException("missing X");
            byte[] y = parameters.Q.Y ?? throw new InvalidOperationException("missing Y");
            byte[] point = new byte[65];
            point[0] = 4;
            x.CopyTo(point.AsSpan(33 - x.Length));
            y.CopyTo(point.AsSpan(65 - y.Length));
            var client = new LinkClient("127.0.0.1", host.Port, host.Fingerprint);
            await client.PairAsync("phoneone", "测试手机", "123456", Convert.ToBase64String(point), TestContext.Current.CancellationToken);
            await client.HelloAsync("PHONEARCHIVE01", "ios", true, TestContext.Current.CancellationToken);
            string offerId = await client.OfferAsync(1, TestContext.Current.CancellationToken);
            await client.AcceptAsync(offerId, TestContext.Current.CancellationToken);
            byte[] content = "picharbor-live-link"u8.ToArray();
            await client.UploadAsync("item1", "IMG_0001.jpg", content, 1_700_000_000, TestContext.Current.CancellationToken);
            string[] saved = Directory.GetFiles(root, "IMG_0001.jpg", SearchOption.AllDirectories);
            saved.Length.ShouldBe(1);
            File.ReadAllBytes(saved[0]).ShouldBe(content);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Upload_before_accept_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "picharbor-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = LinkHost.Start(new LinkHostOptions
            {
                ArchiveRoot = root,
                FixedCode = "123456",
                Beacon = false,
                EphemeralCertificate = true,
            });
            var client = await PairedClient(host);
            await client.HelloAsync("PHONEARCHIVE01", "ios", true, TestContext.Current.CancellationToken);
            IOException error = await Should.ThrowAsync<IOException>(() => client.UploadAsync("item1", "IMG_0001.jpg", "bytes"u8.ToArray(), 1_700_000_000, TestContext.Current.CancellationToken));
            error.Message.ShouldContain("need_accept");
            Directory.GetFiles(root, "IMG_0001.jpg", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task The_same_hash_is_not_stored_twice_and_finish_allows_another_request()
    {
        string root = Path.Combine(Path.GetTempPath(), "picharbor-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = LinkHost.Start(new LinkHostOptions
            {
                ArchiveRoot = root,
                FixedCode = "123456",
                Beacon = false,
                EphemeralCertificate = true,
            });
            var client = await PairedClient(host);
            await client.HelloAsync("PHONEARCHIVE01", "ios", true, TestContext.Current.CancellationToken);
            byte[] content = "same-photo"u8.ToArray();
            string first = await client.OfferAsync(2, TestContext.Current.CancellationToken);
            await client.AcceptAsync(first, TestContext.Current.CancellationToken);
            await client.UploadAsync("item1", "IMG_0001.jpg", content, 1_700_000_000, TestContext.Current.CancellationToken);
            await client.UploadAsync("item2", "IMG_0002.jpg", content, 1_700_000_000, TestContext.Current.CancellationToken);
            Directory.GetFiles(root, "IMG_*.jpg", SearchOption.AllDirectories).Length.ShouldBe(1);
            await client.FinishAsync(first, TestContext.Current.CancellationToken);
            string second = await client.OfferAsync(1, TestContext.Current.CancellationToken);
            second.ShouldNotBe(first);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task<LinkClient> PairedClient(LinkHost host)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(false);
        byte[] x = parameters.Q.X ?? throw new InvalidOperationException("missing X");
        byte[] y = parameters.Q.Y ?? throw new InvalidOperationException("missing Y");
        byte[] point = new byte[65];
        point[0] = 4;
        x.CopyTo(point.AsSpan(33 - x.Length));
        y.CopyTo(point.AsSpan(65 - y.Length));
        var client = new LinkClient("127.0.0.1", host.Port, host.Fingerprint);
        await client.PairAsync("phoneone", "测试手机", "123456", Convert.ToBase64String(point), TestContext.Current.CancellationToken);
        return client;
    }
}

public sealed class LivePhotoGrouperTests
{
    [Fact]
    public void Same_stem_heic_and_mov_become_one_pair()
    {
        var files = new[]
        {
            new GooglePhotosPreparedFile(@"D:\a\IMG_1.HEIC", 10, 0, null),
            new GooglePhotosPreparedFile(@"D:\a\IMG_1.MOV", 20, 0, null),
            new GooglePhotosPreparedFile(@"D:\a\IMG_2.JPG", 5, 0, null),
        };
        var (ordinary, pairs) = LivePhotoGrouper.Separate(files);
        pairs.Count.ShouldBe(1);
        pairs[0].Still.Path.ShouldEndWith("IMG_1.HEIC");
        ordinary.Count.ShouldBe(1);
    }

    [Fact]
    public void Motion_package_appends_video_without_reencoding_jpeg()
    {
        string directory = Path.Combine(Path.GetTempPath(), "picharbor-motion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAGcD//Z");
            string still = Path.Combine(directory, "still.jpg");
            string video = Path.Combine(directory, "clip.mov");
            File.WriteAllBytes(still, jpeg);
            File.WriteAllBytes(video, "videobytes"u8.ToArray());
            string packaged = MotionPhotoMux.Package(still, video, directory, 1500);
            byte[] output = File.ReadAllBytes(packaged);
            output.AsSpan(0, 2).ToArray().ShouldBe(new byte[] { 0xFF, 0xD8 });
            System.Text.Encoding.ASCII.GetString(output).ShouldContain("MotionPhoto=\"1\"");
            output[^10..].ShouldBe("videobytes"u8.ToArray());
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
