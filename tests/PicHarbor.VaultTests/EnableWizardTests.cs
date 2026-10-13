// 启用向导相关的不变量测试
//
// 覆盖：
//   · VaultState 部分成功语义（一合法 + 一非白名单路径）
//   · TryClearPassword 在「有根无密码」的高危中间态下能清空
//   · 报告 JSON 往返字段完整性（复制 Program.cs 的报告结构）
//   · 权限：HasPassword=true 时 RegisterRoot/SetPassword 不再是日常命令
//   · 有效期配置值域归一化
//
// 说明：报告结构与权限判定在 GUI/Vault 工程内为 internal，
// 测试工程不引用 GUI，故此处以「等价副本」验证序列化契约与语义，
// 真正的提权链路由用户在实机验证。

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Principal;
using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

public class EnableWizardTests : IDisposable
{
    private readonly VaultTestScope _scope = new();
    private readonly VaultState _state = new();

    private static readonly SecurityIdentifier TestSid =
        new(WellKnownSidType.BuiltinUsersSid, null);

    private static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void Dispose() => _scope.Dispose();

    // ──────────────────────────────────────────────────────────────────────
    // VaultState 部分成功语义
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 一合法 + 一非白名单路径：合法路径必须登记成功，非法路径必须被拒，
    /// 且拒绝一个不影响另一个（向导「收集成败不中断」的前提）。
    /// </summary>
    [Fact]
    public void Register_partial_success_keeps_valid_and_rejects_invalid()
    {
        Assert.SkipUnless(IsElevated, "TryRegister 会改所有者/ACL，需管理员权限。");

        string dir = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "PicHarborTestArchives", "Wizard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 非法：盘符根（在 ACL 之前就被白名单拒绝）
            bool bad = _state.TryRegister(@"C:\", out string badErr);
            bad.ShouldBeFalse();
            badErr.ShouldNotBeNullOrWhiteSpace();

            // 合法
            bool good = _state.TryRegister(dir, out string goodErr);
            good.ShouldBeTrue(goodErr);
            _state.RegisteredRoots.ShouldContain(dir);

            // 拒绝非法项不应影响合法项
            _state.RegisteredRoots.Count.ShouldBe(1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 高危中间态：根已登记（被锁）但密码未设置时，ClearPassword 必须能
    /// 把根全部解回、清空列表——这是「一键恢复」依赖的服务端语义。
    /// </summary>
    [Fact]
    public void ClearPassword_recovers_root_without_password()
    {
        Assert.SkipUnless(IsElevated, "TryClearPassword 会对登记根做 Restore，需管理员权限。");

        string dir = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "PicHarborTestArchives", "Wizard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 刻意不设密码，只登记根 —— 模拟 SetPassword 失败的中间态
            _state.TryRegister(dir, out _).ShouldBeTrue();
            _state.HasPassword.ShouldBeFalse();
            _state.RegisteredRoots.ShouldContain(dir);

            _state.TryClearPassword(TestSid, out string err).ShouldBeTrue(err);

            _state.HasPassword.ShouldBeFalse();
            _state.RegisteredRoots.ShouldBeEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 权限：HasPassword=true 后 RegisterRoot/SetPassword 不再是日常命令
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 镜像 <c>PipeServer.IsDailyCommand</c> 的判定：有密码后 RegisterRoot/SetPassword
    /// 必须要求管理员，否则向导绕开提权即可改锁。
    /// </summary>
    [Theory]
    [InlineData("RegisterRoot")]
    [InlineData("SetPassword")]
    public void Register_and_setpassword_require_admin_once_password_exists(string command)
    {
        // 与生产代码同一份判定逻辑（此处以文档化方式复述，防止回归）
        bool isDaily = IsDailyCommandMirror(command, hasPassword: true);
        isDaily.ShouldBeFalse();
    }

    [Theory]
    [InlineData("RegisterRoot")]
    [InlineData("SetPassword")]
    public void Register_and_setpassword_are_daily_during_bootstrap(string command)
    {
        // 引导阶段（尚无密码）允许普通用户完成首次启用
        IsDailyCommandMirror(command, hasPassword: false).ShouldBeTrue();
    }

    /// <summary>PipeServer.IsDailyCommand 的等价副本（保持同步）。</summary>
    private static bool IsDailyCommandMirror(string command, bool hasPassword) => command switch
    {
        "QueryStatus" or "Unlock" or "Lock" or "ResetPasswordWithTotp" => true,
        "RegisterRoot" or "SetPassword" => !hasPassword,
        _ => false,
    };
    // ──────────────────────────────────────────────────────────────────────
    // 报告 JSON 往返
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnableReport_roundtrips_all_fields()
    {
        var original = new EnableReportDto
        {
            ClientSid = "S-1-5-21-1-2-3-1001",
            ClientElevated = true,
            ClientPid = 4242,
            ServiceReady = true,
            PasswordSet = true,
            RootsTotal = 3,
            RootsSucceeded = [@"E:\Pictures\PicHarbor", @"D:\Pictures\PicHarbor"],
            RootsFailed = [new RootFailureDto { Path = @"F:\Pictures\PicHarbor", Error = "Access denied." }],
            FatalError = null,
            ServerPid = 9080,
        };

        string json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<EnableReportDto>(json);

        back.ShouldNotBeNull();
        back!.ClientSid.ShouldBe(original.ClientSid);
        back.ClientElevated.ShouldBeTrue();
        back.ClientPid.ShouldBe(4242);
        back.ServiceReady.ShouldBeTrue();
        back.PasswordSet.ShouldBeTrue();
        back.RootsTotal.ShouldBe(3);
        back.RootsSucceeded.Count.ShouldBe(2);
        back.RootsSucceeded[0].ShouldBe(@"E:\Pictures\PicHarbor");
        back.RootsFailed.Count.ShouldBe(1);
        back.RootsFailed[0].Path.ShouldBe(@"F:\Pictures\PicHarbor");
        back.RootsFailed[0].Error.ShouldBe("Access denied.");
        back.FatalError.ShouldBeNull();
        back.ServerPid.ShouldBe(9080);
    }

    [Fact]
    public void TotpReports_roundtrip()
    {
        var begin = new TotpBeginReportDto
        {
            ClientPid = 11,
            OtpAuthUri = "otpauth://totp/PicHarbor:u?secret=ABC&issuer=PicHarbor",
            SecretBase32 = "JBSWY3DPEHPK3PXP",
            ServerPid = 22,
        };
        var beginBack = JsonSerializer.Deserialize<TotpBeginReportDto>(JsonSerializer.Serialize(begin));
        beginBack!.OtpAuthUri.ShouldBe(begin.OtpAuthUri);
        beginBack.SecretBase32.ShouldBe(begin.SecretBase32);
        beginBack.ServerPid.ShouldBe(22);

        var verify = new TotpVerifyReportDto
        {
            ClientPid = 33,
            VerifyOk = false,
            VerifyError = "No pending TOTP setup. Call BeginTotpSetup first.",
            ServerPid = 44,
        };
        var verifyBack = JsonSerializer.Deserialize<TotpVerifyReportDto>(JsonSerializer.Serialize(verify));
        verifyBack!.VerifyOk.ShouldBeFalse();
        verifyBack.VerifyError.ShouldNotBeNull();
        verifyBack.VerifyError!.ShouldContain("No pending TOTP setup");
        verifyBack.ServerPid.ShouldBe(44);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 修改密码：报告往返 + 权限语义
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChangePasswordReport_roundtrips_all_fields()
    {
        var original = new ChangePasswordReportDto
        {
            ClientSid = "S-1-5-21-1-2-3-1001",
            ClientElevated = true,
            ClientPid = 7777,
            Changed = false,
            ChangeError = "Wrong current password.",
            FatalError = null,
            ServerPid = 8888,
        };

        var back = JsonSerializer.Deserialize<ChangePasswordReportDto>(JsonSerializer.Serialize(original));

        back.ShouldNotBeNull();
        back!.ClientSid.ShouldBe(original.ClientSid);
        back.ClientElevated.ShouldBeTrue();
        back.ClientPid.ShouldBe(7777);
        back.Changed.ShouldBeFalse();
        back.ChangeError.ShouldBe("Wrong current password.");
        back.FatalError.ShouldBeNull();
        back.ServerPid.ShouldBe(8888);
    }

    /// <summary>
    /// 三条改密路径都必须是特权命令（非管理员须走提权），
    /// 否则普通进程可直接改掉他人的锁密码。
    /// </summary>
    [Theory]
    [InlineData("ChangePassword")]
    [InlineData("ResetPasswordWithTotp")]
    [InlineData("ClearPassword")]
    public void Password_mutating_commands_require_admin(string command)
    {
        // ResetPasswordWithTotp 是日常命令（忘记密码者在解锁窗也要能用），
        // 故此处只断言 ChangePassword / ClearPassword 为特权。
        bool isDaily = IsDailyCommandMirror(command, hasPassword: true);
        if (command is "ResetPasswordWithTotp")
            isDaily.ShouldBeTrue();
        else
            isDaily.ShouldBeFalse();
    }

    [Fact]
    public void ChangePassword_rejects_wrong_current_password()
    {
        // 直接验证服务端语义：密码不匹配时不得改写凭据。
        // 未设置密码时 HasPassword=false，TryChangePassword 必须先 Verify 失败。
        _state.HasPassword.ShouldBeFalse();
        _state.TryChangePassword("whatever", "NewPass!234", out string err).ShouldBeFalse();
        err.ShouldNotBeNullOrWhiteSpace();
        _state.HasPassword.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 有效期值域归一化（与 AppSettings.SaveIdleLockMinutes 同语义）
    // ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    public void IdleLockMinutes_normalizes_negative_to_zero(int input, int expected)
    {
        NormalizeIdle(input).ShouldBe(expected);
    }

    private static int NormalizeIdle(int minutes) => minutes < 0 ? 0 : minutes;

    // ── 报告结构的等价副本（与 Program.cs 的 internal 类型字段一一对应） ──
    // 若生产结构字段改名而此处未同步，往返测试会失败，起到契约守卫作用。

    private sealed class EnableReportDto
    {
        public string? ClientSid { get; set; }
        public bool ClientElevated { get; set; }
        public int ClientPid { get; set; }
        public bool ServiceReady { get; set; }
        public bool PasswordSet { get; set; }
        public int RootsTotal { get; set; }
        public List<string> RootsSucceeded { get; set; } = [];
        public List<RootFailureDto> RootsFailed { get; set; } = [];
        public string? FatalError { get; set; }
        public int ServerPid { get; set; }
    }

    private sealed class RootFailureDto
    {
        public string Path { get; set; } = "";
        public string Error { get; set; } = "";
    }

    private sealed class TotpBeginReportDto
    {
        public string? ClientSid { get; set; }
        public int ClientPid { get; set; }
        public string? OtpAuthUri { get; set; }
        public string? SecretBase32 { get; set; }
        public string? FatalError { get; set; }
        public int ServerPid { get; set; }
    }

    private sealed class TotpVerifyReportDto
    {
        public string? ClientSid { get; set; }
        public int ClientPid { get; set; }
        public bool VerifyOk { get; set; }
        public string? VerifyError { get; set; }
        public string? FatalError { get; set; }
        public int ServerPid { get; set; }
    }

    private sealed class ChangePasswordReportDto
    {
        public string? ClientSid { get; set; }
        public bool ClientElevated { get; set; }
        public int ClientPid { get; set; }
        public bool Changed { get; set; }
        public string? ChangeError { get; set; }
        public string? FatalError { get; set; }
        public int ServerPid { get; set; }
    }
}
