// TOTP 引擎与 RFC 6238 测试向量
// 使用 RFC 6238 附录 B 的标准测试向量（SHA1，8 位码的截断到 6 位验证）。

using System.Text;
using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

public class TotpEngineTests
{
    // RFC 6238 附录 B 使用的种子（ASCII "12345678901234567890"，20 字节）
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238 附录 B 的标准时间点与 8 位期望码
    [Theory]
    [InlineData(59L,          "94287082")]
    [InlineData(1111111109L,  "07081804")]
    [InlineData(1111111111L,  "14050471")]
    [InlineData(1234567890L,  "89005924")]
    [InlineData(2000000000L,  "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_rfc6238_test_vectors_sha1(long unixTime, string expected8)
    {
        // ComputeCodeForStep 产出 6 位；RFC 向量是 8 位，比较后 6 位
        long step = unixTime / TotpEngine.StepSeconds;
        string actual = TotpEngine.ComputeCodeForStep(RfcSecret, step);
        actual.ShouldBe(expected8[^6..]);
    }

    [Fact]
    public void ComputeCode_is_stable_within_same_step()
    {
        // 取一个步起点，加上 0..29 秒都应落回同一步
        long stepStart = 1_000_000_020L - (1_000_000_020L % TotpEngine.StepSeconds);
        string a = TotpEngine.ComputeCode(RfcSecret, stepStart);
        string b = TotpEngine.ComputeCode(RfcSecret, stepStart + TotpEngine.StepSeconds - 1);
        a.ShouldBe(b);
    }

    [Fact]
    public void ComputeCode_changes_across_steps()
    {
        string a = TotpEngine.ComputeCode(RfcSecret, 1_000_000_000L);
        string b = TotpEngine.ComputeCode(RfcSecret, 1_000_000_030L);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Verify_accepts_current_code()
    {
        const long now = 1_700_000_000L;
        string code = TotpEngine.ComputeCode(RfcSecret, now);
        TotpEngine.Verify(RfcSecret, code, now, out long step).ShouldBeTrue();
        step.ShouldBe(now / TotpEngine.StepSeconds);
    }

    [Fact]
    public void Verify_accepts_within_drift_window()
    {
        const long now = 1_700_000_000L;
        // 上一时间步的码（漂移 -1）
        string code = TotpEngine.ComputeCode(RfcSecret, now - TotpEngine.StepSeconds);
        TotpEngine.Verify(RfcSecret, code, now, out _).ShouldBeTrue();
    }

    [Fact]
    public void Verify_rejects_outside_drift_window()
    {
        const long now = 1_700_000_000L;
        long farSteps = (TotpEngine.AllowedDriftSteps + 2) * TotpEngine.StepSeconds;
        string code = TotpEngine.ComputeCode(RfcSecret, now - farSteps);
        TotpEngine.Verify(RfcSecret, code, now, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]      // 位数不足
    [InlineData("1234567")]    // 位数过多
    [InlineData("abcdef")]     // 非数字
    public void Verify_rejects_malformed_code(string code)
    {
        TotpEngine.Verify(RfcSecret, code, 1_700_000_000L, out _).ShouldBeFalse();
    }

    [Fact]
    public void Verify_tolerates_internal_spaces()
    {
        const long now = 1_700_000_000L;
        string code = TotpEngine.ComputeCode(RfcSecret, now);
        string spaced = code[..3] + " " + code[3..];
        TotpEngine.Verify(RfcSecret, spaced, now, out _).ShouldBeTrue();
    }

    [Fact]
    public void Base32_encoding_matches_known_value()
    {
        // "12345678901234567890" (ASCII) 的 Base32
        TotpEngine.ToBase32(RfcSecret).ShouldBe("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
    }

    [Fact]
    public void OtpAuthUri_contains_required_params()
    {
        string uri = TotpEngine.BuildOtpAuthUri("ABCDEFGH", "alice", "PicHarbor");
        uri.ShouldStartWith("otpauth://totp/");
        uri.ShouldContain("secret=ABCDEFGH");
        uri.ShouldContain("issuer=PicHarbor");
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");
        uri.ShouldContain("algorithm=SHA1");
    }

    [Fact]
    public void GenerateSecret_returns_20_bytes_and_is_random()
    {
        var a = TotpEngine.GenerateSecret();
        var b = TotpEngine.GenerateSecret();
        a.Length.ShouldBe(20);
        b.Length.ShouldBe(20);
        a.ShouldNotBe(b);
    }
}
