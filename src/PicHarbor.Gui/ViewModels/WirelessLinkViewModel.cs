using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Link;
using PicHarbor.Core.Organize;
using PicHarbor.Gui.Config;
using QRCoder;

namespace PicHarbor.Gui.ViewModels;

/// <summary>One phone on the main-page list. One row per identity.</summary>
public sealed class WirelessPeerRow
{
    public required string PairingId { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public bool CanAsk { get; init; }
    public string? ArchiveId { get; init; }
    public bool ScopeV2 { get; init; }
}

/// <summary>
/// Keeps the computer listening while PicHarbor is open.
/// The phone confirms before any file is copied, and the other way around.
/// </summary>
public partial class WirelessLinkViewModel : ObservableObject
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer clock;
    private LinkHost? host;
    private bool suppressEnabled;
    private string archivePath = "";
    private OrganizeScheme scheme = OrganizeScheme.Month;
    private bool groupByDevice = true;
    private string? trackedId;
    private string? trackedPairing;
    private string trackedDirection = "";
    private bool waitingConfirm;
    private DateTimeOffset pairVisibleUntil;
    private string? shownQr;
    private string? autoNoted;
    private int writeTicket;

    public WirelessLinkViewModel()
    {
        suppressEnabled = true;
        Enabled = AppSettings.Load().WirelessReceiveEnabled;
        suppressEnabled = false;
        clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => Refresh();
        StatusText = App.GetString("WirelessStopped", "无线接收已关闭。");
    }

    public ObservableCollection<WirelessPeerRow> Peers { get; } = new();

    /// <summary>Called when the receive card picks an archive folder.</summary>
    public Action<string>? ChooseArchive { get; set; }

    /// <summary>UDID of the iPhone currently on the cable, when one is connected.</summary>
    public Func<string?>? CurrentIphoneUdid { get; set; }

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    private bool listening;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private string addressText = "";

    [ObservableProperty]
    private string certText = "";

    [ObservableProperty]
    private string archiveHint = "";

    [ObservableProperty]
    private string codeText = "";

    [ObservableProperty]
    private bool showPairing;

    [ObservableProperty]
    private BitmapSource? qrImage;

    [ObservableProperty]
    private bool hasArchive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIncomingConfirmationOpen))]
    private bool hasOffer;

    [ObservableProperty]
    private bool hasPeers;

    [ObservableProperty]
    private string offerTitle = "";

    [ObservableProperty]
    private string offerDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfferCancelLabel))]
    [NotifyPropertyChangedFor(nameof(IsIncomingConfirmationOpen))]
    private bool canConfirm;

    public bool IsIncomingConfirmationOpen => HasOffer && CanConfirm;
    public Action? TransferCompleted { get; set; }
    public string OfferCancelLabel => CanConfirm ? App.GetString("WirelessReject", "拒绝") : App.GetString("PairCancelBackup", "取消请求 / 备份");

    [ObservableProperty]
    private string iphoneWriteText = "";

    [ObservableProperty]
    private DateTime? scopeFrom;

    [ObservableProperty]
    private DateTime? scopeTo;

    public string ComputerName => Environment.MachineName;

    /// <summary>Android codes this computer already handed to a paired phone.</summary>
    public IReadOnlyList<string> AndroidCodes() => host?.BoundAndroidCodes() ?? [];

    /// <summary>Start listening when the switch is on. An empty archive is allowed.</summary>
    public void Start()
    {
        if (!Enabled || host is not null)
        {
            return;
        }

        try
        {
            host = LinkHost.Start(new LinkHostOptions { Beacon = true });
            host.Changed += OnChanged;
            host.OfferArrived += OnOffer;
            host.Status += OnStatus;
            host.IdentityConflict += OnConflict;
            host.UseArchive(archivePath, scheme, groupByDevice);
            Listening = true;
            StatusText = App.GetString("WirelessFirewallHint", "如果手机一直连不上，请在 Windows 防火墙里允许 PicHarbor.Gui.exe 接收入站连接。");
            clock.Start();
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            Listening = false;
            host = null;
        }
    }

    /// <summary>Stop listening. The process is not a Windows service, so exit does this too.</summary>
    public void Stop()
    {
        clock.Stop();
        LinkHost? dying = host;
        host = null;
        if (dying is not null)
        {
            dying.Changed -= OnChanged;
            dying.OfferArrived -= OnOffer;
            dying.Status -= OnStatus;
            dying.IdentityConflict -= OnConflict;
            _ = dying.DisposeAsync();
        }

        Listening = false;
        HasPeers = false;
        Peers.Clear();
        ClearOffer();
        ShowPairing = false;
        StatusText = App.GetString("WirelessStopped", "无线接收已关闭。");
    }

    /// <summary>Remember where accepted photos should land.</summary>
    public void UseArchive(string? path, OrganizeScheme nextScheme, bool nextGroup)
    {
        archivePath = path?.Trim() ?? "";
        NotifySelection();
        scheme = nextScheme;
        groupByDevice = nextGroup;
        host?.UseArchive(archivePath, scheme, groupByDevice);
        Refresh();
    }

    /// <summary>Remember a UDID read over the cable. The phone writes the file after its next check-in.</summary>
    public void NoteCableUdid(string? udid)
    {
        host?.NoteIphoneUdid(udid);
        if (string.IsNullOrWhiteSpace(udid))
        {
            return;
        }

        WirelessPeerRow? peer = IosPeer();
        if (peer is null)
        {
            return;
        }

        string trimmed = udid.Trim();
        if (autoNoted == trimmed)
        {
            return;
        }

        autoNoted = trimmed;
        _ = PublishWriteOutcomeAsync(peer.PairingId);
    }

    partial void OnEnabledChanged(bool value)
    {
        if (suppressEnabled)
        {
            return;
        }

        try
        {
            AppConfig config = AppSettings.Load();
            config.WirelessReceiveEnabled = value;
            AppSettings.Save(config);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }

        if (value)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    [RelayCommand]
    private void TogglePairing()
    {
        if (ShowPairing)
        {
            ShowPairing = false;
            return;
        }

        host?.RefreshPairingCode();
        ShowPairing = true;
        shownQr = null;
        pairVisibleUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        Refresh();
    }

    [RelayCommand]
    private void AcceptOffer()
    {
        if (host is null || trackedId is null || trackedPairing is null || !HasArchive)
        {
            return;
        }

        host.AcceptOffer(trackedId, trackedPairing);
        Refresh();
    }

    [RelayCommand]
    private void RejectOffer()
    {
        if (host is not null && trackedId is not null && trackedPairing is not null)
        {
            host.RejectOffer(trackedId, trackedPairing);
        }

        ClearOffer();
    }

    [RelayCommand]
    private void PickArchive()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = App.GetString("WirelessPickArchive", "选择归档目录"),
            InitialDirectory = Directory.Exists(archivePath) ? archivePath : string.Empty,
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ChooseArchive?.Invoke(dialog.FolderName);
        }
    }

    [RelayCommand]
    private void AskPeer(string? pairingId)
    {
        if (host is null || string.IsNullOrWhiteSpace(pairingId))
        {
            return;
        }

        if (!HasArchive)
        {
            ArchiveHint = App.GetString("WirelessNeedArchive", "请先选择归档目录，手机传来的照片会写入这个目录。");
            return;
        }

        var spec = BuildSelection();
        if (spec.Validate() is { } error) { StatusText = error; return; }
        LinkOffer? offer = host.AskPhone(pairingId, BuildScope(), 0);
        if (offer is null)
        {
            StatusText = App.GetString("WirelessBusy", "这台手机已有一条备份请求。");
            return;
        }

        ShowOffer(offer, notify: false);
    }

    [RelayCommand]
    private void WriteIphone()
    {
        string? udid = CurrentIphoneUdid?.Invoke();
        host?.NoteIphoneUdid(udid);
        WirelessPeerRow? peer = IosPeer();
        if (host is null || peer is null || string.IsNullOrWhiteSpace(udid))
        {
            IphoneWriteText = App.GetString("WirelessWriteFailed", "写入失败。请确认手机已解锁、已信任这台电脑，并且 PicHarbor 在前台。");
            return;
        }

        _ = PublishWriteOutcomeAsync(peer.PairingId);
    }

    private async Task PublishWriteOutcomeAsync(string pairingId)
    {
        int ticket = ++writeTicket;
        IphoneWriteText = App.GetString("WirelessWriteWorking", "正在通过已配对的连接写入…");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        LinkWriteOutcome outcome = LinkWriteOutcome.Failed;
        while (DateTimeOffset.UtcNow < deadline && ticket == writeTicket)
        {
            outcome = host?.IphoneWriteOutcome(pairingId) ?? LinkWriteOutcome.Failed;
            if (outcome is LinkWriteOutcome.Written or LinkWriteOutcome.DirectoryMissing)
            {
                break;
            }

            await Task.Delay(400);
        }

        if (ticket != writeTicket)
        {
            return;
        }

        IphoneWriteText = outcome switch
        {
            LinkWriteOutcome.Written => App.GetString("WirelessWriteWritten", "写成了。"),
            LinkWriteOutcome.DirectoryMissing => App.GetString("WirelessWriteMissing", "目录还不存在。请先在 iPhone 上打开一次 PicHarbor。"),
            _ => App.GetString("WirelessWriteFailed", "写入失败。请确认手机已解锁、已信任这台电脑，并且 PicHarbor 在前台。"),
        };
    }

    private void OnChanged() => dispatcher.BeginInvoke(Refresh);

    private void OnStatus(string message) => dispatcher.BeginInvoke(() =>
    {
        if (!string.IsNullOrWhiteSpace(message) && !message.StartsWith("已重新连接 ", StringComparison.Ordinal))
        {
            StatusText = message;
            if (message.StartsWith("备份完成", StringComparison.Ordinal) || message.StartsWith("备份未完成", StringComparison.Ordinal)) TransferCompleted?.Invoke();
            TransferLog.Add(DateTime.Now.ToString("HH:mm:ss") + " " + message);
            if (TransferLog.Count > 1000) TransferLog.RemoveAt(0);

        }

        Refresh();
    });

    public Action? ActivatePairingPage { get; set; }
    private void OnOffer(LinkOffer offer) => dispatcher.BeginInvoke(() => { SelectedPeer = Peers.FirstOrDefault(peer => peer.PairingId == offer.PairingId) ?? SelectedPeer; ShowOffer(offer, notify: true); ActivatePairingPage?.Invoke(); });

    private void OnConflict(string archiveId) => dispatcher.BeginInvoke(() =>
    {
        MessageBox.Show(
            string.Format(CultureInfo.CurrentCulture, App.GetString("WirelessIdentityConflict", "这台手机报上的身份和已配对记录不一致（记录为 {0}）。请确认是不是连错了设备。这次不会自动合并。"), archiveId),
            App.GetString("MsgBoxTitle", "提示"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    });

    private void Refresh()
    {
        if (host is null)
        {
            Listening = false;
            HasPeers = false;
            HasArchive = !string.IsNullOrWhiteSpace(archivePath) && Directory.Exists(archivePath);
            ArchiveHint = HasArchive ? "" : App.GetString("WirelessNeedArchive", "请先选择归档目录，手机传来的照片会写入这个目录。");
            return;
        }

        Listening = true;
        AddressText = string.Join("  ", host.Addresses.Select(address => address + ":" + host.Port));
        string prefix = host.Fingerprint.Length >= 16 ? host.Fingerprint[..16] : host.Fingerprint;
        CertText = App.GetString("WirelessCertPrefix", "证书 ") + host.Fingerprint;
        HasArchive = !string.IsNullOrWhiteSpace(archivePath) && Directory.Exists(archivePath);
        ArchiveHint = HasArchive ? "" : App.GetString("WirelessNeedArchive", "请先选择归档目录，手机传来的照片会写入这个目录。");
        UpdatePeers();
        UpdateOffer();
        UpdateQr();
        UpdateCatalog();
    }

    private void UpdatePeers()
    {
        if (host is null)
        {
            return;
        }

        var rows = host.KnownPeers.Select(peer => new WirelessPeerRow
        {
            PairingId = peer.PairingId,
            Title = (string.IsNullOrWhiteSpace(peer.Name) ? peer.Platform : peer.Name) + (peer.Online ? " · 在线" : " · 离线"),
            Detail = string.IsNullOrWhiteSpace(peer.ArchiveId)
                ? App.GetString("WirelessIdentityPending", "身份还没就绪")
                : peer.Platform + " · " + peer.ArchiveId,
            CanAsk = peer.Online && peer.Ready, ArchiveId = peer.ArchiveId, ScopeV2 = peer.ScopeV2,
        }).ToList();
        HasPeers = rows.Count > 0;
        if (rows.Count == Peers.Count && rows.Select(row => row.PairingId + row.Detail + row.Title + row.ScopeV2).SequenceEqual(Peers.Select(row => row.PairingId + row.Detail + row.Title + row.ScopeV2)))
        {
            return;
        }

        string? selected = SelectedPeer?.PairingId;
        var previous = SelectedPeer;
        loadingScope = true;
        Peers.Clear();
        foreach (WirelessPeerRow row in rows)
        {
            Peers.Add(row);
        }
        var next = Peers.FirstOrDefault(row => row.PairingId == selected) ?? Peers.FirstOrDefault();
        SelectedPeer = next;
        loadingScope = false;
        if (previous?.PairingId != next?.PairingId || previous?.ScopeV2 != next?.ScopeV2) OnSelectedPeerChanged(next);
        NotifySelection();
    }

    private void UpdateOffer()
    {
        if (host is null)
        {
            return;
        }

        if (trackedId is null)
        {
            LinkOffer? incoming = host.OpenOffers.FirstOrDefault(offer => offer.Direction == "phone-to-computer");
            if (incoming is not null)
            {
                ShowOffer(incoming, notify: false);
            }

            return;
        }

        LinkOffer? offer = host.FindOffer(trackedId);
        if (offer is null)
        {
            if (waitingConfirm)
            {
                StatusText = trackedDirection == "computer-to-phone"
                    ? App.GetString("WirelessPhoneSilent", "手机没有确认。请打开 PicHarbor 并保持在前台。")
                    : App.GetString("WirelessOfferExpired", "请求已超时，没有传输文件。");
            }

            if (!waitingConfirm) SelectedDeviceStatus = "备份请求已结束；文件结果请查看传输日志。";
            ClearOffer();
            return;
        }

        if (offer.Accepted)
        {
            waitingConfirm = false;
            CanConfirm = false;
            OfferDetail = App.GetString("WirelessReceiving", "已确认，正在接收。");
            return;
        }

        TimeSpan left = offer.Expires - DateTimeOffset.UtcNow;
        OfferDetail = string.Format(CultureInfo.CurrentCulture, App.GetString("WirelessSeconds", "还剩 {0} 秒"), Math.Max(0, (int)left.TotalSeconds));
        CanConfirm = offer.Direction == "phone-to-computer" && HasArchive;
    }

    private void UpdateQr()
    {
        if (!ShowPairing || host is null)
        {
            return;
        }

        if (DateTimeOffset.UtcNow >= pairVisibleUntil)
        {
            ShowPairing = false;
            return;
        }

        if (shownQr == host.QrText && QrImage is not null)
        {
            CodeText = host.Code;
            return;
        }

        shownQr = host.QrText;
        CodeText = host.Code;
        pairVisibleUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(host.QrText, QRCodeGenerator.ECCLevel.Q);
            byte[] bytes = new PngByteQRCode(data).GetGraphic(8);
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            QrImage = image;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void ShowOffer(LinkOffer offer, bool notify)
    {
        trackedId = offer.Id;
        trackedPairing = offer.PairingId;
        trackedDirection = offer.Direction;
        waitingConfirm = !offer.Accepted;
        HasOffer = true;
        TransferLog.Add(DateTime.Now.ToString("HH:mm:ss") + (offer.Direction == "phone-to-computer" ? " 手机主动发送，采用手机选择：" + offer.Scope : " 电脑发起，采用已保存范围。"));
        string? name = host?.OnlinePeers.FirstOrDefault(peer => peer.PairingId == offer.PairingId)?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "手机";
        }

        OfferTitle = offer.Direction == "computer-to-phone"
            ? string.Format(CultureInfo.CurrentCulture, App.GetString("WirelessOfferWait", "已请 {0} 确认。请让 PicHarbor 保持在前台。"), name)
            : offer.Count > 0
                ? string.Format(CultureInfo.CurrentCulture, App.GetString("WirelessOfferReceive", "接收 {0} 项，来自 {1}"), offer.Count, name)
                : string.Format(CultureInfo.CurrentCulture, App.GetString("WirelessOfferReceiveOpen", "接收来自 {0} 的照片。确认后才开始传输。"), name);
        CanConfirm = offer.Direction == "phone-to-computer" && HasArchive && !offer.Accepted;
        if (notify && offer.Direction == "phone-to-computer" && Application.Current?.MainWindow?.IsActive != true)
        {
            DesktopNotice.Show(App.GetString("WirelessCardTitle", "无线接收"), OfferTitle);
        }
    }

    private void ClearOffer()
    {
        trackedId = null;
        trackedPairing = null;
        trackedDirection = "";
        waitingConfirm = false;
        HasOffer = false;
        CanConfirm = false;
        OfferTitle = "";
        OfferDetail = "";
    }

    private string BuildScope()
    {
        if (CanUseScopeV2) return BuildSelection().ToWire();
        if (!DateEnabled || ScopeFrom is null && ScopeTo is null) return "all";
        // Older Android clients require both dates; encode open ends as inclusive calendar bounds.
        return "range:" + (ScopeFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "0001-01-01") + ":" + (ScopeTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "9999-12-30");
    }

    private WirelessPeerRow? IosPeer()
    {
        var peers = host?.OnlinePeers.Where(peer => string.Equals(peer.Platform, "ios", StringComparison.OrdinalIgnoreCase)).ToList();
        if (peers is null || peers.Count == 0)
        {
            return null;
        }

        LinkPeer? match = peers.FirstOrDefault(peer => string.Equals(peer.ArchiveId, autoNoted, StringComparison.Ordinal)) ?? peers[0];
        return new WirelessPeerRow
        {
            PairingId = match.PairingId,
            Title = match.Name,
            Detail = match.ArchiveId ?? "",
            CanAsk = match.ArchiveId is not null,
        };
    }
}

/// <summary>A balloon when the main window is not in front. The card on the page is the record that stays.</summary>
internal static class DesktopNotice
{
    private const uint Add = 0;
    private const uint Delete = 2;
    private const uint IconFlag = 0x10;

    public static void Show(string title, string text)
    {
        try
        {
            IntPtr window = Application.Current?.MainWindow is Window owner
                ? new WindowInteropHelper(owner).Handle
                : IntPtr.Zero;
            if (window == IntPtr.Zero)
            {
                return;
            }

            var data = new NoticeData
            {
                cbSize = Marshal.SizeOf<NoticeData>(),
                hWnd = window,
                uID = 0x5048,
                uFlags = IconFlag,
                szTip = "PicHarbor",
                szInfo = Trim(text, 250),
                szInfoTitle = Trim(title, 60),
                dwInfoFlags = 1,
            };
            _ = ShellNotify(Add, ref data);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _ = ShellNotify(Delete, ref data);
            };
            timer.Start();
        }
        catch (Exception)
        {
            // The page card is still visible when the user returns.
        }
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotify(uint message, ref NoticeData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NoticeData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}
