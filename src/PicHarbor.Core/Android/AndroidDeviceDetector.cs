namespace PicHarbor.Core.Android;

/// <summary>
/// 设备标识的来源，用于日志与界面提示。
/// </summary>
public enum DeviceIdentitySource
{
    /// <summary>PC 侧已确认的标识（最稳，不依赖设备端文件）。</summary>
    PcConfirmed,

    /// <summary>设备端 <c>.picharbor-serial</c> 中的标识。</summary>
    PhoneSerialFile,

    /// <summary>设备端旧版识别文件（<c>.picharbor-device-id</c> / <c>.getandsee-device-id</c>）中的标识。</summary>
    PhoneLegacyFile,

    /// <summary>调用方传入的配置值（设备端与 PC 侧都没有时）。</summary>
    Configured,

    /// <summary>全新生成。</summary>
    Generated,
}

/// <summary>
/// 目标设备与 PC 侧已确认标识的核对结果。
/// </summary>
public enum DeviceIdentityMatch
{
    /// <summary>设备端标识与 PC 侧已确认标识一致。</summary>
    Matched,

    /// <summary>设备端标识存在，但与 PC 侧已确认标识不符（可能连错设备，或换了一台设备）。</summary>
    Mismatched,

    /// <summary>PC 侧有已确认标识，但设备端缺少识别文件，<b>无法核对</b>。</summary>
    Unverifiable,

    /// <summary>PC 侧没有已确认标识，无从核对（首次绑定或全新设备）。</summary>
    NoBaseline,
}

/// <summary>
/// 回传（PC → 安卓）前解析出的目标设备身份。
/// </summary>
/// <param name="DeviceId">最终用于记录分区的设备标识；无法确定时为空字符串。</param>
/// <param name="Source">该标识的来源。</param>
/// <param name="RemoteId">设备端读到的标识；设备端没有识别文件时为 <c>null</c>。</param>
/// <param name="Match">与 PC 侧已确认标识的核对结果。</param>
/// <param name="BaselineId">PC 侧已确认的标识（核对基准）；PC 侧没有时为空。</param>
public sealed record DeviceIdentityResult(
    string DeviceId,
    DeviceIdentitySource Source,
    string? RemoteId,
    DeviceIdentityMatch Match,
    string BaselineId = "")
{
    /// <summary>是否需要用户确认后才能继续（不一致或无法核对）。</summary>
    public bool RequiresConfirmation =>
        Match is DeviceIdentityMatch.Mismatched or DeviceIdentityMatch.Unverifiable;
}

/// <summary>
/// 安卓 FTP 设备识别与配对：读取设备端识别文件、解析设备身份、写入唯一码凭据。
/// </summary>
/// <remarks>
/// <b>身份真相在 PC 侧。</b> 设备端文件只用于<b>核对</b>：PC 侧已确认的标识是基准，
/// 设备端读到的值用来判断「当前连的是不是我以为的那台」。
/// 设备端文件丢失只会导致「无法核对」，不会改变身份。
/// </remarks>
public static class AndroidDeviceDetector
{
    /// <summary>设备端存放唯一码凭据的文件名。</summary>
    public const string SerialFileName = ".picharbor-serial";

    /// <summary>设备端存放设备标识的文件名。</summary>
    public const string DeviceIdFileName = ".picharbor-device-id";

    /// <summary>旧版本（get-and-see）使用的设备标识文件名。</summary>
    public const string LegacyDeviceIdFileName = ".getandsee-device-id";

    /// <summary>
    /// 读取设备端的唯一码凭据（<c>.picharbor-serial</c>）；不存在时返回 <c>null</c>。
    /// </summary>
    public static async Task<string?> TryReadDeviceSerialAsync(
        SimpleFtpClient ftp,
        string remoteTargetDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ftp);
        return await TryReadFirstLineAsync(ftp, remoteTargetDir, SerialFileName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取设备端的旧版识别文件（<c>.picharbor-device-id</c> → <c>.getandsee-device-id</c>）。
    /// </summary>
    public static async Task<string?> TryReadLegacyDeviceIdAsync(
        SimpleFtpClient ftp,
        string remoteTargetDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ftp);

        string? id = await TryReadFirstLineAsync(ftp, remoteTargetDir, DeviceIdFileName, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        return await TryReadFirstLineAsync(ftp, remoteTargetDir, LegacyDeviceIdFileName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把唯一码凭据写入设备端固定目录（<c>/DCIM/PicHarbor/.picharbor-serial</c>）。
    /// </summary>
    public static async Task WriteDeviceSerialAsync(
        SimpleFtpClient ftp,
        string remoteTargetDir,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ftp);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await ftp.EnsureDirectoryExistsAsync(remoteTargetDir, cancellationToken).ConfigureAwait(false);

        string normalizedDir = remoteTargetDir.Replace('\\', '/').TrimEnd('/');
        string serialFilePath = $"{normalizedDir}/{SerialFileName}";

        string content =
            $"""
            {deviceId.Trim()}
            PicHarbor Android Device Identity Credential.
            Do NOT delete or edit this file.
            (设备身份凭据，请勿修改或删除)
            """;

        await ftp.UploadTextAsync(serialFilePath, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析目标设备的身份，并给出与 PC 侧已确认标识的核对结果。<b>只读</b>，不写入任何文件。
    /// </summary>
    /// <param name="ftp">已连接的 FTP 客户端。</param>
    /// <param name="remoteTargetDir">设备端目标目录（固定为 <see cref="AndroidRemoteLayout.RootDirectory"/>）。</param>
    /// <param name="pcConfirmedId">PC 侧已确认的唯一码；没有则为 <c>null</c> 或空白。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<DeviceIdentityResult> ResolveIdentityAsync(
        SimpleFtpClient ftp,
        string remoteTargetDir,
        string? pcConfirmedId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ftp);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteTargetDir);

        await ftp.EnsureDirectoryExistsAsync(remoteTargetDir, cancellationToken).ConfigureAwait(false);

        string? remoteId = await TryReadDeviceSerialAsync(ftp, remoteTargetDir, cancellationToken).ConfigureAwait(false);
        DeviceIdentitySource remoteSource = DeviceIdentitySource.PhoneSerialFile;

        if (string.IsNullOrWhiteSpace(remoteId))
        {
            remoteId = await TryReadLegacyDeviceIdAsync(ftp, remoteTargetDir, cancellationToken).ConfigureAwait(false);
            remoteSource = DeviceIdentitySource.PhoneLegacyFile;
        }

        return ResolveFromValues(pcConfirmedId, remoteId, remoteSource);
    }

    /// <summary>
    /// 纯决策：给定 PC 侧已确认标识与设备端读到的标识，决定最终标识与核对结果。
    /// </summary>
    /// <param name="pcConfirmedId">PC 侧已确认的唯一码（核对基准）；没有则为 <c>null</c> 或空白。</param>
    /// <param name="remoteId">设备端读到的标识；设备端没有识别文件时为 <c>null</c> 或空白。</param>
    /// <param name="remoteSource">设备端标识的来源。</param>
    /// <remarks>
    /// 规则：
    /// <list type="bullet">
    /// <item>无基准 → 采用设备端的值（有的话），标记 <see cref="DeviceIdentityMatch.NoBaseline"/>。</item>
    /// <item>有基准、设备端无文件 → <b>无法核对</b>，身份仍取基准（保证记录稳定）。</item>
    /// <item>有基准、设备端有值 → 相同即 <see cref="DeviceIdentityMatch.Matched"/>；
    /// 不同则取<b>设备端的值</b>（按「实际连到的设备」记录）并标记
    /// <see cref="DeviceIdentityMatch.Mismatched"/>，交由调用方请用户确认。</item>
    /// </list>
    /// </remarks>
    public static DeviceIdentityResult ResolveFromValues(
        string? pcConfirmedId,
        string? remoteId,
        DeviceIdentitySource remoteSource = DeviceIdentitySource.PhoneSerialFile)
    {
        string? remote = string.IsNullOrWhiteSpace(remoteId) ? null : remoteId.Trim();
        string? baseline = string.IsNullOrWhiteSpace(pcConfirmedId) ? null : pcConfirmedId.Trim();

        if (baseline is null)
        {
            return remote is not null
                ? new DeviceIdentityResult(remote, remoteSource, remote, DeviceIdentityMatch.NoBaseline)
                : new DeviceIdentityResult(string.Empty, DeviceIdentitySource.Configured, null, DeviceIdentityMatch.NoBaseline);
        }

        if (remote is null)
        {
            return new DeviceIdentityResult(baseline, DeviceIdentitySource.PcConfirmed, null, DeviceIdentityMatch.Unverifiable, baseline);
        }

        return string.Equals(remote, baseline, StringComparison.OrdinalIgnoreCase)
            ? new DeviceIdentityResult(baseline, DeviceIdentitySource.PcConfirmed, remote, DeviceIdentityMatch.Matched, baseline)
            : new DeviceIdentityResult(remote, remoteSource, remote, DeviceIdentityMatch.Mismatched, baseline);
    }

    private static async Task<string?> TryReadFirstLineAsync(
        SimpleFtpClient ftp,
        string remoteTargetDir,
        string fileName,
        CancellationToken cancellationToken)
    {
        try
        {
            string normalizedDir = remoteTargetDir.Replace('\\', '/').TrimEnd('/');
            string? content = await ftp.DownloadTextAsync($"{normalizedDir}/{fileName}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]) ? lines[0].Trim() : null;
        }
        catch
        {
            // 文件不存在或不可读，视为「没有」。
            return null;
        }
    }
}
