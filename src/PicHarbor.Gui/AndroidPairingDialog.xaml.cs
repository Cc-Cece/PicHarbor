using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PicHarbor.Core.Android;
using PicHarbor.Gui.Controls;

namespace PicHarbor.Gui;

/// <summary>配对步骤在界面上的呈现。</summary>
public sealed class PairStepView
{
    public string Icon { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public Brush LabelBrush { get; init; } = Brushes.Black;
    public Brush DetailBrush { get; init; } = Brushes.Gray;
}

/// <summary>
/// 设备配对对话框：分步实时反馈 + 回读验证 + 覆盖保护。
/// </summary>
/// <remarks>
/// 这是改变设备端身份的唯一入口。配对必须绑定到明确的设备记录，
/// 因此调用方需先确定 <c>targetDeviceId</c>（来自设备历史记录，或为新设备生成的新码）。
/// </remarks>
public partial class AndroidPairingDialog : Window
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly Brush FailBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x87, 0x80));

    private readonly string deviceName;
    private readonly string targetDeviceId;
    private readonly string ftpHost;
    private readonly int ftpPort;
    private readonly string ftpUser;
    private readonly string ftpPassword;

    private readonly ObservableCollection<PairStepView> steps = new();
    private readonly Dictionary<PairStepStage, int> stageIndex = new();
    private bool running;
    private bool isClosing;

    /// <summary>配对结果；调用方据此决定是否需要回写本地记录。</summary>
    public PairOutcome Outcome { get; private set; } = PairOutcome.Failed;

    /// <summary>是否已达成配对（新配对完成，或设备端本就一致）。</summary>
    public bool IsPaired => Outcome is PairOutcome.Paired or PairOutcome.AlreadyPaired;

    public AndroidPairingDialog(
        string deviceName,
        string targetDeviceId,
        string ftpHost,
        int ftpPort,
        string ftpUser,
        string ftpPassword)
    {
        InitializeComponent();

        this.deviceName = deviceName;
        this.targetDeviceId = targetDeviceId;
        this.ftpHost = ftpHost;
        this.ftpPort = ftpPort;
        this.ftpUser = ftpUser;
        this.ftpPassword = ftpPassword;

        StepsList.ItemsSource = steps;

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseWithAnimation();
                e.Handled = true;
            }
        };

        Loaded += OnDialogLoaded;
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        PclAnimation.AnimateDouble(RootBorder, OpacityProperty, 1.0, 180, PclAnimation.EaseOutFluentWeak);
        PclAnimation.AnimateScale(RootScaleTransform, 1.0, 240, PclAnimation.EaseOutBack);

        TargetDeviceText.Text = string.IsNullOrWhiteSpace(deviceName) ? "Android Device" : deviceName;
        TargetIdentityText.Text = targetDeviceId;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (running) return;

        running = true;
        StartButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        steps.Clear();
        stageIndex.Clear();
        ResultText.Visibility = Visibility.Collapsed;
        Outcome = PairOutcome.Failed;

        try
        {
            var progress = new Progress<PairStep>(OnStepReported);

            PairResult result = await AndroidPairingService.PairAsync(
                ftpHost,
                ftpPort,
                ftpUser,
                ftpPassword,
                AndroidRemoteLayout.RootDirectory,
                targetDeviceId,
                ConfirmOverwrite,
                progress).ConfigureAwait(true);

            Outcome = result.Outcome;
            ShowOutcome(result);
        }
        catch (Exception ex)
        {
            ShowResult(string.Format(App.GetString("FmtPairFailed", "配对失败：{0}"), ex.Message), isError: true);
        }
        finally
        {
            running = false;
            StartButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            StartButton.Content = App.GetString("PairRetry", "重新配对");
        }
    }

    private void OnStepReported(PairStep step)
    {
        PairStepView view = MakeView(step);

        if (stageIndex.TryGetValue(step.Stage, out int index) && index < steps.Count)
        {
            steps[index] = view;
        }
        else
        {
            stageIndex[step.Stage] = steps.Count;
            steps.Add(view);
        }
    }

    private static PairStepView MakeView(PairStep step)
    {
        string label = step.Stage switch
        {
            PairStepStage.Connecting => App.GetString("PairStageConnecting", "连接 FTP"),
            PairStepStage.ReadingRemote => App.GetString("PairStageReading", "读取手机端识别文件"),
            PairStepStage.WritingRemote => App.GetString("PairStageWriting", "写入识别文件"),
            _ => App.GetString("PairStageVerifying", "回读验证"),
        };

        (string icon, Brush brush) = step.Status switch
        {
            PairStepStatus.Running => ("⏳", MutedBrush),
            PairStepStatus.Succeeded => ("✅", OkBrush),
            PairStepStatus.Failed => ("❌", FailBrush),
            _ => ("⏭", MutedBrush),
        };

        return new PairStepView
        {
            Icon = icon,
            Label = label,
            Detail = step.Detail,
            LabelBrush = brush,
            DetailBrush = brush,
        };
    }

    /// <summary>
    /// 设备端已有不同标识时的覆盖确认（在后台线程被调用，需切回 UI 线程弹框）。
    /// </summary>
    private bool ConfirmOverwrite(string remoteId)
    {
        bool confirmed = false;

        Dispatcher.Invoke(() =>
        {
            confirmed = MessageBox.Show(
                this,
                string.Format(
                    App.GetString(
                        "FmtPairOverwriteConfirm",
                        "该手机已绑定到标识 {0}，与你选择的 {1} 不一致。可能是连错了手机，或这台手机已属于另一条设备记录。确定要用 {1} 覆盖吗？"),
                    remoteId,
                    targetDeviceId),
                App.GetString("MsgBoxTitle", "提示"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        });

        return confirmed;
    }

    private void ShowOutcome(PairResult result)
    {
        switch (result.Outcome)
        {
            case PairOutcome.Paired:
                ShowResult(string.Format(App.GetString("FmtPairOk", "配对完成：{0}"), result.DeviceId), isError: false);
                break;

            case PairOutcome.AlreadyPaired:
                ShowResult(string.Format(App.GetString("FmtPairAlready", "该手机已是 {0}，无需重新配对。"), result.DeviceId), isError: false);
                break;

            case PairOutcome.Declined:
                ShowResult(App.GetString("PairDeclined", "已取消覆盖，未做任何改动。"), isError: true);
                break;

            default:
                ShowResult(string.Format(App.GetString("FmtPairFailed", "配对失败：{0}"), result.Message), isError: true);
                break;
        }
    }

    private void ShowResult(string text, bool isError)
    {
        ResultText.Text = text;
        ResultText.Foreground = isError ? FailBrush : OkBrush;
        ResultText.Visibility = Visibility.Visible;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseWithAnimation();

    public void CloseWithAnimation()
    {
        if (isClosing) return;
        isClosing = true;

        PclAnimation.AnimateScale(RootScaleTransform, 0.94, 150, PclAnimation.EaseOutFluentMiddle);
        PclAnimation.AnimateDouble(RootBorder, OpacityProperty, 0.0, 140, PclAnimation.EaseOutFluentMiddle, onCompleted: Close);
    }
}
