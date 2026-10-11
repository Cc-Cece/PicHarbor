// PicHarbor.Gui — QR 码渲染器
// 用 QRCoder 生成 otpauth:// URI 的二维码位图，供 TOTP 绑定显示。

using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace PicHarbor.Gui.Util;

/// <summary>
/// 将文本编码为 QR 码位图。使用 QRCoder（MIT）保证规范正确，
/// Google Authenticator 可正常扫描。
/// </summary>
internal static class QrCodeRenderer
{
    /// <summary>渲染文本为 <paramref name="sizePx"/> 见方的位图。</summary>
    public static ImageSource Render(string text, int sizePx)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M, forceUtf8: true);

        // 每模块像素数：目标尺寸除以模块数（含静区），至少 1
        int modules = data.ModuleMatrix.Count + 8;
        int pixelsPerModule = Math.Max(1, sizePx / modules);

        var png = new PngByteQRCode(data);
        byte[] pngBytes = png.GetGraphic(pixelsPerModule, drawQuietZones: true);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new System.IO.MemoryStream(pngBytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
