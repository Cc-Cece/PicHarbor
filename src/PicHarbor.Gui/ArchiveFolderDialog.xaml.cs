using System.Windows;
using System.Windows.Input;
using PicHarbor.Gui.Controls;

namespace PicHarbor.Gui;

/// <summary>
/// 首次备份前确认归档文件夹名。
/// </summary>
/// <remarks>
/// 归档文件夹名参与 manifest 的相对路径，一旦有文件写入就<b>不可更改</b>——
/// 改动会让已有文件与新文件落进不同目录。因此这里只给用户<b>一次</b>机会。
/// </remarks>
public partial class ArchiveFolderDialog : Window
{
    private readonly Func<string, Task<string?>> validateAsync;
    private bool isClosing;

    /// <summary>用户确认后的文件夹名（仅当 <see cref="Confirmed"/> 为 <c>true</c> 时有效）。</summary>
    public string FolderName { get; private set; } = string.Empty;

    /// <summary>用户是否确认。</summary>
    public bool Confirmed { get; private set; }

    /// <param name="suggestedName">默认值。</param>
    /// <param name="validateAsync">
    /// 校验回调，返回错误信息；<c>null</c> 表示通过。异步是因为重名校验要查归档库
    /// （预加载设备名列表会把当前设备自己算成冲突）。
    /// </param>
    public ArchiveFolderDialog(string suggestedName, Func<string, Task<string?>> validateAsync)
    {
        InitializeComponent();

        this.validateAsync = validateAsync ?? (_ => Task.FromResult<string?>(null));

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CancelButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };

        Loaded += (_, _) =>
        {
            PclAnimation.AnimateDouble(RootBorder, OpacityProperty, 1.0, 180, PclAnimation.EaseOutFluentWeak);
            PclAnimation.AnimateScale(RootScaleTransform, 1.0, 240, PclAnimation.EaseOutBack);

            FolderNameBox.Text = suggestedName ?? string.Empty;
            FolderNameBox.Focus();
            FolderNameBox.SelectAll();
        };
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        string value = FolderNameBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            ShowError(App.GetString("ArchiveFolderEmpty", "文件夹名不能为空。"));
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;

        string? error;
        try
        {
            error = await validateAsync(value);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!string.IsNullOrEmpty(error))
        {
            ShowError(error);
            return;
        }

        FolderName = value;
        Confirmed = true;
        CloseWithAnimation();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        CloseWithAnimation();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    public void CloseWithAnimation()
    {
        if (isClosing) return;
        isClosing = true;

        PclAnimation.AnimateScale(RootScaleTransform, 0.94, 150, PclAnimation.EaseOutFluentMiddle);
        PclAnimation.AnimateDouble(RootBorder, OpacityProperty, 0.0, 140, PclAnimation.EaseOutFluentMiddle, onCompleted: Close);
    }
}
