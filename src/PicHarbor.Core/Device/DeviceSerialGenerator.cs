using System.Security.Cryptography;

namespace PicHarbor.Core.Device;

/// <summary>
/// 生成 PC 侧内部自定义设备标识——这是本项目<b>唯一的设备身份来源</b>。
/// </summary>
/// <remarks>
/// <para>
/// 不使用硬件序列号：免 ADB 读取它的唯一途径是 USB 描述符 / WPD 注册表，而该注册表包含历史条目、
/// USB 模式变化会改变 PID 与实例 ID、部分机型根本未烧录唯一序列号——既不可靠，且相对本方案<b>毫无功能增益</b>：
/// 连错设备时的拦截能力完全来自「手机上报的码与 PC 侧基准比对」，而码本就由本类生成并写入手机。
/// </para>
/// <para>
/// 采用 Crockford Base32 字母表（剔除易混淆的 <c>I</c> / <c>L</c> / <c>O</c> / <c>U</c>），
/// 便于用户手抄与口述。6 位可提供 32 的 6 次方 ≈ 1.07×10^9 种组合，对单机归档场景绰绰有余。
/// </para>
/// </remarks>
public static class DeviceSerialGenerator
{
    /// <summary>内部标识前缀，便于与其它形态的标识区分。</summary>
    public const string InternalPrefix = "PH-";

    /// <summary>Crockford Base32 字母表（无 I / L / O / U）。</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>随机体长度。</summary>
    public const int BodyLength = 6;

    /// <summary>生成标识的总长度（<c>PH-</c> + 6）。</summary>
    public const int TotalLength = 3 + BodyLength;

    /// <summary>
    /// 生成一个内部自定义标识，如 <c>PH-7K3M9Q2X</c>。
    /// </summary>
    /// <remarks>
    /// 使用密码学随机源；<c>256 % 32 == 0</c>，故取模无偏。
    /// </remarks>
    public static string CreateInternalId()
    {
        Span<byte> bytes = stackalloc byte[BodyLength];
        RandomNumberGenerator.Fill(bytes);

        Span<char> body = stackalloc char[BodyLength];
        for (int i = 0; i < BodyLength; i++)
        {
            body[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return string.Concat(InternalPrefix, new string(body));
    }

    /// <summary>判断标识是否为 PC 侧生成的内部自定义标识。</summary>
    public static bool IsInternalId(string? serial) =>
        !string.IsNullOrWhiteSpace(serial) &&
        serial.Trim().StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase);
}
