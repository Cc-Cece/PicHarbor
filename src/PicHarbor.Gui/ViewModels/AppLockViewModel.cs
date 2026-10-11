// PicHarbor.Gui — 应用锁界面逻辑
// 与 PicHarbor.Vault 服务通过命名管道通信。
// 所有判定（密码正确性、TOTP 有效性）都在服务侧完成，本类只驱动界面状态。

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Storage;
using PicHarbor.Gui.Config;
using PicHarbor.Gui.Util;
using PicHarbor.Gui.Vault;

namespace PicHarbor.Gui.ViewModels;

/// <summary>
/// 应用锁的状态与命令。由 <c>App</c> 在启动时创建，主窗口与解锁窗口共用。
/// </summary>
public partial class AppLockViewModel : ObservableObject
{
    private readonly DispatcherTimer? _idleTimer;

    // ──────────────────────────────────────────────────────────────────────
    // 状态
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>服务是否可达（未安装 / 未启动时为 false）。</summary>
    [ObservableProperty] private bool isServiceAvailable;

    /// <summary>是否已设置应用密码（即「已启用应用锁」）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsTotpCardEnabled))]
    [NotifyPropertyChangedFor(nameof(AppLockSummaryText))]
    private bool hasPassword;

    /// <summary>当前是否处于已解锁状态。</summary>
    [ObservableProperty] private bool isUnlocked;

    /// <summary>是否已绑定 TOTP 验证器。</summary>
    [ObservableProperty]
    private bool hasTotp;

    /// <summary>是否正在执行异步操作（用于禁用按钮）。</summary>
    [ObservableProperty] private bool isBusy;

    /// <summary>界面提示消息（成功或失败）。</summary>
    [ObservableProperty] private string statusMessage = "";

    /// <summary>状态消息是否为错误（用于着色）。</summary>
    [ObservableProperty] private bool isError;

    /// <summary>已登记的归档根路径。</summary>
    public IReadOnlyList<string> RegisteredRoots { get; private set; } = [];

    /// <summary>设置页应用锁卡片上的一行摘要。</summary>
    public string AppLockSummaryText => HasPassword
        ? App.GetString("AppLockSummaryEnabled", "应用锁已启用，正在保护您的归档目录。")
        : App.GetString("AppLockSummaryDisabled", "应用锁未启用。启用后，锁定时归档目录将无法被访问。");

    /// <summary>
    /// 要保护的归档根目录（由主窗口注入，通常是当前图库根 / 备份目标）。
    /// 启用应用锁时会把它登记进服务并立即锁定。
    /// </summary>
    public string? ArchiveRoot { get; set; }

    /// <summary>是否已启用应用锁。</summary>
    public bool IsEnabled => HasPassword;

    // ── 解锁窗口输入 ──
    [ObservableProperty] private string passwordInput = "";
    [ObservableProperty] private string newPasswordInput = "";
    [ObservableProperty] private string confirmPasswordInput = "";

    // ── TOTP 绑定 ──
    [ObservableProperty] private string totpSetupUri = "";
    [ObservableProperty] private string totpSetupSecret = "";
    [ObservableProperty] private string totpCodeInput = "";

    // ── 闲置超时（分钟，0 = 关闭） ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomIdleLockActive))]
    private int idleTimeoutMinutes;

    // ──────────────────────────────────────────────────────────────────────
    // 启用向导（第 1~3 步只收集输入，第 4 步唯一执行）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>向导是否打开。</summary>
    [ObservableProperty] private bool isWizardOpen;

    /// <summary>当前步骤：1=密码 2=目录 3=确认 4=结果。（管理模式已移除，向导只做启用。）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWizardStep1))]
    [NotifyPropertyChangedFor(nameof(IsWizardStep2))]
    [NotifyPropertyChangedFor(nameof(IsWizardStep3))]
    [NotifyPropertyChangedFor(nameof(IsWizardStep4))]
    [NotifyPropertyChangedFor(nameof(ShowWizardBack))]
    [NotifyPropertyChangedFor(nameof(ShowWizardCancel))]
    [NotifyPropertyChangedFor(nameof(ShowWizardNext))]
    [NotifyPropertyChangedFor(nameof(ShowWizardEnable))]
    [NotifyPropertyChangedFor(nameof(ShowWizardFinish))]
    [NotifyPropertyChangedFor(nameof(WizardCanNext))]
    private int wizardStep = 1;

    public bool IsWizardStep1 => WizardStep == 1;
    public bool IsWizardStep2 => WizardStep == 2;
    public bool IsWizardStep3 => WizardStep == 3;
    public bool IsWizardStep4 => WizardStep == 4;

    // 底部按钮的可见性一律用「单步布尔」表达，避免 Inverse 转换器在多步之间串台
    // （曾出现管理页同时显示「取消/完成/上一步/取消/下一步」六个按钮）。

    /// <summary>步 1~3：显示 上一步/取消/下一步。</summary>
    public bool ShowWizardBack => IsWizardStep1 || IsWizardStep2 || IsWizardStep3;

    /// <summary>步 1~3：任意一步都可直接放弃并关闭向导。</summary>
    public bool ShowWizardCancel => IsWizardStep1 || IsWizardStep2 || IsWizardStep3;
    public bool ShowWizardNext => IsWizardStep1 || IsWizardStep2;

    /// <summary>步 3：显示 启用。</summary>
    public bool ShowWizardEnable => IsWizardStep3;

    /// <summary>步 4（结果）：只显示 完成。</summary>
    public bool ShowWizardFinish => IsWizardStep4;

    /// <summary>向导里的密码（草稿，不提交给服务）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WizardCanNext))]
    [NotifyPropertyChangedFor(nameof(WizardPasswordError))]
    [NotifyPropertyChangedFor(nameof(HasWizardPasswordError))]
    private string wizardPassword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WizardCanNext))]
    [NotifyPropertyChangedFor(nameof(WizardPasswordError))]
    [NotifyPropertyChangedFor(nameof(HasWizardPasswordError))]
    private string wizardConfirmPassword = "";

    /// <summary>步 1 的实时校验错误（空串 = 无错误）。</summary>
    public string WizardPasswordError
    {
        get
        {
            if (string.IsNullOrEmpty(WizardPassword)) return "";
            if (string.IsNullOrEmpty(WizardConfirmPassword)) return "";
            return WizardPassword == WizardConfirmPassword
                ? ""
                : App.GetString("AppWizardErrPasswordMismatch", "两次输入的密码不一致。");
        }
    }

    public bool HasWizardPasswordError => !string.IsNullOrEmpty(WizardPasswordError);

    /// <summary>候选目录（默认全选）。</summary>
    public ObservableCollection<RootChoice> ProtectableRoots { get; } = [];

    /// <summary>是否至少勾选了一个目录。</summary>
    public bool HasAnyRootSelected => ProtectableRoots.Any(r => r.IsSelected);

    /// <summary>当前步骤是否允许「下一步 / 启用」。</summary>
    public bool WizardCanNext => WizardStep switch
    {
        1 => !string.IsNullOrEmpty(WizardPassword) && WizardPassword == WizardConfirmPassword,
        2 => HasAnyRootSelected,
        _ => true,
    };

    /// <summary>已成功登记的根（步 4 展示）。</summary>
    public ObservableCollection<string> WizardSucceededRoots { get; } = [];

    /// <summary>登记失败的根（步 4 展示）。</summary>
    public ObservableCollection<string> WizardFailedRoots { get; } = [];

    /// <summary>步 4 的建议文案。</summary>
    [ObservableProperty] private string wizardAdviceText = "";

    /// <summary>出现了部分失败（有成功也有失败）。</summary>
    [ObservableProperty] private bool isPartialFailure;

    /// <summary>高危中间态：有根已锁但密码未设成。</summary>
    [ObservableProperty] private bool isEnableFatal;

    /// <summary>向导是否正在执行（禁用按钮）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WizardCanNext))]
    private bool isWizardRunning;

    /// <summary>步 3/4 展示的「即将锁定」路径摘要。</summary>
    public string WizardSelectedRootsText =>
        string.Join(Environment.NewLine, ProtectableRoots.Where(r => r.IsSelected).Select(r => "• " + r.Path));

    // ──────────────────────────────────────────────────────────────────────
    // 双因素卡片（独立流程）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>双因素卡片是否可用（需先启用应用锁）。</summary>
    public bool IsTotpCardEnabled => HasPassword;

    /// <summary>
    /// 安全页「彻底移除应用锁组件」按钮的可见性：仅在服务已安装时显示。
    /// 未安装时隐藏（避免点了得到「无需移除」的困惑结果）。
    /// </summary>
    [ObservableProperty] private bool isServiceInstalled;

    /// <summary>安全页双因素区块是否展开（默认为折叠，保持页面干净）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTotpSectionCollapsed))]
    private bool isTotpSectionExpanded;

    /// <summary>双因素区块折叠态（与展开互斥，供 XAML 单布尔绑定）。</summary>
    public bool IsTotpSectionCollapsed => !IsTotpSectionExpanded;

    /// <summary>展开/收起双因素区块。</summary>
    [RelayCommand]
    private void ToggleTotpSection() => IsTotpSectionExpanded = !IsTotpSectionExpanded;

    // ── 「修改密码」对话框 ──

    /// <summary>修改密码对话框是否打开。</summary>
    [ObservableProperty] private bool isChangePasswordOpen;

    /// <summary>身份确认方式：0=旧密码；1=TOTP 动态码。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangeModePassword))]
    [NotifyPropertyChangedFor(nameof(IsChangeModeTotp))]
    private int changePasswordMode;

    public bool IsChangeModePassword => ChangePasswordMode == 0;
    public bool IsChangeModeTotp => ChangePasswordMode == 1;

    /// <summary>TOTP 未绑定时，「用动态码」选项不可用。</summary>
    public bool CanUseTotpForChange => HasTotp;

    [ObservableProperty] private string changeOldPassword = "";
    [ObservableProperty] private string changeTotpCode = "";
    [ObservableProperty] private string changeNewPassword = "";
    [ObservableProperty] private string changeConfirmPassword = "";
    [ObservableProperty] private string changePasswordError = "";

    public bool HasChangePasswordError => !string.IsNullOrEmpty(ChangePasswordError);

    partial void OnChangePasswordErrorChanged(string value)
        => OnPropertyChanged(nameof(HasChangePasswordError));

    partial void OnHasTotpChanged(bool value)
        => OnPropertyChanged(nameof(CanUseTotpForChange));

    // ── 危险操作二次确认 ──

    /// <summary>「关闭应用锁」二次确认弹窗是否打开。</summary>
    [ObservableProperty] private bool isDisableConfirmOpen;

    /// <summary>「彻底移除组件」二次确认弹窗是否打开。</summary>
    [ObservableProperty] private bool isRemoveConfirmOpen;

    /// <summary>确认弹窗要输入的密码（关闭应用锁用）。</summary>
    [ObservableProperty] private string dangerConfirmPassword = "";

    /// <summary>确认弹窗要输入的密码（彻底移除组件用，与关闭确认分开存以免串值）。</summary>
    [ObservableProperty] private string removeConfirmPassword = "";

    /// <summary>确认弹窗错误提示。</summary>
    [ObservableProperty] private string confirmError = "";

    public bool HasConfirmError => !string.IsNullOrEmpty(ConfirmError);

    partial void OnConfirmErrorChanged(string value)
        => OnPropertyChanged(nameof(HasConfirmError));

    /// <summary>关闭确认弹窗的说明文字（列出后果，含登记根数量）。</summary>
    public string DisableConfirmDetail => App.GetString("SecurityDisableConfirmDetail",
        "此操作将解除全部已保护目录的锁定，并清除应用密码" +
        (HasTotp ? "与双因素绑定" : "") + "。之后任何人均可访问这些目录。");

    /// <summary>彻底移除组件的确认说明。</summary>
    public string RemoveConfirmDetail => App.GetString("SecurityRemoveConfirmDetail",
        "此操作将卸载后台服务并删除密码、验证器与目录权限记录，不可撤销。" +
        "已锁定的目录会先被解除锁定。");

    /// <summary>
    /// 安全页「自动锁定」下拉的候选项。
    /// 与向导下拉共用同一档位集合（见 <see cref="WizardIdleLockOptions"/>），
    /// 避免「向导选了 15 分钟、安全页无此档位→回落成关闭」的不一致。
    /// </summary>
    public IReadOnlyList<IdleLockOption> IdleLockOptions { get; } =
    [
        new(0,  "SecurityIdleLockOff", "关闭"),
        new(5,  null, "5 分钟"),
        new(10, null, "10 分钟"),
        new(15, null, "15 分钟"),
        new(30, null, "30 分钟"),
        new(60, null, "60 分钟"),
        IdleLockOption.Custom,
    ];

    /// <summary>选择「自定义」后手填分钟的绑定值（0 表示未启用自定义）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomIdleLockActive))]
    private int customIdleLockMinutes;

    /// <summary>当前是否用了自定义值（不在预设档位里，且已启用）。</summary>
    public bool IsCustomIdleLockActive =>
        IdleTimeoutMinutes > 0 &&
        !IdleLockOptions.Any(o => !o.IsCustom && o.Minutes == IdleTimeoutMinutes);

    /// <summary>出码时服务端 PID，用于检测「确认前服务重启」。</summary>
    [ObservableProperty] private int totpPendingServerPid;

    /// <summary>闲置计时器触发时通知外部锁定。</summary>
    public event Action? IdleTimeoutReached;

    /// <summary>锁定状态变化时通知（供主窗口显示/隐藏遮罩）。</summary>
    public event Action? LockStateChanged;

    public AppLockViewModel()
    {
        // 从配置恢复「系统空闲 N 分钟后自动锁定」
        IdleTimeoutMinutes = AppSettings.LoadIdleLockMinutes();

        // 闲置检查：每分钟看一眼是否超时
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 服务交互
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>查询服务状态并刷新本地状态字段。</summary>
    public async Task<bool> RefreshStatusAsync()
    {
        // 服务是否已安装独立探测（装了但没启动时 QueryStatus 会失败，但「移除组件」仍应可见）
        IsServiceInstalled = VaultServiceManager.IsServiceInstalled();

        var status = await VaultClient.QueryStatusAsync();
        if (status is null)
        {
            IsServiceAvailable = false;
            HasPassword = false;
            IsUnlocked = false;
            HasTotp = false;
            RegisteredRoots = [];
            LockStateChanged?.Invoke();
            return false;
        }

        IsServiceAvailable = true;
        HasPassword = status.HasPassword;
        IsUnlocked = status.IsUnlocked;
        HasTotp = status.HasTotp;
        RegisteredRoots = status.RegisteredRoots;
        LockStateChanged?.Invoke();
        return true;
    }

    /// <summary>是否需要在启动时显示解锁窗口。</summary>
    public bool RequiresUnlockOnStartup => IsServiceAvailable && HasPassword && !IsUnlocked;

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (string.IsNullOrEmpty(PasswordInput))
        {
            SetError(App.GetString("AppLockErrPasswordRequired", "请输入应用密码。"));
            return;
        }

        IsBusy = true;
        try
        {
            var resp = await VaultClient.UnlockAsync(PasswordInput);
            if (resp is null)
            {
                SetError(App.GetString("AppLockErrServiceUnavailable", "无法连接到 PicHarbor Vault 服务。"));
                return;
            }
            if (!resp.Ok)
            {
                SetError(App.GetString("AppLockErrWrongPassword", "密码错误。"));
                return;
            }

            PasswordInput = "";
            SetSuccess(App.GetString("AppLockMsgUnlocked", "已解锁。"));
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LockAsync()
    {
        await VaultClient.LockAsync();
        await RefreshStatusAsync();
        SetSuccess(App.GetString("AppLockMsgLocked", "已锁定。"));
    }

    // ── 启用应用锁 ──

    [RelayCommand]
    private async Task EnableAsync()
    {
        if (string.IsNullOrEmpty(NewPasswordInput))
        {
            SetError(App.GetString("AppLockErrNewPasswordRequired", "请设置应用密码。"));
            return;
        }
        if (NewPasswordInput != ConfirmPasswordInput)
        {
            SetError(App.GetString("AppLockErrPasswordMismatch", "两次输入的密码不一致。"));
            return;
        }
        if (string.IsNullOrWhiteSpace(ArchiveRoot))
        {
            SetError(App.GetString("AppLockErrNoArchiveRoot", "尚未确定要保护的归档目录。"));
            return;
        }

        // 关闭按钮点击时可能残留的懒刷新
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // ① 确保 Vault 服务在线（不可达则弹一次 UAC 自行安装并启动）
            await RefreshStatusAsync();
            if (!IsServiceAvailable)
            {
                StatusMessage = App.GetString("AppLockMsgInstallingService", "正在安装并启动 PicHarbor Vault 服务…");
                IsError = false;

                var (installed, installMsg) = await VaultServiceManager.EnsureServiceInstalledAsync();
                if (!installed)
                {
                    SetError(installMsg);
                    return;
                }
                await RefreshStatusAsync();
                if (!IsServiceAvailable)
                {
                    SetError(App.GetString("AppLockErrServiceUnavailable", "无法连接到 PicHarbor Vault 服务。"));
                    return;
                }
            }

            // ② 把归档根登记进服务（登记即锁定，立刻收回访问权限）
            var regResp = await VaultClient.RegisterRootAsync(ArchiveRoot!);
            if (regResp is null || !regResp.Ok)
            {
                SetError(regResp?.Error ?? App.GetString("AppLockErrRegisterFailed", "登记归档目录失败。"));
                return;
            }

            // ③ 设置应用密码（此后即有锁）
            var resp = await VaultClient.SetPasswordAsync(NewPasswordInput);
            if (resp is null || !resp.Ok)
            {
                SetError(resp?.Error ?? App.GetString("AppLockErrServiceUnavailable", "无法连接到 PicHarbor Vault 服务。"));
                return;
            }

            NewPasswordInput = "";
            ConfirmPasswordInput = "";
            SetSuccess(App.GetString("AppLockMsgPasswordSet", "应用密码已设置，归档目录已被锁定。请继续绑定验证器。"));
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 启用向导
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>打开向导（同时载入候选目录与当前有效期）。</summary>
    [RelayCommand]
    private void OpenWizard()
    {
        // 重置执行结果
        WizardSucceededRoots.Clear();
        WizardFailedRoots.Clear();
        WizardAdviceText = "";
        IsPartialFailure = false;
        IsEnableFatal = false;

        LoadProtectableRoots();
        WizardPassword = "";
        WizardConfirmPassword = "";

        // 向导只做「启用」：从步 1（设密码）开始
        WizardStep = 1;

        StatusMessage = "";
        IsError = false;
        IsWizardOpen = true;
    }

    /// <summary>关闭向导：仅清草稿，零成本（不碰服务/目录/UAC）。</summary>
    [RelayCommand]
    private void CloseWizard()
    {
        ForceCloseWizard();
    }

    /// <summary>供代码后置调用的强制关闭（命令方法生成的是私有成员）。</summary>
    public void ForceCloseWizard()
    {
        IsWizardOpen = false;
        WizardPassword = "";
        WizardConfirmPassword = "";
        WizardStep = 1;
        IsWizardRunning = false;
    }

    /// <summary>下一步（步 1→2→3）。</summary>
    [RelayCommand]
    private void WizardNext()
    {
        if (WizardStep == 1)
        {
            if (string.IsNullOrEmpty(WizardPassword))
            {
                SetError(App.GetString("AppLockErrNewPasswordRequired", "请设置应用密码。"));
                return;
            }
            if (WizardPassword != WizardConfirmPassword)
            {
                SetError(App.GetString("AppLockErrPasswordMismatch", "两次输入的密码不一致。"));
                return;
            }
        }
        else if (WizardStep == 2 && !HasAnyRootSelected)
        {
            SetError(App.GetString("AppWizardHintSelectRoot", "请至少勾选一个要保护的目录。"));
            return;
        }

        if (WizardStep < 4) WizardStep++;
    }

    /// <summary>上一步（步 2→1）。</summary>
    [RelayCommand]
    private void WizardBack()
    {
        if (WizardStep > 1) WizardStep--;
    }

    /// <summary>
    /// 第 4 步唯一执行：一次 UAC 完成「装服务 → 登记目录 → 设密码」。
    /// </summary>
    [RelayCommand]
    private async Task ExecuteWizardAsync()
    {
        if (IsWizardRunning) return;

        var selected = ProtectableRoots.Where(r => r.IsSelected).Select(r => r.Path).ToList();
        if (selected.Count == 0)
        {
            SetError(App.GetString("AppWizardHintSelectRoot", "请至少勾选一个要保护的目录。"));
            return;
        }

        IsWizardRunning = true;
        IsBusy = true;
        try
        {
            SetSuccess(App.GetString("AppWizardMsgEnabling", "正在启用应用锁（会请求一次管理员授权）…"));

            var (report, uacCancelled) = await VaultServiceManager
                .EnableAppLockElevatedAsync(selected, WizardPassword)
                .ConfigureAwait(true);

            WizardSucceededRoots.Clear();
            WizardFailedRoots.Clear();

            if (uacCancelled)
            {
                WizardAdviceText = App.GetString("AppWizardAdviceUacCancelled",
                    "您取消了管理员授权，未做任何更改。可稍后重试。");
                SetError(App.GetString("AppWizardErrUacCancelled", "已取消管理员授权。"));
                WizardStep = 4;
                return;
            }

            if (report is null)
            {
                WizardAdviceText = App.GetString("AppWizardAdviceNoReport",
                    "未能取得执行结果。请检查后台服务状态后重试。");
                SetError(App.GetString("AppWizardErrNoReport", "未能取得执行结果。"));
                WizardStep = 4;
                return;
            }

            foreach (string p in report.RootsSucceeded) WizardSucceededRoots.Add(p);
            foreach (var f in report.RootsFailed) WizardFailedRoots.Add($"{f.Path}（{f.Error}）");

            IsEnableFatal = !report.PasswordSet && report.RootsSucceeded.Count > 0;
            IsPartialFailure = !IsEnableFatal &&
                               (report.RootsFailed.Count > 0 || !report.PasswordSet);

            if (IsEnableFatal)
            {
                WizardAdviceText = App.GetString("AppWizardAdviceFatal",
                    "部分目录已被锁定，但密码未设置成功。您可能暂时无法访问这些目录。" +
                    "请立即点击下方「一键恢复」解除锁定。");
                SetError(App.GetString("AppWizardResultFatal", "密码设置失败，目录可能已被锁定。"));
            }
            else if (report.RootsFailed.Count > 0)
            {
                WizardAdviceText = App.GetString("AppWizardAdvicePartial",
                    "应用锁已启用，但部分目录登记失败。可稍后重试或忽略这些目录。");
                SetSuccess(App.GetString("AppWizardResultPartial", "应用锁已启用，部分目录失败。"));
            }
            else if (report.PasswordSet)
            {
                WizardAdviceText = App.GetString("AppWizardAdviceSuccess",
                    "应用锁已启用，选中的目录已锁定。后续可在本页设置自动锁定或绑定双因素验证。");
                SetSuccess(App.GetString("AppWizardResultSuccess", "应用锁已启用。"));
                WizardPassword = "";
                WizardConfirmPassword = "";
            }
            else
            {
                WizardAdviceText = report.FatalError ?? App.GetString("AppWizardErrFatal", "启用失败。");
                SetError(WizardAdviceText);
            }

            WizardStep = 4;
            await RefreshStatusAsync();
        }
        finally
        {
            IsWizardRunning = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 载入候选目录：所有「已识别为 PicHarbor 库」的盘 + 当前活动归档根（去重），默认全选。
    /// 一台机器没有任何库时退化为「当前推导出的默认归档根」单项。
    /// </summary>
    public void LoadProtectableRoots()
    {
        ProtectableRoots.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        try
        {
            foreach (var vol in LibraryStorageService.DiscoverVolumes())
            {
                if (vol.HasExistingLibrary && !string.IsNullOrWhiteSpace(vol.RootPath))
                    candidates.Add(vol.RootPath);
            }
        }
        catch { /* 盘枚举失败时退化为 ArchiveRoot */ }

        if (!string.IsNullOrWhiteSpace(ArchiveRoot)) candidates.Add(ArchiveRoot!);

        // 一台库都没有时，给出默认推导根，保证向导可用
        if (candidates.Count == 0)
        {
            try
            {
                string drive = LibraryStorageService.GetDefaultDriveLetter();
                string root = LibraryStorageService.GetLibraryRootForDrive(drive);
                if (!string.IsNullOrWhiteSpace(root)) candidates.Add(root);
            }
            catch { /* ignore */ }
        }

        foreach (string path in candidates)
        {
            if (!seen.Add(path)) continue;
            ProtectableRoots.Add(new RootChoice(path, path, isSelected: true));
        }

        OnPropertyChanged(nameof(HasAnyRootSelected));
        OnPropertyChanged(nameof(WizardSelectedRootsText));
    }

    /// <summary>勾选状态变化后调用，刷新「下一步」可用性与确认页摘要。</summary>
    public void NotifyRootSelectionChanged()
    {
        OnPropertyChanged(nameof(HasAnyRootSelected));
        OnPropertyChanged(nameof(WizardCanNext));
        OnPropertyChanged(nameof(WizardSelectedRootsText));
    }

    /// <summary>中间态恢复：复用「彻底关闭应用锁」，解除全部目录锁定。</summary>
    [RelayCommand]
    private async Task RecoverFromPartialFailureAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, msg) = await VaultServiceManager.DisableAppLockAsync();
            if (!ok) { SetError(msg); return; }

            SetSuccess(App.GetString("AppWizardMsgRecovered", "已解除全部目录锁定，应用锁处于关闭状态。"));
            WizardAdviceText = App.GetString("AppWizardRecoverHint",
                "已恢复目录访问权限。注意：目录所有者可能变为管理员组，" +
                "若您无法读写，请在「属性 → 安全 → 高级 → 所有者」中把所有者改回自己。");
            IsEnableFatal = false;
            IsPartialFailure = false;
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 双因素卡片（独立流程，提权 + 报告回传）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>卡片「绑定/更换验证器」：提权出码。</summary>
    [RelayCommand]
    private async Task BeginTotpFromCardAsync()
    {
        IsBusy = true;
        try
        {
            var (report, uacCancelled) = await VaultServiceManager
                .BeginTotpElevatedAsync(Environment.UserName)
                .ConfigureAwait(true);

            if (uacCancelled)
            {
                SetError(App.GetString("AppWizardErrUacCancelled", "已取消管理员授权。"));
                return;
            }
            if (report is null || !string.IsNullOrEmpty(report.FatalError))
            {
                SetError(report?.FatalError ?? App.GetString("AppLockErrServiceUnavailable", "无法连接到 PicHarbor Vault 服务。"));
                return;
            }

            TotpSetupUri = report.OtpAuthUri ?? "";
            TotpSetupSecret = report.SecretBase32 ?? "";
            TotpPendingServerPid = report.ServerPid;
            // 出码成功必须展开，否则二维码被折叠区挡住（用户看到「没有相关内容出现」）
            IsTotpSectionExpanded = true;
            SetSuccess(App.GetString("AppLockMsgScanQr", "请用 Google Authenticator 扫描二维码。"));
        }
        finally { IsBusy = false; }
    }

    /// <summary>卡片「确认绑定」：提权提交动态码。先比对服务 PID，变了则提示重新出码。</summary>
    [RelayCommand]
    private async Task ConfirmTotpFromCardAsync()
    {
        if (string.IsNullOrEmpty(TotpCodeInput))
        {
            SetError(App.GetString("AppLockErrTotpRequired", "请输入验证器上的 6 位动态码。"));
            return;
        }

        IsBusy = true;
        try
        {
            // 服务重启会丢掉内存里的 pending 密钥，提前拦截给出清晰提示
            int currentPid = await QueryServerPidAsync().ConfigureAwait(true);
            if (TotpPendingServerPid != 0 && currentPid != 0 && currentPid != TotpPendingServerPid)
            {
                SetError(App.GetString("TotpErrServerRestarted", "后台服务已重启，请重新生成二维码后再确认。"));
                TotpSetupUri = "";
                TotpSetupSecret = "";
                return;
            }

            var (report, uacCancelled) = await VaultServiceManager
                .VerifyTotpElevatedAsync(TotpCodeInput)
                .ConfigureAwait(true);

            if (uacCancelled)
            {
                SetError(App.GetString("AppWizardErrUacCancelled", "已取消管理员授权。"));
                return;
            }
            if (report is null)
            {
                SetError(App.GetString("AppLockErrTotpInvalid", "验证码无效。"));
                return;
            }
            if (!report.VerifyOk)
            {
                string err = report.VerifyError ?? "";
                if (err.Contains("No pending TOTP setup", StringComparison.OrdinalIgnoreCase))
                    SetError(App.GetString("TotpErrServerRestarted", "后台服务已重启，请重新生成二维码后再确认。"));
                else
                    SetError(string.IsNullOrEmpty(err) ? App.GetString("AppLockErrTotpInvalid", "验证码无效。") : err);
                return;
            }

            TotpCodeInput = "";
            TotpSetupUri = "";
            TotpSetupSecret = "";
            TotpPendingServerPid = 0;
            SetSuccess(App.GetString("AppLockMsgTotpBound", "验证器已绑定。"));
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    private static async Task<int> QueryServerPidAsync()
    {
        var status = await VaultClient.QueryStatusAsync().ConfigureAwait(false);
        return status?.ServerPid ?? 0;
    }

    // ── 关闭应用锁 ──

    [RelayCommand]
    private async Task DisableAsync()
    {
        if (string.IsNullOrEmpty(PasswordInput))
        {
            SetError(App.GetString("AppLockErrPasswordRequired", "请输入应用密码。"));
            return;
        }

        IsBusy = true;
        try
        {
            // 先验证密码（未提权也能调 Unlock/Lock 之类的日常命令，
            // 但这里用 Unlock 的成功与否探测密码正确性，成功后再立刻锁回去）。
            var probe = await VaultClient.UnlockAsync(PasswordInput);
            if (probe is null || !probe.Ok)
            {
                SetError(App.GetString("AppLockErrWrongPassword", "密码错误。"));
                return;
            }
            // 探测完立刻收回，避免留下解锁态
            await VaultClient.LockAsync();

            // 彻底关闭（清密码 + 解除全部登记根）——服务端要求管理员，走提权路径
            var (ok, msg) = await VaultServiceManager.DisableAppLockAsync();
            if (!ok) { SetError(msg); return; }

            PasswordInput = "";
            SetSuccess(App.GetString("AppLockMsgDisabled", "应用锁已关闭。"));
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    // ── 彻底移除应用锁组件（注销服务 + 删除服务数据） ──

    [RelayCommand]
    private async Task RemoveComponentsAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = App.GetString("AppLockMsgRemovingComponents", "正在移除应用锁组件…");
            IsError = false;

            // 先卸载服务（停 + 删），再删数据目录。两者都需要管理员。
            var (uninstalled, uninstallMsg) = await VaultServiceManager.UninstallServiceAsync();
            if (!uninstalled) { SetError(uninstallMsg); return; }

            var (purged, purgeMsg) = await VaultServiceManager.PurgeVaultDataAsync();
            if (!purged) { SetError(purgeMsg); return; }

            SetSuccess(App.GetString("AppLockMsgComponentsRemoved", "应用锁组件已彻底移除。如需再次使用，重新启用即可自动重装。"));
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    // ── TOTP ──

    [RelayCommand]
    private async Task BeginTotpSetupAsync()
    {
        IsBusy = true;
        try
        {
            string account = Environment.UserName;
            var resp = await VaultClient.BeginTotpSetupAsync(account);
            if (resp is null || !resp.Ok)
            {
                SetError(resp?.Error ?? App.GetString("AppLockErrServiceUnavailable", "无法连接到 PicHarbor Vault 服务。"));
                return;
            }

            TotpSetupUri = resp.OtpAuthUri ?? "";
            TotpSetupSecret = resp.SecretBase32 ?? "";
            SetSuccess(App.GetString("AppLockMsgScanQr", "请用 Google Authenticator 扫描二维码。"));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ConfirmTotpSetupAsync()
    {
        if (string.IsNullOrEmpty(TotpCodeInput))
        {
            SetError(App.GetString("AppLockErrTotpRequired", "请输入验证器上的 6 位动态码。"));
            return;
        }

        IsBusy = true;
        try
        {
            var resp = await VaultClient.VerifyTotpSetupAsync(TotpCodeInput);
            if (resp is null || !resp.Ok)
            {
                SetError(resp?.Error ?? App.GetString("AppLockErrTotpInvalid", "验证码无效。"));
                return;
            }

            TotpCodeInput = "";
            TotpSetupUri = "";
            TotpSetupSecret = "";
            SetSuccess(App.GetString("AppLockMsgTotpBound", "验证器已绑定。"));
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>忘记密码：用 TOTP 重置。成功后不自动解锁。</summary>
    [RelayCommand]
    private async Task ResetPasswordWithTotpAsync()
    {
        if (string.IsNullOrEmpty(TotpCodeInput))
        {
            SetError(App.GetString("AppLockErrTotpRequired", "请输入验证器上的 6 位动态码。"));
            return;
        }
        if (string.IsNullOrEmpty(NewPasswordInput))
        {
            SetError(App.GetString("AppLockErrNewPasswordRequired", "请设置新密码。"));
            return;
        }

        IsBusy = true;
        try
        {
            var resp = await VaultClient.ResetPasswordWithTotpAsync(TotpCodeInput, NewPasswordInput);
            if (resp is null || !resp.Ok)
            {
                SetError(resp?.Error ?? App.GetString("AppLockErrResetFailed", "重置失败。"));
                return;
            }

            TotpCodeInput = "";
            NewPasswordInput = "";
            SetSuccess(App.GetString("AppLockMsgResetOk", "密码已重置。请用新密码解锁。"));
            // 重置成功 ≠ 解锁，目录仍锁着
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>重绑验证器：应用密码 + 当前 TOTP 码双验证。</summary>
    [RelayCommand]
    private async Task RebindTotpAsync()
    {
        if (string.IsNullOrEmpty(PasswordInput) || string.IsNullOrEmpty(TotpCodeInput))
        {
            SetError(App.GetString("AppLockErrRebindInputs", "需要同时提供应用密码和当前动态码。"));
            return;
        }

        IsBusy = true;
        try
        {
            var resp = await VaultClient.RebindTotpAsync(PasswordInput, TotpCodeInput, Environment.UserName);
            if (resp is null || !resp.Ok)
            {
                SetError(resp?.Error ?? App.GetString("AppLockErrRebindFailed", "重绑失败。"));
                return;
            }

            PasswordInput = "";
            TotpCodeInput = "";
            TotpSetupUri = resp.OtpAuthUri ?? "";
            TotpSetupSecret = resp.SecretBase32 ?? "";
            SetSuccess(App.GetString("AppLockMsgRebindScan", "请用验证器扫描新二维码完成重绑。"));
        }
        finally { IsBusy = false; }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 闲置超时
    // ──────────────────────────────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private static TimeSpan GetSystemIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    private void CheckIdle()
    {
        if (!IsUnlocked) return;
        if (IdleTimeoutMinutes <= 0) return;

        if (GetSystemIdleTime() >= TimeSpan.FromMinutes(IdleTimeoutMinutes))
        {
            IdleTimeoutReached?.Invoke();
            _ = LockAsync();
        }
    }

    /// <summary>
    /// 设置自动锁定分钟数（0 = 关闭）并持久化。向导/管理入口调用。
    /// 走 <see cref="AppSettings.SaveIdleLockMinutes"/> 的读-改-写，避免抹掉其它设置。
    /// </summary>
    public void ApplyIdleTimeout(int minutes)
    {
        int normalized = minutes < 0 ? 0 : minutes;
        IdleTimeoutMinutes = normalized;
        AppSettings.SaveIdleLockMinutes(normalized);
    }

    /// <summary>属性变更后把新值落盘（读取阶段会写入相同值，由 Save 内部幂等跳过）。</summary>
    partial void OnIdleTimeoutMinutesChanged(int value)
    {
        // 构造期间由 AppSettings 载入，此时无需回写；仅当与磁盘不一致才真正写。
        AppSettings.SaveIdleLockMinutes(value);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 修改密码（旧密码 或 TOTP 二选一）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>打开「修改密码」对话框。</summary>
    [RelayCommand]
    private void OpenChangePassword()
    {
        ChangePasswordMode = 0;
        ChangeOldPassword = "";
        ChangeTotpCode = "";
        ChangeNewPassword = "";
        ChangeConfirmPassword = "";
        ChangePasswordError = "";
        IsChangePasswordOpen = true;
    }

    /// <summary>关闭「修改密码」对话框。</summary>
    [RelayCommand]
    private void CloseChangePassword()
    {
        IsChangePasswordOpen = false;
        ChangeOldPassword = "";
        ChangeTotpCode = "";
        ChangeNewPassword = "";
        ChangeConfirmPassword = "";
        ChangePasswordError = "";
    }

    /// <summary>切换身份确认方式（旧密码 ⇄ TOTP）；TOTP 未绑定时不允许切换。</summary>
    [RelayCommand]
    private void SetChangeMode(string mode)
    {
        if (mode == "totp" && !CanUseTotpForChange) return;
        ChangePasswordMode = mode == "totp" ? 1 : 0;
        ChangePasswordError = "";
    }

    /// <summary>提交修改密码：提权执行，身份确认二选一。</summary>
    [RelayCommand]
    private async Task SubmitChangePasswordAsync()
    {
        ChangePasswordError = "";

        if (IsChangeModePassword && string.IsNullOrEmpty(ChangeOldPassword))
        {
            ChangePasswordError = App.GetString("AppLockErrPasswordRequired", "请输入应用密码。");
            return;
        }
        if (IsChangeModeTotp && string.IsNullOrEmpty(ChangeTotpCode))
        {
            ChangePasswordError = App.GetString("AppLockErrTotpRequired", "请输入验证器上的 6 位动态码。");
            return;
        }
        if (string.IsNullOrEmpty(ChangeNewPassword))
        {
            ChangePasswordError = App.GetString("AppLockErrNewPasswordRequired", "请设置新密码。");
            return;
        }
        if (ChangeNewPassword != ChangeConfirmPassword)
        {
            ChangePasswordError = App.GetString("AppLockErrPasswordMismatch", "两次输入的密码不一致。");
            return;
        }

        IsBusy = true;
        try
        {
            var (report, uacCancelled) = await VaultServiceManager
                .ChangePasswordElevatedAsync(
                    IsChangeModePassword ? ChangeOldPassword : null,
                    IsChangeModeTotp ? ChangeTotpCode : null,
                    ChangeNewPassword)
                .ConfigureAwait(true);

            if (uacCancelled)
            {
                ChangePasswordError = App.GetString("AppWizardErrUacCancelled", "已取消管理员授权。");
                return;
            }
            if (report is null)
            {
                ChangePasswordError = App.GetString("AppLockErrServiceUnavailable", "无法连接到 PicHarbor Vault 服务。");
                return;
            }
            if (!string.IsNullOrEmpty(report.FatalError))
            {
                ChangePasswordError = report.FatalError;
                return;
            }
            if (!report.Changed)
            {
                ChangePasswordError = report.ChangeError ?? App.GetString("SecurityErrChangeFailed", "修改密码失败。");
                return;
            }

            CloseChangePassword();
            SetSuccess(App.GetString("SecurityMsgPasswordChanged", "密码已修改。"));
        }
        finally { IsBusy = false; }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 危险操作二次确认（关闭应用锁 / 彻底移除组件）
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>打开「关闭应用锁」二次确认。</summary>
    [RelayCommand]
    private void OpenDisableConfirm()
    {
        DangerConfirmPassword = "";
        ConfirmError = "";
        IsDisableConfirmOpen = true;
    }

    /// <summary>取消关闭确认。</summary>
    [RelayCommand]
    private void CancelDisableConfirm()
    {
        IsDisableConfirmOpen = false;
        DangerConfirmPassword = "";
        ConfirmError = "";
    }

    /// <summary>确认关闭应用锁：先验密码，再提权清密码与登记根。</summary>
    [RelayCommand]
    private async Task ConfirmDisableAsync()
    {
        ConfirmError = "";

        if (string.IsNullOrEmpty(DangerConfirmPassword))
        {
            ConfirmError = App.GetString("AppLockErrPasswordRequired", "请输入应用密码。");
            return;
        }

        IsBusy = true;
        try
        {
            // 验密码（日常命令，未提权即可）：Unlock 成功即密码正确，随即锁回去
            var probe = await VaultClient.UnlockAsync(DangerConfirmPassword);
            if (probe is null || !probe.Ok)
            {
                ConfirmError = App.GetString("AppLockErrWrongPassword", "密码错误。");
                return;
            }
            await VaultClient.LockAsync();

            var (ok, msg) = await VaultServiceManager.DisableAppLockAsync();
            if (!ok) { ConfirmError = msg; return; }

            IsDisableConfirmOpen = false;
            DangerConfirmPassword = "";
            SetSuccess(App.GetString("AppLockMsgDisabled", "应用锁已关闭。"));
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>打开「彻底移除组件」二次确认。</summary>
    [RelayCommand]
    private void OpenRemoveConfirm()
    {
        RemoveConfirmPassword = "";
        ConfirmError = "";
        IsRemoveConfirmOpen = true;
    }

    /// <summary>取消移除确认。</summary>
    [RelayCommand]
    private void CancelRemoveConfirm()
    {
        IsRemoveConfirmOpen = false;
        RemoveConfirmPassword = "";
        ConfirmError = "";
    }

    /// <summary>
    /// 确认彻底移除组件：先验密码，再卸载服务 + 删除数据。
    /// 注意：关闭应用锁的验证密码走同一套 Unlock 探针，
    /// 因此忘密码者若已配置 TOTP，仍可先走「修改密码」重置后再移除。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        ConfirmError = "";

        if (string.IsNullOrEmpty(RemoveConfirmPassword))
        {
            ConfirmError = App.GetString("AppLockErrPasswordRequired", "请输入应用密码。");
            return;
        }

        IsBusy = true;
        try
        {
            // 验密码（日常命令，未提权即可）：Unlock 成功即密码正确，随即锁回去
            var probe = await VaultClient.UnlockAsync(RemoveConfirmPassword);
            if (probe is null || !probe.Ok)
            {
                ConfirmError = App.GetString("AppLockErrWrongPassword", "密码错误。");
                return;
            }
            await VaultClient.LockAsync();
            RemoveConfirmPassword = "";

            StatusMessage = App.GetString("AppLockMsgRemovingComponents", "正在移除应用锁组件…");
            IsError = false;

            var (uninstalled, uninstallMsg) = await VaultServiceManager.UninstallServiceAsync();
            if (!uninstalled) { ConfirmError = uninstallMsg; return; }

            var (purged, purgeMsg) = await VaultServiceManager.PurgeVaultDataAsync();
            if (!purged) { ConfirmError = purgeMsg; return; }

            IsRemoveConfirmOpen = false;
            SetSuccess(App.GetString("AppLockMsgComponentsRemoved", "应用锁组件已彻底移除。如需再次使用，重新启用即可自动重装。"));
            await RefreshStatusAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>把「自动锁定」下拉的选中项应用到 VM 并落盘。选中「自定义」时仅露出输入框，不改超时。</summary>
    [RelayCommand]
    private void ApplyIdleLockOption(IdleLockOption? option)
    {
        if (option is null) return;
        if (option.IsCustom)
        {
            // 只把输入框预填为当前值（或默认 15），等用户确认再落盘
            if (CustomIdleLockMinutes <= 0) CustomIdleLockMinutes = IdleTimeoutMinutes > 0 ? IdleTimeoutMinutes : 15;
            return;
        }
        ApplyIdleTimeout(option.Minutes);
    }

    /// <summary>确认「自定义」输入：把分钟数落盘（限制在 1..1440）。</summary>
    [RelayCommand]
    private void ApplyCustomIdleLock()
    {
        int minutes = CustomIdleLockMinutes;
        if (minutes < 1) minutes = 1;
        if (minutes > 1440) minutes = 1440;
        CustomIdleLockMinutes = minutes;
        ApplyIdleTimeout(minutes);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 工具
    // ──────────────────────────────────────────────────────────────────────

    private void SetSuccess(string msg) { IsError = false; StatusMessage = msg; }
    private void SetError(string msg)   { IsError = true;  StatusMessage = msg; }

    /// <summary>清空输入与消息（打开解锁窗口时调用）。</summary>
    public void ResetInputs()
    {
        PasswordInput = "";
        TotpCodeInput = "";
        NewPasswordInput = "";
        ConfirmPasswordInput = "";
        StatusMessage = "";
        IsError = false;
    }
}

/// <summary>
/// 安全页「自动锁定」下拉的一个候选项。
/// <paramref name="IsCustom"/> 为 true 表示「自定义」项——选中它不直接改超时，
/// 而是露出分钟输入框，由用户填任意数值（<paramref name="Minutes"/> 此时为 -1 仅作占位）。
/// <paramref name="ResourceKey"/> 为 null 时用字面文案。
/// </summary>
public sealed record IdleLockOption(int Minutes, string? ResourceKey, string FallbackText, bool IsCustom = false)
{
    /// <summary>显示文案：优先取本地化资源，缺失则回退字面文本。</summary>
    public string DisplayText =>
        ResourceKey is null ? FallbackText : App.GetString(ResourceKey, FallbackText);

    /// <summary>「自定义」哨兵项。</summary>
    public static IdleLockOption Custom { get; } =
        new(-1, "SecurityIdleLockCustom", "自定义…", IsCustom: true);
}
