using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

using PicHarbor.Core.GooglePhotos;

namespace PicHarbor.Gui;

/// <summary>
/// Interaction logic for GoogleLoginWindow.xaml
/// </summary>
public partial class GoogleLoginWindow : Window
{
    private DispatcherTimer? _pollTimer;
    private bool _isCompleted = false;

    public string? ExtractedToken { get; private set; }
    public string? Proxy { get; }

    public GoogleLoginWindow(string? proxy = null)
    {
        InitializeComponent();
        Proxy = proxy;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus(App.GetString("GoogleLoginWindowLoading", "正在加载 Google 登录页面..."), false);

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string normalizedProxy = GooglePhotosSyncEngine.NormalizeProxy(Proxy);
            string proxyFolder = string.IsNullOrWhiteSpace(normalizedProxy)
                ? "default"
                : Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedProxy)))[..8];
            string userDataDir = Path.Combine(localAppData, "PicHarbor", "WebView2", proxyFolder);
            Directory.CreateDirectory(userDataDir);

            CoreWebView2EnvironmentOptions? options = null;
            string proxyArgs = GooglePhotosSyncEngine.BuildWebView2ProxyArguments(Proxy);
            if (!string.IsNullOrWhiteSpace(proxyArgs))
            {
                options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = proxyArgs
                };
            }

            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
            await LoginWebView.EnsureCoreWebView2Async(env);

            // Configure User-Agent:
            // Remove "WebView2/..." from UA so Google doesn't trigger "This browser or app may not be secure".
            string currentUa = LoginWebView.CoreWebView2.Settings.UserAgent;
            if (!string.IsNullOrEmpty(currentUa))
            {
                string cleanedUa = Regex.Replace(currentUa, @"\s*WebView2/\S+", "");
                LoginWebView.CoreWebView2.Settings.UserAgent = cleanedUa;
            }

            // Clear any stale oauth_token cookie before starting so we only capture a fresh token
            await DeleteExistingOAuthTokenAsync();

            // Wire up event handlers
            LoginWebView.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;
            LoginWebView.NavigationCompleted += LoginWebView_NavigationCompleted;
            LoginWebView.SourceChanged += LoginWebView_SourceChanged;
            LoginWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;

            // Start polling timer
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();

            string waitingStatus = App.GetString("GoogleLoginWindowWaiting", "等待登录...");
            if (!string.IsNullOrWhiteSpace(normalizedProxy))
            {
                waitingStatus += $" ({normalizedProxy})";
            }
            SetStatus(waitingStatus, false);
            LoginWebView.CoreWebView2.Navigate("https://accounts.google.com/EmbeddedSetup");
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(App.GetString("FmtGoogleInitFailed", "初始化浏览器失败: {0}"), ex.Message), true);
            MessageBox.Show(this,
                string.Format(App.GetString("FmtGoogleWebView2Failed", "无法初始化内置浏览器 (WebView2):\n{0}\n\n建议您使用「在外部浏览器中打开」并手动输入凭证。"), ex.Message),
                App.GetString("GoogleLoginWindowTitle", "Google 账号登录"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            DialogResult = false;
            Close();
        }
    }

    private async Task DeleteExistingOAuthTokenAsync()
    {
        if (LoginWebView.CoreWebView2 == null) return;
        try
        {
            var allCookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("");
            foreach (var cookie in allCookies)
            {
                if (string.Equals(cookie.Name, "oauth_token", StringComparison.OrdinalIgnoreCase))
                {
                    LoginWebView.CoreWebView2.CookieManager.DeleteCookie(cookie);
                }
            }
        }
        catch
        {
            // Ignore cookie cleanup errors
        }
    }

    private async void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_isCompleted) return;
        if (!string.IsNullOrEmpty(e.Uri) && e.Uri.Contains("oauth_token="))
        {
            string? token = ExtractTokenFromUri(e.Uri);
            if (!string.IsNullOrEmpty(token))
            {
                e.Cancel = true;
                await CheckAndCompleteAsync(token);
            }
        }
    }

    private async void CoreWebView2_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (_isCompleted) return;
        try
        {
            if (e.Response?.Headers?.Contains("Set-Cookie") == true)
            {
                string? setCookie = e.Response.Headers.GetHeader("Set-Cookie");
                if (setCookie != null && setCookie.Contains("oauth_token"))
                {
                    await CheckAndCompleteAsync();
                }
            }
        }
        catch { }
    }

    private async void LoginWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (_isCompleted) return;

        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            string errorText = e.WebErrorStatus switch
            {
                CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.HostNameNotResolved
                    => App.GetString("GoogleLoginWindowCannotConnect", "无法连接至 Google 服务，请检查网络或代理配置"),
                CoreWebView2WebErrorStatus.Timeout
                    => App.GetString("GoogleLoginWindowTimeout", "连接 Google 超时，请检查代理服务是否正常运行"),
                CoreWebView2WebErrorStatus.ServerUnreachable or CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired
                    => App.GetString("GoogleLoginWindowProxyError", "无法连接至配置的代理服务器，请检查代理端口与服务状态"),
                _ => string.Format(App.GetString("FmtGoogleLoadFailed", "加载失败: {0}"), e.WebErrorStatus)
            };
            SetStatus($"⚠️ {errorText}", true);
        }

        await CheckAndCompleteAsync();
    }

    private async void LoginWebView_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (_isCompleted) return;
        if (LoginWebView.Source != null)
        {
            string uriStr = LoginWebView.Source.ToString();
            if (uriStr.Contains("oauth_token="))
            {
                string? token = ExtractTokenFromUri(uriStr);
                if (!string.IsNullOrEmpty(token))
                {
                    await CheckAndCompleteAsync(token);
                    return;
                }
            }
        }
        await CheckAndCompleteAsync();
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_isCompleted) return;
        await CheckAndCompleteAsync();
    }

    private async Task<bool> CheckAndCompleteAsync(string? explicitToken = null)
    {
        if (_isCompleted || LoginWebView.CoreWebView2 == null) return false;

        if (!string.IsNullOrWhiteSpace(explicitToken))
        {
            await OnTokenFoundAsync(explicitToken);
            return true;
        }

        try
        {
            // 1. Query all cookies
            var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("");
            foreach (var cookie in cookies)
            {
                if (string.Equals(cookie.Name, "oauth_token", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(cookie.Value))
                {
                    await OnTokenFoundAsync(cookie.Value);
                    return true;
                }
            }

            // 2. Fallback check explicit Google domains
            var googleCookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.google.com");
            foreach (var cookie in googleCookies)
            {
                if (string.Equals(cookie.Name, "oauth_token", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(cookie.Value))
                {
                    await OnTokenFoundAsync(cookie.Value);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore cookie polling errors
        }

        return false;
    }

    private async Task OnTokenFoundAsync(string token)
    {
        if (_isCompleted) return;
        _isCompleted = true;
        _pollTimer?.Stop();
        ExtractedToken = token.Trim();

        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingText.Text = App.GetString("GoogleLoginWindowCaptured", "✅ 登录成功！已成功捕获凭据，正在返回...");
        SetStatus(App.GetString("GoogleLoginWindowCaptured", "✅ 登录成功！已成功捕获凭据，正在返回..."), false);

        await Task.Delay(800);
        DialogResult = true;
        Close();
    }

    private string? ExtractTokenFromUri(string uri)
    {
        try
        {
            var match = Regex.Match(uri, @"[?&#]oauth_token=([^&]+)");
            if (match.Success)
            {
                return Uri.UnescapeDataString(match.Groups[1].Value);
            }
        }
        catch { }
        return null;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusTextBlock.Text = message;
        if (isError)
        {
            StatusTextBlock.Foreground = (System.Windows.Media.Brush?)TryFindResource("ColorBrushRed")
                ?? (System.Windows.Media.Brush?)TryFindResource("AccentRedBrush")
                ?? System.Windows.Media.Brushes.Red;
        }
        else
        {
            StatusTextBlock.Foreground = (System.Windows.Media.Brush?)TryFindResource("ColorBrushGray2")
                ?? (System.Windows.Media.Brush?)TryFindResource("TextSecondaryBrush")
                ?? System.Windows.Media.Brushes.DarkSlateGray;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        _pollTimer?.Stop();
        DialogResult = false;
        Close();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = App.GetString("GoogleLoginWindowLoading", "正在加载 Google 登录页面...");
            LoginWebView.CoreWebView2?.Reload();
        }
        catch { }
    }

    private async void ClearCookiesBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LoginWebView.CoreWebView2 != null)
            {
                LoginWebView.CoreWebView2.CookieManager.DeleteAllCookies();
                SetStatus(App.GetString("GoogleLoginWindowWaiting", "等待登录..."), false);
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingText.Text = App.GetString("GoogleLoginWindowLoading", "正在加载 Google 登录页面...");
                LoginWebView.CoreWebView2.Navigate("https://accounts.google.com/EmbeddedSetup");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format(App.GetString("FmtGoogleClearCacheFailed", "清除缓存失败: {0}"), ex.Message), App.GetString("MsgBoxTitle", "提示"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _pollTimer?.Stop();
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer?.Stop();
        try { LoginWebView.Dispose(); } catch { }
        base.OnClosed(e);
    }
}
