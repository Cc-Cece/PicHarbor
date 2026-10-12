// PicHarbor.Gui — 应用锁解锁窗口
// 密码通过之前不构造主窗口；解锁成功后关闭本窗口并放行主窗口。

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PicHarbor.Gui.ViewModels;

namespace PicHarbor.Gui;

public partial class UnlockWindow : Window
{
    private readonly AppLockViewModel _vm;

    public UnlockWindow(AppLockViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.ResetInputs();
        _vm.PropertyChanged += OnVmPropertyChanged;
        Loaded += (_, _) => PasswordBox.Focus();
        Closed += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 解锁成功 → 关闭本窗口，放行主窗口
        if (e.PropertyName == nameof(AppLockViewModel.IsUnlocked) && _vm.IsUnlocked)
        {
            Dispatcher.Invoke(() =>
            {
                DialogResult = true;
                Close();
            });
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    /// <summary>最小化：保持锁定态，仅收起窗口。</summary>
    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    /// <summary>解锁视图：在密码框内按回车等价于点击「解锁」。</summary>
    private void UnlockInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (_vm.UnlockCommand.CanExecute(null))
        {
            _vm.UnlockCommand.Execute(null);
        }

        e.Handled = true;
    }

    /// <summary>重置视图：在动态码/新密码输入框内按回车等价于点击「重置密码」。</summary>
    private void ResetInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ResetPassword_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>
    /// 关闭：等价于「取消解锁」。
    /// DialogResult=false 让主窗口走退出流程（见 MainWindow.PromptUnlockAsync）。
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            // 非 ShowDialog 调用时 DialogResult 不可写，直接关闭即可。
        }

        Close();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _vm.PasswordInput = PasswordBox.Password;

    private void ResetTotpBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => _vm.TotpCodeInput = ResetTotpBox.Text;

    private void ResetNewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _vm.NewPasswordInput = ResetNewPasswordBox.Password;

    private void ShowReset_Click(object sender, RoutedEventArgs e)
    {
        UnlockPanel.Visibility = Visibility.Collapsed;
        ResetPanel.Visibility = Visibility.Visible;
        _vm.StatusMessage = "";
        ResetTotpBox.Focus();
    }

    private void BackToUnlock_Click(object sender, RoutedEventArgs e)
    {
        ResetPanel.Visibility = Visibility.Collapsed;
        UnlockPanel.Visibility = Visibility.Visible;
        _vm.StatusMessage = "";
        PasswordBox.Focus();
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        // 重置成功 ≠ 解锁：成功后回到解锁视图，用户须用新密码再解锁一次
        await _vm.ResetPasswordWithTotpCommand.ExecuteAsync(null);

        if (!_vm.IsError && _vm.StatusMessage.Length > 0 && !_vm.IsUnlocked)
        {
            BackToUnlock_Click(sender, e);
            _vm.StatusMessage = App.GetString("AppLockMsgResetOk", "密码已重置。请用新密码解锁。");
        }
    }
}
