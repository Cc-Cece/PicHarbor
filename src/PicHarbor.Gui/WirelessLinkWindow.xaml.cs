using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PicHarbor.Core.Link;
using PicHarbor.Core.Organize;
using QRCoder;

namespace PicHarbor.Gui;

public partial class WirelessLinkWindow : Window
{
    private LinkHost? host;

    public WirelessLinkWindow(string archiveRoot, OrganizeScheme scheme, bool groupByDevice)
    {
        InitializeComponent();
        try
        {
            host = LinkHost.Start(new LinkHostOptions
            {
                ArchiveRoot = archiveRoot,
                Scheme = scheme,
                GroupByDevice = groupByDevice,
            });
            host.Status += message => Dispatcher.Invoke(() =>
            {
                ShowSession();
                LogText.AppendText(message + Environment.NewLine);
                LogText.ScrollToEnd();
            });
            ShowSession();
            StatusText.Text = App.GetString("WirelessFirewallHint", "如果手机一直连不上，请在 Windows 防火墙里允许 PicHarbor.Gui.exe 接收入站连接。");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ShowSession()
    {
        if (host is null)
        {
            return;
        }

        CodeText.Text = host.Code;
        FingerprintText.Text = App.GetString("WirelessCertPrefix", "证书 ") + host.Fingerprint[..16];
        AddressText.Text = string.Join("  ", host.Addresses.Select(address => address + ":" + host.Port));
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(host.QrText, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        byte[] bytes = png.GetGraphic(8);
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        QrImage.Source = image;
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (host is not null)
        {
            await host.DisposeAsync();
            host = null;
        }

        Close();
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (host is not null)
        {
            await host.DisposeAsync();
            host = null;
        }

        base.OnClosed(e);
    }
}
