namespace PicHarbor.Core.Android;

/// <summary>配对流程中的一个阶段。</summary>
public enum PairStepStage
{
    /// <summary>连接 FTP。</summary>
    Connecting,

    /// <summary>读取设备端已有标识。</summary>
    ReadingRemote,

    /// <summary>写入设备端标识文件。</summary>
    WritingRemote,

    /// <summary>回读验证。</summary>
    VerifyingRemote,
}

/// <summary>单个阶段的执行状态。</summary>
public enum PairStepStatus
{
    /// <summary>进行中。</summary>
    Running,

    /// <summary>成功。</summary>
    Succeeded,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>已跳过（例如设备端已是目标标识，无需写入）。</summary>
    Skipped,
}

/// <summary>一条配对步骤记录，用于向界面实时反馈。</summary>
public sealed record PairStep(PairStepStage Stage, PairStepStatus Status, string Detail);

/// <summary>配对结果。</summary>
public enum PairOutcome
{
    /// <summary>配对完成（已写入且回读验证通过）。</summary>
    Paired,

    /// <summary>设备端已是同一个标识，无需操作。</summary>
    AlreadyPaired,

    /// <summary>设备端已有不同标识，且用户拒绝覆盖。</summary>
    Declined,

    /// <summary>失败。</summary>
    Failed,
}

/// <summary>配对结果详情。</summary>
/// <remarks>
/// <see cref="RemoteId"/> 为设备端读到的标识（可能为 <c>null</c>）；
/// 失败时 <see cref="Message"/> 携带原因，界面据此提示。
/// </remarks>
public sealed record PairResult(PairOutcome Outcome, string DeviceId, string? RemoteId, string Message);

/// <summary>
/// 设备配对：把一个唯一码写入设备端固定目录，并<b>回读验证</b>确认落盘。
/// </summary>
/// <remarks>
/// <para>
/// 配对是<b>唯一</b>改变设备端身份的动作。它必须绑定到一个明确的设备记录——
/// 否则存在「把 A 的身份写到 B 手机上」的风险，这与本方案要防的错误同类。
/// </para>
/// <para>
/// <b>回读验证是配对成功的唯一判据。</b> FTP 的 <c>STOR</c> 可能返回成功但文件并未真正落盘
/// （权限、配额、路径问题），因此必须写入后再读回逐字比对。
/// </para>
/// </remarks>
public static class AndroidPairingService
{
    /// <summary>设备端身份文件的完整路径。</summary>
    public static string RemoteIdentityPath(string remoteTargetDir)
    {
        string dir = remoteTargetDir.Replace('\\', '/').TrimEnd('/');
        return $"{dir}/{AndroidDeviceDetector.SerialFileName}";
    }

    /// <summary>
    /// 执行配对：连接 FTP → 读设备端已有标识 → 决策 → 写入 → 回读验证。
    /// </summary>
    /// <param name="ftpHost">FTP 主机。</param>
    /// <param name="ftpPort">FTP 端口。</param>
    /// <param name="ftpUser">FTP 用户名。</param>
    /// <param name="ftpPassword">FTP 密码。</param>
    /// <param name="remoteTargetDir">设备端固定目标目录。</param>
    /// <param name="targetDeviceId">要写入的唯一码。</param>
    /// <param name="confirmOverwrite">
    /// 当设备端已存在<b>不同</b>标识时调用，参数为设备端当前标识；返回 <c>false</c> 表示用户拒绝覆盖。
    /// 为 <c>null</c> 时一律拒绝覆盖（保守，绝不静默改写别人的身份）。
    /// </param>
    /// <param name="progress">分步进度回调，用于界面实时反馈。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<PairResult> PairAsync(
        string ftpHost,
        int ftpPort,
        string ftpUser,
        string ftpPassword,
        string remoteTargetDir,
        string targetDeviceId,
        Func<string, bool>? confirmOverwrite = null,
        IProgress<PairStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ftpHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteTargetDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);

        string target = targetDeviceId.Trim();
        string remotePath = RemoteIdentityPath(remoteTargetDir);

        // ① 连接
        progress?.Report(new PairStep(PairStepStage.Connecting, PairStepStatus.Running, $"{ftpHost}:{ftpPort}"));
        using var ftp = new SimpleFtpClient();
        try
        {
            await ftp.ConnectAsync(ftpHost, ftpPort, ftpUser, ftpPassword, cancellationToken).ConfigureAwait(false);
            progress?.Report(new PairStep(PairStepStage.Connecting, PairStepStatus.Succeeded, $"{ftpHost}:{ftpPort}"));
        }
        catch (Exception ex)
        {
            progress?.Report(new PairStep(PairStepStage.Connecting, PairStepStatus.Failed, ex.Message));
            return new PairResult(PairOutcome.Failed, target, null, ex.Message);
        }

        // ② 读设备端已有标识
        progress?.Report(new PairStep(PairStepStage.ReadingRemote, PairStepStatus.Running, remotePath));
        string? remote;
        try
        {
            remote = await AndroidDeviceDetector
                .TryReadDeviceSerialAsync(ftp, remoteTargetDir, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new PairStep(PairStepStage.ReadingRemote, PairStepStatus.Succeeded, remote ?? "（未找到）"));
        }
        catch (Exception ex)
        {
            progress?.Report(new PairStep(PairStepStage.ReadingRemote, PairStepStatus.Failed, ex.Message));
            return new PairResult(PairOutcome.Failed, target, null, ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(remote))
        {
            string remoteId = remote.Trim();

            if (string.Equals(remoteId, target, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new PairStep(PairStepStage.WritingRemote, PairStepStatus.Skipped, remoteId));
                progress?.Report(new PairStep(PairStepStage.VerifyingRemote, PairStepStatus.Skipped, remoteId));
                return new PairResult(PairOutcome.AlreadyPaired, target, remoteId, remoteId);
            }

            // 设备端已有不同标识：必须由用户确认，绝不静默覆盖。
            if (confirmOverwrite is null || !confirmOverwrite(remoteId))
            {
                progress?.Report(new PairStep(PairStepStage.WritingRemote, PairStepStatus.Skipped, remoteId));
                progress?.Report(new PairStep(PairStepStage.VerifyingRemote, PairStepStatus.Skipped, remoteId));
                return new PairResult(PairOutcome.Declined, target, remoteId, remoteId);
            }
        }

        // ③ 写入
        progress?.Report(new PairStep(PairStepStage.WritingRemote, PairStepStatus.Running, remotePath));
        try
        {
            await AndroidDeviceDetector
                .WriteDeviceSerialAsync(ftp, remoteTargetDir, target, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new PairStep(PairStepStage.WritingRemote, PairStepStatus.Succeeded, remotePath));
        }
        catch (Exception ex)
        {
            progress?.Report(new PairStep(PairStepStage.WritingRemote, PairStepStatus.Failed, ex.Message));
            return new PairResult(PairOutcome.Failed, target, remote, ex.Message);
        }

        // ④ 回读验证 —— 唯一判据
        progress?.Report(new PairStep(PairStepStage.VerifyingRemote, PairStepStatus.Running, remotePath));
        string? readBack;
        try
        {
            readBack = await AndroidDeviceDetector
                .TryReadDeviceSerialAsync(ftp, remoteTargetDir, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            progress?.Report(new PairStep(PairStepStage.VerifyingRemote, PairStepStatus.Failed, ex.Message));
            return new PairResult(PairOutcome.Failed, target, remote, ex.Message);
        }

        if (!string.Equals(readBack?.Trim(), target, StringComparison.OrdinalIgnoreCase))
        {
            string detail = string.IsNullOrWhiteSpace(readBack) ? "（读回为空）" : readBack.Trim();
            progress?.Report(new PairStep(PairStepStage.VerifyingRemote, PairStepStatus.Failed, detail));
            return new PairResult(PairOutcome.Failed, target, readBack, detail);
        }

        progress?.Report(new PairStep(PairStepStage.VerifyingRemote, PairStepStatus.Succeeded, target));
        return new PairResult(PairOutcome.Paired, target, target, target);
    }
}
