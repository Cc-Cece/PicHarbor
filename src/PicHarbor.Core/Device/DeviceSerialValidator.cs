namespace PicHarbor.Core.Device;

/// <summary>
/// 候选硬件序列号不可用的具体原因。
/// </summary>
public enum DeviceSerialRejection
{
    /// <summary>可用。</summary>
    None = 0,

    /// <summary>设备未在 USB 描述符中提供序列号（实例 ID 由 Windows 端口路径合成）。</summary>
    NotProvided,

    /// <summary>空值或纯空白。</summary>
    Empty,

    /// <summary>过短，通常是截断残留。</summary>
    TooShort,

    /// <summary>过长，不像真实序列号。</summary>
    TooLong,

    /// <summary>全部字符相同（如 <c>0000000000</c>），属于占位填充。</summary>
    UniformCharacters,

    /// <summary>含 Windows 设备实例 ID 的分隔符 <c>&amp;</c>，是端口路径合成值而非设备身份。</summary>
    ContainsSeparator,

    /// <summary>命中已知的通用占位串（个别厂商会烧录同一个通用串）。</summary>
    Placeholder,

    /// <summary>已被其它设备登记，无法用于区分设备。</summary>
    Duplicate,
}

/// <summary>
/// 硬件序列号的合理性校验。
/// </summary>
/// <remarks>
/// 只做「单值是否可信」的判定；「是否与既有设备重复」属于跨记录校验，
/// 由调用方（<c>TransferJournal</c>）另行完成。
/// </remarks>
public static class DeviceSerialValidator
{
    /// <summary>可接受的最短长度。</summary>
    public const int MinLength = 4;

    /// <summary>可接受的最长长度。</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// 本项目在归档库中以 <c>android:{标识}</c> 形式构造设备 UDID（见 FtpAndroidBackupClient）。
    /// 该前缀下装的是配置项而非出厂序列号，必须视为占位值。
    /// </summary>
    public const string SyntheticUdidPrefix = "android:";

    private static readonly HashSet<string> KnownPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "0123456789",
        "0123456789abcdef",
        "1234567890",
        "0000000000000000",
        "ffffffffffffffff",
        "unknown",
        "none",
        "null",
        "n/a",
        "na",
        "default",
    };

    /// <summary>
    /// 对候选硬件序列号做纯字符串合理性校验（不含跨设备重复检测）。
    /// </summary>
    /// <param name="serial">待校验的候选值，可为 <c>null</c>。</param>
    /// <returns>不可用原因；<see cref="DeviceSerialRejection.None"/> 表示可用。</returns>
    public static DeviceSerialRejection Validate(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return DeviceSerialRejection.Empty;
        }

        string value = serial.Trim();

        // 本项目自造的 UDID 前缀：里面装的是配置项（如 FTP 主机名），不是出厂序列号。
        if (value.StartsWith(SyntheticUdidPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return DeviceSerialRejection.Placeholder;
        }

        // 真实序列号不会含 & —— 出现即说明是 Windows 合成的端口路径。
        if (value.Contains('&'))
        {
            return DeviceSerialRejection.ContainsSeparator;
        }

        if (value.Length < MinLength)
        {
            return DeviceSerialRejection.TooShort;
        }

        if (value.Length > MaxLength)
        {
            return DeviceSerialRejection.TooLong;
        }

        if (value.All(c => c == value[0]))
        {
            return DeviceSerialRejection.UniformCharacters;
        }

        if (KnownPlaceholders.Contains(value))
        {
            return DeviceSerialRejection.Placeholder;
        }

        return DeviceSerialRejection.None;
    }

    /// <summary>候选序列号是否可直接用于识别设备。</summary>
    public static bool IsUsable(string? serial) => Validate(serial) == DeviceSerialRejection.None;
}
