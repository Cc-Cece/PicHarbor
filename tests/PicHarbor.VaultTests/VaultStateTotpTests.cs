// VaultState TOTP 流程集成测试
// 不涉及 ACL，只验证状态机层面的绑定/校验/重置/重绑与限流、防重放。
// 通过 VaultTestScope 重定向到独立临时目录。

using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

public class VaultStateTotpTests : IDisposable
{
    private readonly VaultTestScope _scope = new();
    private readonly VaultState _state = new();

    public VaultStateTotpTests() => TotpStore.Clear();

    public void Dispose() => _scope.Dispose();

    private static string CurrentCode(byte[] secret)
        => TotpEngine.ComputeCode(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    [Fact]
    public void BeginSetup_returns_uri_and_secret_but_not_yet_bound()
    {
        _state.TryBeginTotpSetup("tester", out string? uri, out string? secret, out string err)
            .ShouldBeTrue(err);

        uri.ShouldNotBeNullOrEmpty();
        secret.ShouldNotBeNullOrEmpty();
        uri!.ShouldStartWith("otpauth://totp/");
        _state.HasTotp.ShouldBeFalse("确认之前不应算绑定");
    }

    [Fact]
    public void ConfirmSetup_with_valid_code_binds_totp()
    {
        _state.TryBeginTotpSetup("tester", out _, out string? secretB32, out _);

        byte[] secret = FromBase32(secretB32!);
        string code = CurrentCode(secret);

        _state.TryConfirmTotpSetup(code, out string err).ShouldBeTrue(err);
        _state.HasTotp.ShouldBeTrue();
    }

    [Fact]
    public void ConfirmSetup_with_wrong_code_fails_and_does_not_bind()
    {
        _state.TryBeginTotpSetup("tester", out _, out _, out _);
        _state.TryConfirmTotpSetup("000000", out _).ShouldBeFalse();
        _state.HasTotp.ShouldBeFalse();
    }

    [Fact]
    public void ResetPasswordWithTotp_requires_bound_totp()
    {
        _state.TryResetPasswordWithTotp("123456", "newpass", out _).ShouldBeFalse();
    }

    [Fact]
    public void ResetPasswordWithTotp_updates_password_without_unlocking()
    {
        // 先设密码 + 绑定 TOTP
        _state.TrySetPassword("oldpass", out _).ShouldBeTrue();
        _state.TryBeginTotpSetup("tester", out _, out string? b32, out _);
        byte[] secret = FromBase32(b32!);
        _state.TryConfirmTotpSetup(CurrentCode(secret), out _).ShouldBeTrue();

        // 用 TOTP 重置密码
        // 注意：确认绑定已消费当前时间步，需等到下一步（或直接构造下一时间步的码）
        long nextStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TotpEngine.StepSeconds + 1;
        string code = TotpEngine.ComputeCodeForStep(secret, nextStep);

        _state.TryResetPasswordWithTotp(code, "brandnew", out string err).ShouldBeTrue(err);

        // 新密码可用，旧密码失效
        PasswordStore.Verify("brandnew").ShouldBeTrue();
        PasswordStore.Verify("oldpass").ShouldBeFalse();

        // 重置成功 ≠ 解锁
        _state.IsUnlocked.ShouldBeFalse("重置密码后目录仍应保持锁定");
    }

    [Fact]
    public void Rebind_requires_both_password_and_code()
    {
        _state.TrySetPassword("pw", out _);
        _state.TryBeginTotpSetup("tester", out _, out string? b32, out _);
        byte[] secret = FromBase32(b32!);
        _state.TryConfirmTotpSetup(CurrentCode(secret), out _);

        long nextStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TotpEngine.StepSeconds + 1;
        string goodCode = TotpEngine.ComputeCodeForStep(secret, nextStep);

        // 错误密码
        _state.TryRebindTotp("wrongpw", goodCode, "tester", out _, out _, out _).ShouldBeFalse();
        // 正确密码 + 正确码
        _state.TryRebindTotp("pw", goodCode, "tester", out string? uri, out string? newSecret, out string err)
            .ShouldBeTrue(err);
        uri.ShouldNotBeNullOrEmpty();
        newSecret.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Replay_is_rejected()
    {
        _state.TrySetPassword("pw", out _);
        _state.TryBeginTotpSetup("tester", out _, out string? b32, out _);
        byte[] secret = FromBase32(b32!);

        long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TotpEngine.StepSeconds;
        string code = TotpEngine.ComputeCodeForStep(secret, step);
        _state.TryConfirmTotpSetup(code, out _).ShouldBeTrue();

        // 同一个码再用一次应被拒
        _state.TryResetPasswordWithTotp(code, "x", out string err).ShouldBeFalse();
        err.ShouldContain("already been used");
    }

    // Base32 解码（用于从 URI 密钥还原字节，便于测试生成验证码）
    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        int bits = 0, value = 0;
        foreach (char c in input)
        {
            int idx = alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
