using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PicHarbor.Gui.ViewModels;

/// <summary>
/// One row in the backup-page task center. A finished card stays until dismissed.
/// </summary>
public partial class TransferTaskCardViewModel : ObservableObject
{
    public TransferTaskCardViewModel(
        string key,
        bool hasDetails,
        Action stop,
        Action? showCopied,
        Action? showSkipped,
        Action? showFailed,
        Action dismiss,
        Func<Task>? retryFailed = null)
    {
        Key = key;
        HasDetails = hasDetails;
        StopCommand = new RelayCommand(stop);
        ShowCopiedCommand = new RelayCommand(() => showCopied?.Invoke());
        ShowSkippedCommand = new RelayCommand(() => showSkipped?.Invoke());
        ShowFailedCommand = new RelayCommand(() => showFailed?.Invoke());
        DismissCommand = new RelayCommand(dismiss);
        RetryFailedCommand = new AsyncRelayCommand(retryFailed ?? (() => Task.CompletedTask), () => CanRetryFailed);
    }

    public string Key { get; }

    public bool HasDetails { get; }

    [ObservableProperty]
    private string title = "";

    [ObservableProperty]
    private string stateText = "";

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string progressText = "";

    [ObservableProperty]
    private string speedText = "--";

    [ObservableProperty]
    private string resultText = "";

    [ObservableProperty]
    private string copiedDetailsLabel = "";

    [ObservableProperty]
    private string retryFailedLabel = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    private bool canRetryFailed;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isShown;

    [ObservableProperty]
    private bool hasRun;

    [ObservableProperty]
    private bool dismissed;

    public IRelayCommand StopCommand { get; }

    public IRelayCommand DismissCommand { get; }

    public IRelayCommand ShowCopiedCommand { get; }

    public IRelayCommand ShowSkippedCommand { get; }

    public IRelayCommand ShowFailedCommand { get; }

    public IAsyncRelayCommand RetryFailedCommand { get; }
}
