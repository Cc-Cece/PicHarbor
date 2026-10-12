using PicHarbor.Core.Android;
using PicHarbor.Core.Device;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Android;

/// <summary>内部自定义标识的生成与格式约束。</summary>
public sealed class DeviceSerialGeneratorTests
{
    [Fact]
    public void CreateInternalId_is_valid_and_prefixed()
    {
        string id = DeviceSerialGenerator.CreateInternalId();

        id.ShouldStartWith("PH-");
        id.Length.ShouldBe(DeviceSerialGenerator.TotalLength);
        DeviceSerialGenerator.IsInternalId(id).ShouldBeTrue();
        DeviceSerialValidator.IsUsable(id).ShouldBeTrue();
    }

    [Fact]
    public void CreateInternalId_uses_only_the_crockford_alphabet()
    {
        // Crockford Base32 剔除易混淆字符，便于用户手抄。
        foreach (char c in "ILOU")
        {
            DeviceSerialGenerator.Alphabet.ShouldNotContain(c);
        }

        for (int i = 0; i < 100; i++)
        {
            string body = DeviceSerialGenerator.CreateInternalId()[DeviceSerialGenerator.InternalPrefix.Length..];
            body.Length.ShouldBe(DeviceSerialGenerator.BodyLength);
            body.ShouldAllBe(c => DeviceSerialGenerator.Alphabet.Contains(c));
        }
    }

    [Fact]
    public void CreateInternalId_does_not_repeat()
    {
        // 32^6 ≈ 1.07e9；200 个样本的碰撞概率约 2e-5，可忽略。
        var ids = Enumerable.Range(0, 200).Select(_ => DeviceSerialGenerator.CreateInternalId()).ToHashSet();

        ids.Count.ShouldBe(200);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("R58M12345AB", false)]
    [InlineData("ph-abcdef0123", true)]
    public void IsInternalId_detects_prefix(string? value, bool expected)
    {
        DeviceSerialGenerator.IsInternalId(value).ShouldBe(expected);
    }
}

/// <summary>配对服务中的纯逻辑。</summary>
public sealed class AndroidPairingServiceTests
{
    [Theory]
    [InlineData("/DCIM/PicHarbor/", "/DCIM/PicHarbor/.picharbor-serial")]
    [InlineData("/DCIM/PicHarbor", "/DCIM/PicHarbor/.picharbor-serial")]
    [InlineData("\\DCIM\\PicHarbor\\", "/DCIM/PicHarbor/.picharbor-serial")]
    public void RemoteIdentityPath_is_normalized(string input, string expected)
    {
        AndroidPairingService.RemoteIdentityPath(input).ShouldBe(expected);
    }
}

/// <summary>唯一码的合理性校验（手动输入与生成码共用）。</summary>
public sealed class DeviceSerialValidatorTests
{
    [Theory]
    [InlineData("a1b2c3d4e5f6")]
    [InlineData("R58M12345AB")]
    [InlineData("emulator-5554")]
    [InlineData("00008110-001A2C3D4E5F601E")]
    [InlineData("PH-7K3M9Q2X")]
    public void Validate_accepts_plausible_serials(string serial)
    {
        DeviceSerialValidator.Validate(serial).ShouldBe(DeviceSerialRejection.None);
        DeviceSerialValidator.IsUsable(serial).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, DeviceSerialRejection.Empty)]
    [InlineData("", DeviceSerialRejection.Empty)]
    [InlineData("   ", DeviceSerialRejection.Empty)]
    [InlineData("5", DeviceSerialRejection.TooShort)]
    [InlineData("7", DeviceSerialRejection.TooShort)]
    [InlineData("0000000000", DeviceSerialRejection.UniformCharacters)]
    [InlineData("FFFFFFFF", DeviceSerialRejection.UniformCharacters)]
    [InlineData("0123456789ABCDEF", DeviceSerialRejection.Placeholder)]
    [InlineData("unknown", DeviceSerialRejection.Placeholder)]
    [InlineData("android:192.168.1.5", DeviceSerialRejection.Placeholder)]
    [InlineData("android:192.168.1.100", DeviceSerialRejection.Placeholder)]
    [InlineData("5&2b1c4d3&0&1", DeviceSerialRejection.ContainsSeparator)]
    public void Validate_rejects_unusable_values(string? serial, DeviceSerialRejection expected)
    {
        DeviceSerialValidator.Validate(serial).ShouldBe(expected);
        DeviceSerialValidator.IsUsable(serial).ShouldBeFalse();
    }

    [Fact]
    public void Validate_rejects_over_long_values()
    {
        string tooLong = new('A', DeviceSerialValidator.MaxLength + 1);

        DeviceSerialValidator.Validate(tooLong).ShouldBe(DeviceSerialRejection.TooLong);
    }
}

/// <summary>远端目录固定化：任何历史/用户输入都收敛到唯一常量。</summary>
public sealed class AndroidRemoteLayoutTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/DCIM/PicHarbor/iPhone/")]
    [InlineData("/sdcard/MyBackup")]
    [InlineData("\\DCIM\\PicHarbor\\")]
    public void Normalize_always_collapses_to_fixed_root(string? input)
    {
        AndroidRemoteLayout.Normalize(input).ShouldBe(AndroidRemoteLayout.RootDirectory);
    }
}

/// <summary>回传身份解析的决策矩阵。</summary>
public sealed class AndroidDeviceIdentityResolutionTests
{
    [Fact]
    public void No_baseline_uses_remote_value()
    {
        var result = AndroidDeviceDetector.ResolveFromValues(null, "R58M12345AB");

        result.DeviceId.ShouldBe("R58M12345AB");
        result.Match.ShouldBe(DeviceIdentityMatch.NoBaseline);
        result.Source.ShouldBe(DeviceIdentitySource.PhoneSerialFile);
        result.RequiresConfirmation.ShouldBeFalse();
    }

    [Fact]
    public void No_baseline_and_no_remote_yields_empty_id()
    {
        var result = AndroidDeviceDetector.ResolveFromValues(null, null);

        result.DeviceId.ShouldBeEmpty();
        result.Match.ShouldBe(DeviceIdentityMatch.NoBaseline);
        result.RequiresConfirmation.ShouldBeFalse();
    }

    [Fact]
    public void Matching_remote_is_accepted_without_confirmation()
    {
        var result = AndroidDeviceDetector.ResolveFromValues("R58M12345AB", "r58m12345ab");

        result.DeviceId.ShouldBe("R58M12345AB");
        result.Match.ShouldBe(DeviceIdentityMatch.Matched);
        result.Source.ShouldBe(DeviceIdentitySource.PcConfirmed);
        result.RequiresConfirmation.ShouldBeFalse();
    }

    [Fact]
    public void Mismatch_uses_remote_device_and_requires_confirmation()
    {
        var result = AndroidDeviceDetector.ResolveFromValues("PH-AAAAAAAAAA", "R58M12345AB");

        // 记录应落在「实际连到的设备」上，而不是本机以为的那台。
        result.DeviceId.ShouldBe("R58M12345AB");
        result.BaselineId.ShouldBe("PH-AAAAAAAAAA");
        result.RemoteId.ShouldBe("R58M12345AB");
        result.Match.ShouldBe(DeviceIdentityMatch.Mismatched);
        result.RequiresConfirmation.ShouldBeTrue();
    }

    [Fact]
    public void Missing_remote_file_keeps_baseline_and_requires_confirmation()
    {
        var result = AndroidDeviceDetector.ResolveFromValues("PH-AAAAAAAAAA", null);

        result.DeviceId.ShouldBe("PH-AAAAAAAAAA");
        result.RemoteId.ShouldBeNull();
        result.Match.ShouldBe(DeviceIdentityMatch.Unverifiable);
        result.RequiresConfirmation.ShouldBeTrue();
    }

    [Fact]
    public void Legacy_file_source_is_reported()
    {
        var result = AndroidDeviceDetector.ResolveFromValues(null, "OLD-ID", DeviceIdentitySource.PhoneLegacyFile);

        result.Source.ShouldBe(DeviceIdentitySource.PhoneLegacyFile);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    public void Whitespace_is_treated_as_missing(string blank)
    {
        AndroidDeviceDetector.ResolveFromValues(blank, blank).DeviceId.ShouldBeEmpty();
        AndroidDeviceDetector.ResolveFromValues("PH-AAAAAAAAAA", blank).Match.ShouldBe(DeviceIdentityMatch.Unverifiable);
    }
}
