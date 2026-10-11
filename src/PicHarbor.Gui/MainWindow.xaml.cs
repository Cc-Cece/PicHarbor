using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PicHarbor.Gui.Util;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _hookedMainVM;

    /// <summary>
    /// 应用锁视图模型（由 <see cref="App"/> 在启动时注入）。
    /// 声明为依赖属性，便于 XAML 用 RelativeSource 绑定。
    /// </summary>
    public static readonly DependencyProperty AppLockProperty =
        DependencyProperty.Register(
            nameof(AppLock), typeof(AppLockViewModel), typeof(MainWindow),
            new PropertyMetadata(null, OnAppLockChanged));

    public AppLockViewModel? AppLock
    {
        get => (AppLockViewModel?)GetValue(AppLockProperty);
        set => SetValue(AppLockProperty, value);
    }

    private static void OnAppLockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MainWindow win) return;

        if (e.OldValue is AppLockViewModel oldVm)
        {
            oldVm.PropertyChanged -= win.AppLockVm_PropertyChanged;
            oldVm.IdleTimeoutReached -= win.AppLock_IdleTimeoutReached;
        }

        if (e.NewValue is AppLockViewModel vm)
        {
            vm.PropertyChanged += win.AppLockVm_PropertyChanged;
            vm.IdleTimeoutReached += win.AppLock_IdleTimeoutReached;
        }
    }

    private void AppLockVm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppLockViewModel.TotpSetupUri))
        {
            Dispatcher.Invoke(() =>
            {
                string uri = AppLock?.TotpSetupUri ?? "";
                var source = string.IsNullOrEmpty(uri) ? null : QrCodeRenderer.Render(uri, 160);
                if (SecurityTotpQrImage is not null) SecurityTotpQrImage.Source = source;
            });
        }
        else if (e.PropertyName == nameof(AppLockViewModel.WizardStep))
        {
            Dispatcher.Invoke(() =>
            {
                if (AppLock?.IsWizardStep1 == true)
                {
                    WizardPasswordBox?.Focus();
                }
                else if (AppLock?.IsWizardStep3 == true)
                {
                    SyncWizardIdleLockCombo();
                }
            });
        }
        else if (e.PropertyName == nameof(AppLockViewModel.IsTotpSectionExpanded))
        {
            // 展开后若已有二维码，重新渲染（Image 在 Collapsed 时不加载）
            Dispatcher.Invoke(() =>
            {
                if (AppLock?.IsTotpSectionExpanded != true) return;
                string uri = AppLock?.TotpSetupUri ?? "";
                if (SecurityTotpQrImage is not null && !string.IsNullOrEmpty(uri))
                    SecurityTotpQrImage.Source = QrCodeRenderer.Render(uri, 160);
            });
        }
        else if (e.PropertyName == nameof(AppLockViewModel.IdleTimeoutMinutes))
        {
            Dispatcher.Invoke(() => SyncIdleCombo(SecurityIdleLockCombo, AppLock?.IdleTimeoutMinutes ?? 0));
        }
        else if (e.PropertyName == nameof(AppLockViewModel.IsChangePasswordOpen))
        {
            Dispatcher.Invoke(() =>
            {
                if (AppLock?.IsChangePasswordOpen == true)
                {
                    // 默认聚焦到当前身份确认方式的输入框
                    if (AppLock.IsChangeModePassword) ChangeOldPasswordBox?.Focus();
                    else ChangeTotpBox?.Focus();
                }
            });
        }
        else if (e.PropertyName == nameof(AppLockViewModel.IsDisableConfirmOpen))
        {
            Dispatcher.Invoke(() =>
            {
                if (AppLock?.IsDisableConfirmOpen == true) DisableConfirmPasswordBox?.Focus();
            });
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += MainWindow_Loaded;
        DataContextChanged += MainWindow_DataContextChanged;
        if (DataContext is MainViewModel vm)
        {
            HookMainViewModel(vm);
        }
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        HookMainViewModel(e.NewValue as MainViewModel);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsTaskManagerOpen)
        {
            AnimateOpenTaskManager(animate: false);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 应用锁交互
    // ──────────────────────────────────────────────────────────────────────

    private void WizardPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (AppLock is not null) AppLock.WizardPassword = WizardPasswordBox.Password;
    }

    private void WizardConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (AppLock is not null) AppLock.WizardConfirmPassword = WizardConfirmPasswordBox.Password;
    }

    private void SecurityTotpCodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AppLock is not null) AppLock.TotpCodeInput = SecurityTotpCodeBox.Text;
    }

    // ── 修改密码对话框 ──

    private void ChangeOldPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (AppLock is not null) AppLock.ChangeOldPassword = ChangeOldPasswordBox.Password;
    }

    private void ChangeTotpBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AppLock is not null) AppLock.ChangeTotpCode = ChangeTotpBox.Text;
    }

    private void ChangeNewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (AppLock is not null) AppLock.ChangeNewPassword = ChangeNewPasswordBox.Password;
    }

    private void ChangeConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (AppLock is not null) AppLock.ChangeConfirmPassword = ChangeConfirmPasswordBox.Password;
    }

    // ── 危险操作确认弹窗 ──

    private void DisableConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (AppLock is not null) AppLock.DangerConfirmPassword = DisableConfirmPasswordBox.Password;
    }

    /// <summary>勾选变化后刷新「下一步」可用性与确认页摘要（数据已由双向绑定写入 RootChoice）。</summary>
    private void WizardRootCheckBox_Click(object sender, RoutedEventArgs e)
    {
        AppLock?.NotifyRootSelectionChanged();
    }

    /// <summary>有效期选择变化：立即持久化（纯客户端配置，不重装服务）。</summary>
    private void WizardIdleLockCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyIdleLockFromCombo(WizardIdleLockCombo);
    }

    /// <summary>安全页「自动锁定」下拉：选中项即 IdleLockOption，直接取 Minutes。</summary>
    private void SecurityIdleLockCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppLock is null) return;
        if (SecurityIdleLockCombo?.SelectedItem is not IdleLockOption item) return;
        if (AppLock.IdleTimeoutMinutes != item.Minutes) AppLock.ApplyIdleTimeout(item.Minutes);
    }

    private void ApplyIdleLockFromCombo(ComboBox? combo)
    {
        if (AppLock is null || combo?.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag || !int.TryParse(tag, out int minutes)) return;

        if (AppLock.IdleTimeoutMinutes != minutes)
        {
            AppLock.ApplyIdleTimeout(minutes);
        }
    }

    /// <summary>把 VM 里的当前有效期回填到向导下拉框。</summary>
    private void SyncWizardIdleLockCombo()
    {
        int current = AppLock?.IdleTimeoutMinutes ?? 0;
        SyncIdleCombo(WizardIdleLockCombo, current);
    }

    private static void SyncIdleCombo(ComboBox? combo, int current)
    {
        if (combo is null) return;
        foreach (object obj in combo.Items)
        {
            if (obj is ComboBoxItem item && item.Tag is string tag &&
                int.TryParse(tag, out int minutes) && minutes == current)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppLock is null) return;
        // 用户主动锁定：先收回权限，再弹解锁窗
        await AppLock.LockCommand.ExecuteAsync(null);
        await PromptUnlockAsync();
    }

    /// <summary>
    /// 弹出解锁窗：隐藏主窗口，通过后恢复，取消则关闭应用。
    /// 供「立即锁定」按钮与闲置超时自动锁定共用。
    /// 调用方负责先执行锁定（<c>LockCommand</c>）。
    /// </summary>
    private async Task PromptUnlockAsync()
    {
        if (AppLock is null) return;

        // 若向导正开着，先关掉，避免解锁后草稿态残留
        if (AppLock.IsWizardOpen) AppLock.ForceCloseWizard();

        var unlock = new UnlockWindow(AppLock);
        Hide();
        bool? ok = unlock.ShowDialog();
        if (ok == true && AppLock.IsUnlocked)
        {
            Show();
        }
        else
        {
            Close();
        }

        await Task.CompletedTask;
    }

    private void AppLock_IdleTimeoutReached()
    {
        // 计时器回调在 UI 线程（DispatcherTimer），可直接操作窗口。
        // VM 的 CheckIdle 已自行调用 LockAsync，这里只负责弹解锁窗。
        if (AppLock is null) return;
        if (!AppLock.IsUnlocked) return;
        _ = PromptUnlockAsync();
    }

    private void HookMainViewModel(MainViewModel? vm)
    {
        if (_hookedMainVM != null)
        {
            _hookedMainVM.PropertyChanged -= MainVM_PropertyChanged;
        }
        _hookedMainVM = vm;
        if (_hookedMainVM != null)
        {
            _hookedMainVM.PropertyChanged += MainVM_PropertyChanged;
        }
    }

    private void MainVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsTaskManagerOpen))
        {
            if (_hookedMainVM?.IsTaskManagerOpen == true)
            {
                AnimateOpenTaskManager(animate: true);
            }
            else
            {
                AnimateCloseTaskManager(animate: true);
            }
        }
    }

    public void AnimateOpenTaskManager(bool animate = true)
    {
        if (TaskManagerOverlay == null || TaskManagerTranslate == null) return;

        TaskManagerOverlay.Visibility = Visibility.Visible;
        TaskManagerOverlay.IsHitTestVisible = true;

        if (!animate)
        {
            TaskManagerOverlay.BeginAnimation(OpacityProperty, null);
            TaskManagerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            TaskManagerOverlay.Opacity = 1.0;
            TaskManagerTranslate.Y = 0.0;
            return;
        }

        // Reset previous clocks and set starting values
        TaskManagerOverlay.BeginAnimation(OpacityProperty, null);
        TaskManagerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        TaskManagerOverlay.Opacity = 0.0;
        TaskManagerTranslate.Y = 32.0;

        // PCL2 subpage slide up and fade in
        Controls.PclAnimation.AnimateDouble(TaskManagerOverlay, OpacityProperty, 1.0, 220, Controls.PclAnimation.EaseOutFluentWeak);
        Controls.PclAnimation.AnimateDouble(TaskManagerTranslate, TranslateTransform.YProperty, 0.0, 260, Controls.PclAnimation.EaseOutFluentStrong);
    }

    public void AnimateCloseTaskManager(bool animate = true)
    {
        if (TaskManagerOverlay == null || TaskManagerTranslate == null) return;

        TaskManagerOverlay.IsHitTestVisible = false;

        if (!animate)
        {
            TaskManagerOverlay.BeginAnimation(OpacityProperty, null);
            TaskManagerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            TaskManagerOverlay.Visibility = Visibility.Collapsed;
            TaskManagerOverlay.Opacity = 0.0;
            TaskManagerTranslate.Y = 32.0;
            return;
        }

        // PCL2 subpage slide down and fade out
        Controls.PclAnimation.AnimateDouble(TaskManagerTranslate, TranslateTransform.YProperty, 24.0, 160, Controls.PclAnimation.EaseOutFluentMiddle);
        Controls.PclAnimation.AnimateDouble(TaskManagerOverlay, OpacityProperty, 0.0, 160, Controls.PclAnimation.EaseOutFluentMiddle, onCompleted: () =>
        {
            if (DataContext is MainViewModel vm && !vm.IsTaskManagerOpen)
            {
                TaskManagerOverlay.BeginAnimation(OpacityProperty, null);
                TaskManagerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                TaskManagerOverlay.Visibility = Visibility.Collapsed;
                TaskManagerOverlay.Opacity = 0.0;
                TaskManagerTranslate.Y = 32.0;
            }
        });
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            var topModal = Controls.PclModalHost.GetTopActiveModal();
            if (topModal != null)
            {
                topModal.Close();
                e.Handled = true;
                return;
            }

            if (DataContext is MainViewModel mainVM && mainVM.IsTaskManagerOpen)
            {
                mainVM.CloseTaskManager();
                e.Handled = true;
                return;
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int index))
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SelectedTabIndex = index;
            }
        }
    }

    private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            if (!row.IsSelected)
            {
                if (FindParent<DataGrid>(row) is DataGrid dataGrid)
                {
                    dataGrid.SelectedItem = row.Item;
                }
            }
        }
    }

    private void ListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            if (!item.IsSelected)
            {
                if (FindParent<ListBox>(item) is ListBox listBox)
                {
                    listBox.SelectedItem = item.DataContext;
                }
            }
        }
    }

    private void DataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is DataGridRow { DataContext: MediaSearchResultItem item })
            {
                if (DataContext is MainViewModel { SearchVM: { } searchVM })
                {
                    searchVM.OpenPreviewCommand.Execute(item);
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DataGridRow_MouseDoubleClick error: {ex.Message}");
        }
    }

    private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".webp",
        ".heic", ".heif", ".tif", ".tiff", ".dng", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".rw2", ".orf"
    };

    private string? ResolveDetailFilePath(TransferItemDetail? detail)
    {
        if (detail == null) return null;
        if (!string.IsNullOrEmpty(detail.FullPath) && File.Exists(detail.FullPath))
            return detail.FullPath;
        if (!string.IsNullOrEmpty(detail.TargetPath) && File.Exists(detail.TargetPath))
            return detail.TargetPath;
        if (!string.IsNullOrEmpty(detail.SourcePath) && File.Exists(detail.SourcePath))
            return detail.SourcePath;
        if (DataContext is MainViewModel mainVM && !string.IsNullOrEmpty(detail.TargetPath))
        {
            var dest = mainVM.DestinationPath;
            if (!string.IsNullOrEmpty(dest))
            {
                var candidate = Path.Combine(dest, detail.TargetPath);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private void DetailDataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void DetailDataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is DataGridRow { DataContext: TransferItemDetail detail })
            {
                var path = ResolveDetailFilePath(detail);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    ShellServices.OpenFiles(new[] { path });
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DetailDataGridRow_MouseDoubleClick error: {ex.Message}");
        }
    }

    private void DetailContextMenu_ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TransferItemDetail detail })
        {
            var path = ResolveDetailFilePath(detail);
            if (!string.IsNullOrEmpty(path))
            {
                ShellServices.ShowInExplorer(new[] { path });
            }
        }
    }

    private void DetailContextMenu_OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TransferItemDetail detail })
        {
            var path = ResolveDetailFilePath(detail);
            if (!string.IsNullOrEmpty(path))
            {
                ShellServices.OpenFiles(new[] { path });
            }
        }
    }

    private void DetailContextMenu_CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TransferItemDetail detail })
        {
            var path = ResolveDetailFilePath(detail);
            if (!string.IsNullOrEmpty(path))
            {
                ShellServices.CopyFilesToClipboard(new[] { path });
            }
        }
    }

    private void DetailContextMenu_Properties_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TransferItemDetail detail })
        {
            var path = ResolveDetailFilePath(detail);
            if (!string.IsNullOrEmpty(path))
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                ShellServices.ShowProperties(new[] { path }, hwnd);
            }
        }
    }

    private void ModernListItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { DataContext: MediaSearchResultItem item })
            {
                if (!string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath))
                {
                    ShellServices.OpenFiles([item.FullPath]);
                    e.Handled = true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ModernListItem_MouseDoubleClick error: {ex.Message}");
        }
    }

    private void ModernListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item)
        {
            item.IsSelected = true;
        }
    }

    private void ListItem_ToggleUnified_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            DataContext is MainViewModel { SearchVM: { } searchVM })
        {
            searchVM.ToggleUnifiedManualSelectionCommand.Execute(item);
        }
    }

    private void ListItem_ToggleIPhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            DataContext is MainViewModel { SearchVM: { } searchVM })
        {
            searchVM.ToggleUnifiedManualSelectionCommand.Execute(item);
        }
    }

    private void ListItem_ToggleAndroid_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            DataContext is MainViewModel { SearchVM: { } searchVM })
        {
            searchVM.ToggleUnifiedManualSelectionCommand.Execute(item);
        }
    }

    private void ListItem_Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            !string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath))
        {
            ShellServices.ShowInExplorer([item.FullPath]);
        }
    }

    private void ListItem_Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaSearchResultItem item } &&
            !string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath))
        {
            ShellServices.OpenFiles([item.FullPath]);
        }
    }

    private void ManualListItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TryResolveManualItem(sender, out _, out string path))
        {
            ShellServices.OpenFiles([path]);
            e.Handled = true;
        }
    }

    private void ManualListItem_Open_Click(object sender, RoutedEventArgs e)
    {
        if (TryResolveManualItem(sender, out _, out string path))
        {
            ShellServices.OpenFiles([path]);
        }
    }

    private void ManualListItem_Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (TryResolveManualItem(sender, out _, out string path))
        {
            ShellServices.ShowInExplorer([path]);
        }
    }

    private void ManualListItem_Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetManualItem(sender, out ManualSelectedItemViewModel item) &&
            DataContext is MainViewModel main)
        {
            main.RemoveUnifiedManualItemCommand.Execute(item);
        }
    }

    private bool TryResolveManualItem(object sender, out ManualSelectedItemViewModel item, out string path)
    {
        item = null!;
        path = "";
        if (!TryGetManualItem(sender, out ManualSelectedItemViewModel selected))
        {
            return false;
        }

        item = selected;
        if (!string.IsNullOrWhiteSpace(selected.FullPath) && File.Exists(selected.FullPath))
        {
            path = selected.FullPath;
            return true;
        }

        if (DataContext is MainViewModel main &&
            !string.IsNullOrWhiteSpace(main.DestinationPath) &&
            !string.IsNullOrWhiteSpace(selected.RelativePath))
        {
            string combined = Path.Combine(main.DestinationPath, selected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(combined))
            {
                path = combined;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetManualItem(object sender, out ManualSelectedItemViewModel item)
    {
        item = null!;
        if (sender is not FrameworkElement element)
        {
            return false;
        }

        if (element.DataContext is ManualSelectedItemViewModel direct)
        {
            item = direct;
            return true;
        }

        DependencyObject? current = element;
        while (current != null)
        {
            if (current is ContextMenu menu &&
                menu.PlacementTarget is FrameworkElement target &&
                target.DataContext is ManualSelectedItemViewModel placed)
            {
                item = placed;
                return true;
            }

            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void CloseTaskManager_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel mainVM)
        {
            mainVM.CloseTaskManager();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        try
        {
            base.OnKeyDown(e);
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            if (DataContext is MainViewModel mainVM)
            {
                if (e.Key == Key.Escape && mainVM.IsDetailModalOpen)
                {
                    mainVM.CloseActiveDetailModal();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape && mainVM.IsTaskManagerOpen)
                {
                    mainVM.CloseTaskManager();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    if (mainVM.SelectedTabIndex == 1 && mainVM.IPhoneSyncVM.IsScopeManualSelection)
                    {
                        if (mainVM.IPhoneSyncVM.PasteFilesFromClipboardCommand.CanExecute(null))
                        {
                            mainVM.IPhoneSyncVM.PasteFilesFromClipboardCommand.Execute(null);
                            e.Handled = true;
                        }
                    }
                    else if (mainVM.SelectedTabIndex == 2 && mainVM.AndroidSyncVM.IsScopeManualSelection)
                    {
                        if (mainVM.AndroidSyncVM.PasteFilesFromClipboardCommand.CanExecute(null))
                        {
                            mainVM.AndroidSyncVM.PasteFilesFromClipboardCommand.Execute(null);
                            e.Handled = true;
                        }
                    }
                    else if (mainVM.SelectedTabIndex == 3 && mainVM.GooglePhotosVM.IsScopeManualSelection)
                    {
                        if (mainVM.GooglePhotosVM.PasteFilesFromClipboardCommand.CanExecute(null))
                        {
                            mainVM.GooglePhotosVM.PasteFilesFromClipboardCommand.Execute(null);
                            e.Handled = true;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnKeyDown error: {ex.Message}");
        }
    }

    private void ManualDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void UnifiedManualDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (DataContext is MainViewModel mainVM)
                    {
                        mainVM.AddFilesToUnifiedManual(files);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UnifiedManualDropZone_Drop error: {ex.Message}");
        }
    }

    private void IPhoneManualDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (DataContext is MainViewModel { IPhoneSyncVM: { } vm })
                    {
                        vm.ProcessPickedFiles(files);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IPhoneManualDropZone_Drop error: {ex.Message}");
        }
    }

    private void AndroidManualDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (DataContext is MainViewModel { AndroidSyncVM: { } vm })
                    {
                        vm.ProcessPickedFiles(files);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AndroidManualDropZone_Drop error: {ex.Message}");
        }
    }

    private void GooglePhotosManualDropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (DataContext is MainViewModel { GooglePhotosVM: { } vm })
                    {
                        vm.ProcessPickedFiles(files);
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GooglePhotosManualDropZone_Drop error: {ex.Message}");
        }
    }

    private void SearchResults_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        CheckAndLoadMore(e);
    }

    private void Gallery_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        CheckAndLoadMore(e);
    }

    private void CheckAndLoadMore(ScrollChangedEventArgs e)
    {
        try
        {
            if (e.VerticalChange > 0 && e.ExtentHeight > 0)
            {
                if (e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight) < 300)
                {
                    if (DataContext is MainViewModel { SearchVM: { } searchVM })
                    {
                        if (searchVM.LoadMoreItemsCommand.CanExecute(null))
                        {
                            searchVM.LoadMoreItemsCommand.Execute(null);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScrollChanged error: {ex.Message}");
        }
    }

    private void DeviceHeroArea_MouseEnter(object sender, MouseEventArgs e)
    {
        if (IPhoneHeroHoverMenu != null)
        {
            Controls.PclAnimation.AnimateDouble(IPhoneHeroHoverMenu, HeightProperty, 30.0, 160, Controls.PclAnimation.EaseOutFluentWeak);
            Controls.PclAnimation.AnimateDouble(IPhoneHeroHoverMenu, OpacityProperty, 1.0, 160, Controls.PclAnimation.EaseOutFluentWeak);
        }
        if (AndroidHeroHoverMenu != null)
        {
            Controls.PclAnimation.AnimateDouble(AndroidHeroHoverMenu, HeightProperty, 30.0, 160, Controls.PclAnimation.EaseOutFluentWeak);
            Controls.PclAnimation.AnimateDouble(AndroidHeroHoverMenu, OpacityProperty, 1.0, 160, Controls.PclAnimation.EaseOutFluentWeak);
        }
    }

    private void DeviceHeroArea_MouseLeave(object sender, MouseEventArgs e)
    {
        if (IPhoneHeroHoverMenu != null)
        {
            Controls.PclAnimation.AnimateDouble(IPhoneHeroHoverMenu, HeightProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle);
            Controls.PclAnimation.AnimateDouble(IPhoneHeroHoverMenu, OpacityProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle);
        }
        if (AndroidHeroHoverMenu != null)
        {
            Controls.PclAnimation.AnimateDouble(AndroidHeroHoverMenu, HeightProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle);
            Controls.PclAnimation.AnimateDouble(AndroidHeroHoverMenu, OpacityProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle);
        }
    }

    private void IPhoneHero_MouseEnter(object sender, MouseEventArgs e) => DeviceHeroArea_MouseEnter(sender, e);
    private void IPhoneHero_MouseLeave(object sender, MouseEventArgs e) => DeviceHeroArea_MouseLeave(sender, e);
    private void AndroidHero_MouseEnter(object sender, MouseEventArgs e) => DeviceHeroArea_MouseEnter(sender, e);
    private void AndroidHero_MouseLeave(object sender, MouseEventArgs e) => DeviceHeroArea_MouseLeave(sender, e);

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }

    private void DeviceGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row && row.Item is DeviceHistoryItem item)
        {
            if (DataContext is MainViewModel mainVM)
            {
                mainVM.StatusVM.OpenDeviceHistory(item);
            }
        }
    }

    public void ShowModalDim()
    {
        ModalDimOverlay.Visibility = Visibility.Visible;
        Controls.PclAnimation.AnimateDouble(ModalDimOverlay, OpacityProperty, 1.0, 180, Controls.PclAnimation.EaseOutFluentWeak);
    }

    public void HideModalDim()
    {
        Controls.PclAnimation.AnimateDouble(ModalDimOverlay, OpacityProperty, 0.0, 140, Controls.PclAnimation.EaseOutFluentMiddle, onCompleted: () =>
        {
            ModalDimOverlay.Visibility = Visibility.Collapsed;
        });
    }
}

