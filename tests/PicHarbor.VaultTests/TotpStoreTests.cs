// TotpStore 防重放与限流状态测试
// 通过 VaultTestScope 重定向到独立临时目录，不触碰真实服务数据。

using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

public class TotpStoreTests : IDisposable
{
    private readonly VaultTestScope _scope = new();
    private static string FilePath => System.IO.Path.Combine(VaultStore.Directory, "vault_totp.json");

    public TotpStoreTests() => TotpStore.Clear();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void HasSecret_false_initially()
    {
        TotpStore.HasSecret.ShouldBeFalse();
        TotpStore.GetSecret().ShouldBeNull();
    }

    [Fact]
    public void SetSecret_then_GetSecret_roundtrips()
    {
        byte[] secret = TotpEngine.GenerateSecret();
        TotpStore.SetSecret(secret);

        TotpStore.HasSecret.ShouldBeTrue();
        TotpStore.GetSecret().ShouldBe(secret);
    }

    [Fact]
    public void Secret_is_not_stored_in_plaintext()
    {
        byte[] secret = TotpEngine.GenerateSecret();
        TotpStore.SetSecret(secret);

        string raw = File.ReadAllText(FilePath);
        // 明文 Base64 不应出现
        raw.ShouldNotContain(Convert.ToBase64String(secret));
    }

    [Fact]
    public void Replay_protection_marks_and_rejects_consumed_step()
    {
        const long step = 12345678L;

        TotpStore.IsStepConsumed(step).ShouldBeFalse();
        TotpStore.MarkStepConsumed(step);
        TotpStore.IsStepConsumed(step).ShouldBeTrue();

        // 更早的步也算已消费（只前进不后退）
        TotpStore.IsStepConsumed(step - 1).ShouldBeTrue();
        // 更晚的步尚未消费
        TotpStore.IsStepConsumed(step + 1).ShouldBeFalse();
    }

    [Fact]
    public void MarkStepConsumed_never_regresses()
    {
        TotpStore.MarkStepConsumed(100);
        TotpStore.MarkStepConsumed(50);   // 应被忽略
        TotpStore.IsStepConsumed(100).ShouldBeTrue();
    }

    [Fact]
    public void Rate_limit_locks_after_repeated_failures()
    {
        const long now = 1_700_000_000L;

        TotpStore.IsLockedOut(now, out _).ShouldBeFalse();

        // 前 4 次失败不锁
        for (int i = 0; i < 4; i++)
            TotpStore.RecordFailure(now, out _);

        TotpStore.IsLockedOut(now, out _).ShouldBeFalse();

        // 第 5 次进入锁定
        TotpStore.RecordFailure(now, out int lockout).ShouldBeTrue();
        lockout.ShouldBeGreaterThan(0);

        TotpStore.IsLockedOut(now, out int remaining).ShouldBeTrue();
        remaining.ShouldBeGreaterThan(0);

        // 锁定过期后解锁
        TotpStore.IsLockedOut(now + lockout + 1, out _).ShouldBeFalse();
    }

    [Fact]
    public void RecordSuccess_clears_failures_and_lockout()
    {
        const long now = 1_700_000_000L;
        for (int i = 0; i < 6; i++) TotpStore.RecordFailure(now, out _);
        TotpStore.IsLockedOut(now, out _).ShouldBeTrue();

        TotpStore.RecordSuccess();

        TotpStore.IsLockedOut(now, out _).ShouldBeFalse();
    }

    [Fact]
    public void State_survives_reload()
    {
        byte[] secret = TotpEngine.GenerateSecret();
        TotpStore.SetSecret(secret);
        TotpStore.MarkStepConsumed(999);

        // 重新从磁盘读取（无内存缓存）
        TotpStore.HasSecret.ShouldBeTrue();
        TotpStore.IsStepConsumed(999).ShouldBeTrue();
        TotpStore.GetSecret().ShouldBe(secret);
    }

    [Fact]
    public void Clear_removes_everything()
    {
        TotpStore.SetSecret(TotpEngine.GenerateSecret());
        TotpStore.MarkStepConsumed(5);

        TotpStore.Clear();

        TotpStore.HasSecret.ShouldBeFalse();
        TotpStore.IsStepConsumed(5).ShouldBeFalse();
    }
}
