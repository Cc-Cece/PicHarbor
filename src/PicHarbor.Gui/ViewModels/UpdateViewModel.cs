using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PicHarbor.Core.Updates;
using PicHarbor.Gui.Config;
using PicHarbor.Gui.Updates;
using PicHarbor.Gui.Util;
using PicHarbor.Vault;

namespace PicHarbor.Gui.ViewModels;

public partial class UpdateViewModel : ObservableObject
{
    private CancellationTokenSource? operation;
    private ReleaseUpdate? ready;
    private string? candidate;
    private Window? owner;
    private Func<bool> hasWork = () => false;
    private bool allowClose;
    private bool handlingClose;
    private bool installOnExit;
    private bool loading;
    private string statusKey = "UpdateIdle";
    private object[] statusArguments = [];

    [ObservableProperty] private int selectedMode;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckAndUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairVaultServiceCommand))]
    private bool isBusy;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string latestVersion = "—";
    [ObservableProperty] private string status = "";

    public string CurrentVersion => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    public string VersionText => string.Format(App.GetString("UpdateCurrentVersion"), CurrentVersion);
    public string ActionText => App.GetString(SelectedMode == (int)UpdateMode.Browser ? "UpdateBrowserButton"
        : SelectedMode == (int)UpdateMode.Manual ? "UpdateManualButton" : "UpdateCheckButton");

    public UpdateViewModel()
    {
        loading = true;
        var mode = AppSettings.Load().UpdateMode;
        SelectedMode = Enum.IsDefined(mode) ? (int)mode : 0;
        loading = false;
        SetStatus("UpdateIdle");
    }

    public void Attach(Window window, Func<bool> busy)
    {
        owner = window;
        hasWork = busy;
        window.Closing += OnClosing;
        window.ContentRendered += OnFirstRendered;
        window.Closed += (_, _) => { operation?.Cancel(); LocalizationService.LanguageChanged -= OnLanguageChanged; };
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public void SuppressExitInstallation()
    {
        installOnExit = false;
        operation?.Cancel();
    }

    private async void OnFirstRendered(object? sender, EventArgs e)
    {
        owner!.ContentRendered -= OnFirstRendered;
        UpdateInstaller.CompleteStartup();
        if (SelectedMode == (int)UpdateMode.Automatic) await CheckAsync(false);
    }

    internal void OnLanguageChanged(string language)
    {
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(ActionText));
        SetStatus(statusKey, statusArguments);
    }

    partial void OnSelectedModeChanged(int value)
    {
        OnPropertyChanged(nameof(ActionText));
        RepairVaultServiceCommand.NotifyCanExecuteChanged();
        if (loading) return;
        operation?.Cancel();
        installOnExit = false;
        var config = AppSettings.Load();
        config.UpdateMode = Enum.IsDefined((UpdateMode)value) ? (UpdateMode)value : UpdateMode.Automatic;
        AppSettings.Save(config);
        SetStatus(value == (int)UpdateMode.Browser ? "UpdateBrowserHint" : "UpdateIdle");
    }

    [RelayCommand]
    private void OpenReleases()
    {
        try { Process.Start(new ProcessStartInfo(ReleaseUpdate.ReleasesUrl) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("UpdateFailed", ex.Message); }
    }

    private bool CanCheck() => !IsBusy;
    private bool CanCancel() => IsBusy && operation is not null;
    private bool CanRepairVault() => !IsBusy && UpdateInstaller.IsPackaged && ServiceInstaller.IsInstalled();

    [RelayCommand(CanExecute = nameof(CanRepairVault))]
    private async Task RepairVaultService()
    {
        if (!CanRepairVault() || owner is null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        IsBusy = true;
        try
        {
            SetStatus("UpdateWaiting");
            while (hasWork()) await Task.Delay(300, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            UpdateWorkGate.IsInstalling = true;
            owner.IsEnabled = false;
            var result = await VaultServiceManager.EnsureServiceInstalledAsync();
            SetStatus(result.ok ? "UpdateServiceRepaired" : "UpdateFailed", result.message);
        }
        catch (OperationCanceledException) { SetStatus("UpdateCancelled"); }
        catch (Exception ex) { Log(ex); SetStatus("UpdateFailed", ex.Message); }
        finally { owner.IsEnabled = true; UpdateWorkGate.IsInstalling = false; IsBusy = false; operation = null; }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAndUpdate()
    {
        if (SelectedMode == (int)UpdateMode.Browser) { OpenReleases(); return; }
        await CheckAsync(true);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelUpdate() => operation?.Cancel();

    private async Task CheckAsync(bool explicitAction)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        IsBusy = true;
        var ct = cancellation.Token;
        try
        {
            SetStatus("UpdateChecking");
            // Read on every operation so edits to the network setting take effect without restarting.
            using var http = UpdateHttp.CreateClient(AppSettings.Load().GooglePhotosProxy, TimeSpan.FromMinutes(10));
            var client = new ReleaseClient(http);
            var published = await client.LatestReleaseAsync(ct);
            if (published is null) { SetStatus("UpdateNoStableRelease"); return; }
            LatestVersion = published.Tag;
            var current = UpdatePolicy.ParseVersion(CurrentVersion, false)
                ?? throw new InvalidDataException("Unable to determine current application version.");
            if (published.Version <= current)
            { ready = null; installOnExit = false; SetStatus("UpdateAlreadyLatest"); return; }
            // Checking is useful in every build. Only downloading/installing requires a packaged executable.
            if (!UpdateInstaller.IsPackaged)
            { ready = null; installOnExit = false; SetStatus("UpdateCheckOnly", published.Tag); return; }
            var release = published.GetUpdate();
            if (!explicitAction && AppSettings.Load().SkippedUpdateVersion == release.Tag)
            { SetStatus("UpdateSkipped", release.Tag); return; }
            string hash = await client.ChecksumAsync(release, ct);
            string directory = Path.Combine(UpdateInstaller.CacheRoot, release.Version.ToString());
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, ReleaseUpdate.AssetName);
            bool cached = false;
            if (File.Exists(destination))
            {
                try { await UpdatePolicy.VerifyAsync(destination, release, hash, ct); cached = true; }
                catch (InvalidDataException) { File.Delete(destination); }
            }
            if (!cached)
            {
                SetStatus("UpdateDownloading", release.Tag);
                Progress = 0;
                string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
                try
                {
                    await client.DownloadAsync(release, temporary, new Progress<double>(p => Progress = p), ct);
                    await UpdatePolicy.VerifyAsync(temporary, release, hash, ct);
                    File.Move(temporary, destination, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            ct.ThrowIfCancellationRequested();
            ready = release;
            candidate = destination;
            Progress = 100;
            installOnExit = SelectedMode == (int)UpdateMode.Automatic;
            SetStatus("UpdateReady", release.Tag);
            if (explicitAction && SelectedMode == (int)UpdateMode.Manual)
                await InstallAsync(restart: true, ct);
        }
        catch (OperationCanceledException) { SetStatus("UpdateCancelled"); }
        catch (Exception ex) { Log(ex); SetStatus("UpdateFailed", ex.Message); }
        finally { if (ReferenceEquals(operation, cancellation)) { IsBusy = false; operation = null; } }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        if (handlingClose) { e.Cancel = true; return; }
        if (ready is null || !installOnExit) { operation?.Cancel(); return; }
        e.Cancel = true;
        handlingClose = true;
        operation?.Cancel();
        using var closing = new CancellationTokenSource();
        operation = closing;
        IsBusy = true;
        try
        {
            bool launched = await InstallAsync(restart: false, closing.Token);
            // Cancelling permission/UAC still honours the user's request to exit.
            if (!launched && !closing.IsCancellationRequested) { allowClose = true; owner!.Close(); }
        }
        catch (OperationCanceledException) { SetStatus("UpdateCancelled"); }
        catch (Exception ex) { Log(ex); allowClose = true; owner!.Close(); }
        finally { handlingClose = false; IsBusy = false; operation = null; }
    }

    private async Task<bool> InstallAsync(bool restart, CancellationToken ct)
    {
        if (ready is null || candidate is null || owner is null) return false;
        SetStatus("UpdateWaiting");
        while (hasWork()) { await Task.Delay(300, ct); }
        ct.ThrowIfCancellationRequested();
        bool enabled = owner.IsEnabled;
        UpdateWorkGate.IsInstalling = true;
        owner.IsEnabled = false;
        try
        {
            bool elevated = UpdateInstaller.RequiresElevation(Environment.ProcessPath!);
            // Only a ready update being installed on exit AND requiring UAC can show the exit dialog.
            if ((restart && elevated) || UpdatePolicy.ShowExitPrompt(ready is not null, installOnExit, elevated))
            {
                owner.IsEnabled = enabled;
                var dialog = new UpdatePermissionDialog(ready!.Tag) { Owner = owner };
                bool accepted = dialog.ShowDialog() == true;
                var config = AppSettings.Load();
                var decision = UpdatePolicy.ApplyConsent(config.UpdateMode, config.SkippedUpdateVersion, ready.Tag,
                    accepted, dialog.SkipVersion, dialog.DisableAutomatic);
                config.SkippedUpdateVersion = decision.SkippedVersion;
                config.UpdateMode = decision.Mode;
                AppSettings.Save(config);
                if (dialog.DisableAutomatic)
                {
                    loading = true;
                    SelectedMode = (int)UpdateMode.Manual;
                    loading = false;
                    installOnExit = false;
                }
                if (!decision.Install) return false;
                owner.IsEnabled = false;
            }
            ct.ThrowIfCancellationRequested();
            // The modal can pump task completion/commands; verify idle again before committing.
            if (hasWork()) { SetStatus("UpdateWaiting"); return false; }
            SetStatus("UpdateInstalling");
            await UpdateInstaller.LaunchAsync(candidate, ready!.Tag, restart, elevated);
            allowClose = true;
            owner.Close();
            return true;
        }
        catch (Exception ex)
        {
            Log(ex);
            SetStatus(ex.Message == "UAC_CANCELLED" ? "UpdateCancelled" : "UpdateFailed", ex.Message);
            return false;
        }
        finally { owner.IsEnabled = enabled; UpdateWorkGate.IsInstalling = false; }
    }

    private void SetStatus(string key, params object[] arguments)
    {
        statusKey = key;
        statusArguments = arguments;
        Status = string.Format(App.GetString(key, key), arguments);
    }

    private static void Log(Exception ex)
    {
        try { Directory.CreateDirectory(UpdateInstaller.CacheRoot); File.AppendAllText(Path.Combine(UpdateInstaller.CacheRoot, "updates.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); }
        catch { Debug.WriteLine(ex); }
    }
}
