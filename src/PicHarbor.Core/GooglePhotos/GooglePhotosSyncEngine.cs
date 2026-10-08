using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Search;
using PicHarbor.Core.Util;

namespace PicHarbor.Core.GooglePhotos;

/// <summary>
/// Engine coordinating file synchronization and credential testing with Google Photos via gpmc.
/// </summary>
public static class GooglePhotosSyncEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Directory in LocalApplicationData where the embedded gpmc runtime is extracted.
    /// Strictly isolated; never pollutes current working directory or user desktop.
    /// Path: %LocalAppData%\PicHarbor\runtime\
    /// </summary>
    public static string RuntimeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PicHarbor",
        "runtime");

    /// <summary>
    /// Locates or extracts the gpmc_bridge.py script and bundled gpmc package into %LocalAppData%\PicHarbor\runtime\.
    /// Returns the absolute path to gpmc_bridge.py.
    /// </summary>
    public static string LocateBridgeScript()
    {
        string targetDir = RuntimeDirectory;
        Directory.CreateDirectory(targetDir);

        string targetBridge = Path.Combine(targetDir, "gpmc_bridge.py");
        string versionMarkerFile = Path.Combine(targetDir, "gpmc_bundle_version.txt");

        // Obtain embedded gpmc_bundle.zip if present
        byte[]? bundleBytes = null;
        var assembly = typeof(GooglePhotosSyncEngine).Assembly;
        Stream? resourceStream = assembly.GetManifestResourceStream("gpmc_bundle.zip");
        if (resourceStream == null)
        {
            string? resName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("gpmc_bundle.zip", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                resourceStream = assembly.GetManifestResourceStream(resName);
            }
        }

        if (resourceStream != null)
        {
            using (resourceStream)
            using (var ms = new MemoryStream())
            {
                resourceStream.CopyTo(ms);
                bundleBytes = ms.ToArray();
            }
        }

        // Compute deterministic version marker based on embedded zip hash
        string versionMarker = bundleBytes != null
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bundleBytes))
            : (assembly.GetName().Version?.ToString() ?? "1.0.0");

        bool needsExtraction = !File.Exists(targetBridge)
            || !Directory.Exists(Path.Combine(targetDir, "gpmc"))
            || !File.Exists(versionMarkerFile)
            || !string.Equals(File.ReadAllText(versionMarkerFile).Trim(), versionMarker, StringComparison.OrdinalIgnoreCase);

        if (needsExtraction)
        {
            if (bundleBytes != null)
            {
                // Purge any stale python bytecode before extracting updated files
                try
                {
                    foreach (var pycacheDir in Directory.GetDirectories(targetDir, "__pycache__", SearchOption.AllDirectories))
                    {
                        try { Directory.Delete(pycacheDir, true); } catch { }
                    }
                }
                catch { }

                using var archive = new ZipArchive(new MemoryStream(bundleBytes), ZipArchiveMode.Read);
                archive.ExtractToDirectory(targetDir, overwriteFiles: true);
                File.WriteAllText(versionMarkerFile, versionMarker, Utf8NoBom);
            }
            else
            {
                // Fallback to embedded string if resource stream is unavailable
                File.WriteAllText(targetBridge, EmbeddedBridgeSource, Utf8NoBom);
            }
        }

        return targetBridge;
    }

    private static JsonElement? ExtractJsonResult(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("__GPMC_RESULT__:", StringComparison.Ordinal))
            {
                string json = trimmed.Substring("__GPMC_RESULT__:".Length).Trim();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.Clone();
                }
                catch { }
            }
        }
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    return doc.RootElement.Clone();
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Normalizes raw proxy string or port into a standard proxy URL (e.g. 7890 -> http://127.0.0.1:7890).
    /// </summary>
    public static string NormalizeProxy(string? proxyStr)
    {
        string p = (proxyStr ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;

        if (p.StartsWith(':') && int.TryParse(p[1..], out _))
        {
            return $"http://127.0.0.1{p}";
        }

        if (int.TryParse(p, out _))
        {
            return $"http://127.0.0.1:{p}";
        }

        if (!p.Contains("://"))
        {
            return $"http://{p}";
        }

        return p;
    }

    /// <summary>
    /// Tests network proxy connectivity by sending a lightweight probe to Google's generate_204 endpoint.
    /// </summary>
    public static async Task<GooglePhotosProxyTestResult> TestProxyAsync(string? proxyStr, CancellationToken cancellationToken = default)
    {
        string normalized = NormalizeProxy(proxyStr);

        try
        {
            SocketsHttpHandler handler;
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                if (!Uri.TryCreate(normalized, UriKind.Absolute, out var proxyUri))
                {
                    return new GooglePhotosProxyTestResult(false, 0, $"代理地址格式无效: {proxyStr}", normalized);
                }

                // 1. Probe local proxy TCP port first to provide precise diagnostics
                try
                {
                    using var tcpClient = new TcpClient();
                    using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    tcpCts.CancelAfter(TimeSpan.FromSeconds(3));
                    int port = proxyUri.Port > 0 ? proxyUri.Port : 80;
                    await tcpClient.ConnectAsync(proxyUri.Host, port, tcpCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new GooglePhotosProxyTestResult(
                        false,
                        0,
                        $"无法连接至本地代理服务 ({proxyUri.Host}:{proxyUri.Port}): {ex.Message}请检查本地代理客户端是否已启动。",
                        normalized);
                }

                string proxyForWeb = normalized;
                if (proxyForWeb.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
                {
                    proxyForWeb = "socks5://" + proxyForWeb["socks5h://".Length..];
                }

                handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy(proxyForWeb),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(5)
                };
            }
            else
            {
                // Direct connection test
                handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    ConnectTimeout = TimeSpan.FromSeconds(5)
                };
            }

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            var sw = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.google.com/generate_204");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
            {
                string desc = !string.IsNullOrWhiteSpace(normalized)
                    ? $"代理连接成功！延迟: {sw.ElapsedMilliseconds} ms (已成功连通 Google 服务器)"
                    : $"直连 Google 成功！延迟: {sw.ElapsedMilliseconds} ms (无需代理即可访问 Google)";
                return new GooglePhotosProxyTestResult(true, sw.ElapsedMilliseconds, desc, normalized);
            }
            else
            {
                return new GooglePhotosProxyTestResult(
                    false,
                    sw.ElapsedMilliseconds,
                    $"代理已连通，但 Google 响应状态异常: {(int)response.StatusCode} {response.ReasonPhrase}",
                    normalized);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GooglePhotosProxyTestResult(false, 0, "连接超时 (超过 8 秒未响应 Google 验证请求)", normalized);
        }
        catch (Exception ex)
        {
            string msg = !string.IsNullOrWhiteSpace(normalized)
                ? $"代理连通测试失败: {ex.Message}"
                : $"直连 Google 失败 (超时或无法访问): {ex.Message}。建议配置网络代理。";
            return new GooglePhotosProxyTestResult(false, 0, msg, normalized);
        }
    }

    /// <summary>
    /// Tests Google Photos credentials and network connectivity using gpmc.
    /// </summary>
    public static async Task<GooglePhotosAuthTestResult> TestAuthAsync(
        GooglePhotosSyncConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        string credential = config.EffectiveAuthCredential;
        if (string.IsNullOrWhiteSpace(credential))
        {
            return new GooglePhotosAuthTestResult(false, null,
                config.AuthMethod == GooglePhotosAuthMethod.OAuthCookie
                    ? "oauth_token Cookie 不能为空"
                    : "auth_data 凭据不能为空");
        }

        string bridgePath = LocateBridgeScript();
        string tempConfigFile = Path.Combine(Path.GetTempPath(), $"gpmc_test_{Guid.NewGuid():N}.json");

        try
        {
            var payload = new
            {
                action = "test",
                auth_data = credential,
                proxy = config.Proxy ?? "",
                timeout = config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 30,
                gpmc_path = config.GpmcPath ?? ""
            };

            await File.WriteAllTextAsync(tempConfigFile, JsonSerializer.Serialize(payload), Utf8NoBom, cancellationToken).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(config.PythonPath) ? "python" : config.PythonPath,
                Arguments = $"\"{bridgePath}\" \"{tempConfigFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONPATH"] = RuntimeDirectory;

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var jsonEl = ExtractJsonResult(stdout);
                if (jsonEl.HasValue)
                {
                    string status = jsonEl.Value.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                    if (status.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        string email = jsonEl.Value.TryGetProperty("email", out var el) ? el.GetString() ?? "" : "";
                        string? exchanged = jsonEl.Value.TryGetProperty("exchanged_auth_data", out var exEl) && exEl.ValueKind == JsonValueKind.String ? exEl.GetString() : null;
                        return new GooglePhotosAuthTestResult(true, email, null, exchanged);
                    }
                    string err = jsonEl.Value.TryGetProperty("error", out var errEl) ? errEl.GetString() ?? "" : "未知错误";
                    return new GooglePhotosAuthTestResult(false, null, err);
                }
            }

            string combinedError = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
            if (string.IsNullOrWhiteSpace(combinedError)) combinedError = $"进程退出代码: {process.ExitCode}";
            return new GooglePhotosAuthTestResult(false, null, combinedError);
        }
        catch (Exception ex)
        {
            return new GooglePhotosAuthTestResult(false, null, ex.Message);
        }
        finally
        {
            try { if (File.Exists(tempConfigFile)) File.Delete(tempConfigFile); } catch { }
        }
    }

    /// <summary>
    /// Resolves candidate target files and directory based on configuration and archive manifest.
    /// </summary>
    public static (List<string> Files, string? TargetDirectory, long TotalBytes) ResolveCandidateFiles(
        string? pcDestinationRoot,
        GooglePhotosSyncConfig config,
        Action<string>? onLog = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        List<string> targetAbsoluteFiles = [];
        string? targetDirectory = null;
        long totalBytes = 0;

        if (config.ExplicitTargetFiles != null && config.ExplicitTargetFiles.Count > 0)
        {
            foreach (string filePath in config.ExplicitTargetFiles)
            {
                if (File.Exists(filePath))
                {
                    targetAbsoluteFiles.Add(filePath);
                }
            }
            totalBytes = targetAbsoluteFiles.Sum(f => new FileInfo(f).Length);
            return (targetAbsoluteFiles, null, totalBytes);
        }

        if (config.ScopeMode == GooglePhotosScopeMode.CustomTarget)
        {
            if (string.IsNullOrWhiteSpace(config.CustomTargetPath))
            {
                throw new ArgumentException("自定义上传目标路径不能为空。");
            }
            if (Directory.Exists(config.CustomTargetPath))
            {
                targetDirectory = config.CustomTargetPath;
                try
                {
                    var files = Directory.GetFiles(targetDirectory, "*.*", SearchOption.AllDirectories);
                    totalBytes = files.Sum(f => new FileInfo(f).Length);
                    targetAbsoluteFiles.AddRange(files);
                }
                catch { }
            }
            else if (File.Exists(config.CustomTargetPath))
            {
                targetAbsoluteFiles.Add(config.CustomTargetPath);
                totalBytes = new FileInfo(config.CustomTargetPath).Length;
            }
            else
            {
                throw new FileNotFoundException($"找不到指定的上传路径: {config.CustomTargetPath}");
            }
            return (targetAbsoluteFiles, targetDirectory, totalBytes);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pcDestinationRoot);
        using var journal = TransferJournal.OpenReadOnly(pcDestinationRoot);

        IReadOnlyList<ManifestEntry> fullManifest = journal.ReadManifest();
        HashSet<string> manualSet = config.ScopeMode == GooglePhotosScopeMode.ManualSelection
            ? (config.ManualSelectedPaths != null ? new HashSet<string>(config.ManualSelectedPaths, StringComparer.OrdinalIgnoreCase) : journal.GetGooglePhotosManualSelections())
            : [];

        List<ManifestEntry> filtered = [];
        int nonMediaCount = 0;
        foreach (var item in fullManifest)
        {
            bool include = config.ScopeMode switch
            {
                GooglePhotosScopeMode.All => true,
                GooglePhotosScopeMode.DateRange => MatchesDate(item, config.DateRangeStart, config.DateRangeEnd),
                GooglePhotosScopeMode.Subfolder => !string.IsNullOrWhiteSpace(config.SelectedSubfolder) &&
                                                   (item.DestPath.StartsWith(config.SelectedSubfolder, StringComparison.OrdinalIgnoreCase) ||
                                                    item.DestPath.StartsWith(config.SelectedSubfolder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)),
                GooglePhotosScopeMode.ManualSelection => manualSet.Contains(item.DestPath),
                _ => true
            };

            if (include)
            {
                // Google Photos only accepts photos and videos; exclude non-media files (.AAE, .plist, etc.)
                if (MediaTypeClassifier.Classify(item.DestPath) == MediaType.Other)
                {
                    nonMediaCount++;
                    continue;
                }

                filtered.Add(item);
            }
        }

        if (nonMediaCount > 0)
        {
            onLog?.Invoke($"[Google Photos] 已自动排除 {nonMediaCount:N0} 个非图片/视频侧车文件 (如 .AAE 等)。");
        }

        totalBytes = filtered.Sum(f => f.SizeBytes);
        foreach (var item in filtered)
        {
            string fullPath = Path.Combine(pcDestinationRoot, item.DestPath);
            if (File.Exists(fullPath))
            {
                targetAbsoluteFiles.Add(fullPath);
            }
        }

        return (targetAbsoluteFiles, null, totalBytes);
    }

    /// <summary>
    /// Executes synchronization of selected archive or external files to Google Photos.
    /// </summary>
    public static async Task<GooglePhotosSyncResult> SyncAsync(
        string pcDestinationRoot,
        GooglePhotosSyncConfig config,
        IProgress<GooglePhotosProgressSnapshot>? progressTarget = null,
        Action<string>? onLog = null,
        Action<GooglePhotosProgressEvent>? onItemProcessed = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        string credential = config.EffectiveAuthCredential;
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException(
                config.AuthMethod == GooglePhotosAuthMethod.OAuthCookie
                    ? "请先配置 Google OAuth Cookie 凭据后方可上传。"
                    : "请先配置 Google auth_data 凭据后方可上传。");
        }

        string bridgePath = LocateBridgeScript();

        var (targetAbsoluteFiles, targetDirectory, totalBytes) = ResolveCandidateFiles(pcDestinationRoot, config, onLog);
        int totalFileCount = targetAbsoluteFiles.Count;

        if (config.ExplicitTargetFiles != null && config.ExplicitTargetFiles.Count > 0)
        {
            if (config.SessionTotalFiles.HasValue && config.SessionTotalFiles.Value > totalFileCount)
            {
                onLog?.Invoke($"[Google Photos] 正在继续上传当前方案，剩余 {totalFileCount:N0} 个目标文件待处理 (总方案共 {config.SessionTotalFiles.Value:N0} 项，约 {FormatSize(config.SessionTotalBytes ?? totalBytes)})");
            }
            else
            {
                onLog?.Invoke($"[Google Photos] 正在执行目标文件上传/重试，共 {totalFileCount:N0} 个目标文件 (共 {FormatSize(totalBytes)})");
            }
        }
        else if (config.ScopeMode == GooglePhotosScopeMode.CustomTarget)
        {
            if (targetDirectory != null)
            {
                onLog?.Invoke($"[Google Photos] 目标为外部目录: {targetDirectory} (共 {totalFileCount:N0} 个文件)");
            }
            else
            {
                onLog?.Invoke($"[Google Photos] 目标为单个外部文件: {config.CustomTargetPath}");
            }
        }
        else
        {
            if (totalFileCount == 0)
            {
                onLog?.Invoke("[Google Photos] 当前筛选范围内没有符合条件的媒体文件。");
                return new GooglePhotosSyncResult(0, 0, 0, 0, new Dictionary<string, string>());
            }
            onLog?.Invoke($"[Google Photos] 选定范围包含 {totalFileCount:N0} 个文件 (共 {FormatSize(totalBytes)})");
        }

        TransferJournal? journal = null;
        if (!string.IsNullOrWhiteSpace(pcDestinationRoot) && Directory.Exists(pcDestinationRoot))
        {
            try
            {
                journal = TransferJournal.Open(pcDestinationRoot);
            }
            catch { }
        }

        int effectiveTotalFiles = config.SessionTotalFiles ?? totalFileCount;
        long effectiveTotalBytes = config.SessionTotalBytes ?? totalBytes;

        string tempConfigFile = Path.Combine(Path.GetTempPath(), $"gpmc_upload_{Guid.NewGuid():N}.json");

        try
        {
            string? albumName = config.AlbumMode switch
            {
                GooglePhotosAlbumMode.AutoParentDir => "AUTO",
                GooglePhotosAlbumMode.CustomName => !string.IsNullOrWhiteSpace(config.CustomAlbumName) ? config.CustomAlbumName : null,
                _ => null
            };

            string? albumId = config.AlbumMode == GooglePhotosAlbumMode.AlbumId && !string.IsNullOrWhiteSpace(config.AlbumId)
                ? config.AlbumId
                : null;

            var payload = new
            {
                action = "upload",
                auth_data = credential,
                proxy = config.Proxy ?? "",
                timeout = config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 60,
                gpmc_path = config.GpmcPath ?? "",
                target_dir = targetDirectory,
                target_paths = targetAbsoluteFiles.Count > 0 ? targetAbsoluteFiles : null,
                album_name = albumName,
                album_id = albumId,
                threads = Math.Max(1, config.Threads),
                use_quota = !config.UnlimitedQuality,
                saver = config.StorageSaver,
                skip_existing_filenames = config.SkipExistingFilenames,
                auto_retries = Math.Max(0, config.AutoRetryAttempts),
                retry_delay = Math.Max(0.5, config.RetryDelaySeconds)
            };

            await File.WriteAllTextAsync(tempConfigFile, JsonSerializer.Serialize(payload), Utf8NoBom, cancellationToken).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(config.PythonPath) ? "python" : config.PythonPath,
                Arguments = $"\"{bridgePath}\" \"{tempConfigFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONPATH"] = RuntimeDirectory;

            onLog?.Invoke($"[Google Photos] 启动 gpmc 引擎 (并发线程: {payload.threads}, 自动重试: {payload.auto_retries}次, 无限原画: {config.UnlimitedQuality})...");

            using var process = new Process { StartInfo = psi };
            process.Start();

            int completedCount = 0;
            int skippedCount = 0;
            int failedCount = 0;
            long uploadedBytes = 0;
            var stopwatch = Stopwatch.StartNew();

            var readStderrTask = Task.Run(async () =>
            {
                using var reader = process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith('{') && line.Contains("\"phase\""))
                    {
                        try
                        {
                            var evt = JsonSerializer.Deserialize<GooglePhotosProgressEvent>(line, JsonOptions);
                            if (evt != null)
                            {
                                string phase = evt.Phase.ToLowerInvariant();
                                if (phase is "completed" or "complete")
                                {
                                    completedCount++;
                                    uploadedBytes += evt.BytesTotal;
                                    onLog?.Invoke($"[Google Photos] 已上传: {evt.Filename} ({FormatSize(evt.BytesTotal)})");
                                    onItemProcessed?.Invoke(evt);
                                }
                                else if (phase == "skipped")
                                {
                                    skippedCount++;
                                    onLog?.Invoke($"[Google Photos] 云端已存在，已跳过: {evt.Filename}");
                                    onItemProcessed?.Invoke(evt);
                                }
                                else if (phase == "retrying")
                                {
                                    string errSuffix = !string.IsNullOrWhiteSpace(evt.Error) ? $" ({evt.Error})" : "";
                                    int attempt = evt.RetryAttempt ?? 1;
                                    int maxRetries = evt.RetryMax ?? 3;
                                    double delay = evt.RetryDelay ?? 2.0;
                                    onLog?.Invoke($"[Google Photos] 上传遇到限流/波动，正在自动避让重试 (第 {attempt}/{maxRetries} 次，等待 {delay:F1}s): {evt.Filename}{errSuffix}");
                                }
                                else if (phase is "error" or "failed")
                                {
                                    failedCount++;
                                    string errSuffix = !string.IsNullOrWhiteSpace(evt.Error) ? $": {evt.Error}" : "";
                                    onLog?.Invoke($"[Google Photos] 上传失败: {evt.Filename}{errSuffix}");
                                    onItemProcessed?.Invoke(evt);
                                }

                                int totalUploaded = config.BaseUploadedFiles + completedCount;
                                int totalSkipped = config.BaseSkippedFiles + skippedCount;
                                int totalFailed = config.BaseFailedFiles + failedCount;
                                long totalUploadedBytes = config.BaseUploadedBytes + uploadedBytes;

                                double speed = stopwatch.Elapsed.TotalSeconds > 0
                                    ? uploadedBytes / stopwatch.Elapsed.TotalSeconds
                                    : 0;

                                int processedFiles = totalUploaded + totalSkipped + totalFailed;
                                double overallPercent = effectiveTotalFiles > 0
                                    ? Math.Clamp((double)processedFiles / effectiveTotalFiles * 100.0, 0, 100.0)
                                    : 0;

                                double currentFilePercent = evt.BytesTotal > 0
                                    ? Math.Clamp((double)evt.BytesCompleted / evt.BytesTotal * 100.0, 0, 100.0)
                                    : 0;

                                string friendlyPhase = phase switch
                                {
                                    "hashing" => "计算哈希",
                                    "checking" => "校验云端",
                                    "uploading" => "正在上传",
                                    "finalizing" => "提交确认",
                                    "complete" or "completed" => "已完成",
                                    "skipped" => "已跳过",
                                    "error" or "failed" => "上传失败",
                                    _ => evt.Phase
                                };

                                var snapshot = new GooglePhotosProgressSnapshot(
                                    TotalFiles: effectiveTotalFiles > 0 ? effectiveTotalFiles : processedFiles,
                                    UploadedFiles: totalUploaded,
                                    SkippedFiles: totalSkipped,
                                    FailedFiles: totalFailed,
                                    TotalBytes: effectiveTotalBytes,
                                    UploadedBytes: totalUploadedBytes,
                                    OverallPercent: overallPercent,
                                    SpeedBytesPerSecond: speed,
                                    CurrentFile: evt.Filename,
                                    CurrentPhase: friendlyPhase,
                                    CurrentFilePercent: currentFilePercent
                                );

                                progressTarget?.Report(snapshot);
                            }
                        }
                        catch
                        {
                            if (!line.Contains("InsecureRequestWarning") && !line.Contains("warnings.warn"))
                            {
                                onLog?.Invoke($"[gpmc] {line}");
                            }
                        }
                    }
                    else
                    {
                        if (!line.Contains("InsecureRequestWarning") && !line.Contains("warnings.warn"))
                        {
                            onLog?.Invoke($"[gpmc] {line}");
                        }
                    }
                }
            }, cancellationToken);

            var readStdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            using (cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { }
            }))
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            await readStderrTask.ConfigureAwait(false);
            string stdout = await readStdoutTask.ConfigureAwait(false);

            Dictionary<string, string> mediaKeys = new(StringComparer.OrdinalIgnoreCase);

            string? engineError = null;

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                try
                {
                    var jsonEl = ExtractJsonResult(stdout);
                    if (jsonEl.HasValue)
                    {
                        if (jsonEl.Value.TryGetProperty("error", out var errEl))
                        {
                            engineError = errEl.GetString();
                        }

                        if (jsonEl.Value.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in resultsEl.EnumerateObject())
                            {
                                string fullPath = prop.Name;
                                string mediaKey = prop.Value.GetString() ?? "";
                                mediaKeys[fullPath] = mediaKey;

                                if (journal != null && !string.IsNullOrWhiteSpace(pcDestinationRoot))
                                {
                                    try
                                    {
                                        string relPath = Path.GetRelativePath(pcDestinationRoot, fullPath).Replace('\\', '/');
                                        long size = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
                                        journal.RecordGooglePhotosSync(relPath, mediaKey, DateTimeOffset.UtcNow, albumName, size);
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"Failed to record Google Photos sync: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"[Google Photos] 解析返回结果时出错: {ex.Message}");
                }
            }

            if (process.ExitCode != 0 && cancellationToken.IsCancellationRequested)
            {
                onLog?.Invoke("[Google Photos] 上传操作已被用户取消。");
            }
            else if (process.ExitCode != 0)
            {
                string errMsg = !string.IsNullOrWhiteSpace(engineError)
                    ? engineError
                    : $"引擎退出，代码: {process.ExitCode}";
                onLog?.Invoke($"[Google Photos] 引擎异常退出: {errMsg}");
                throw new InvalidOperationException($"Google Photos 同步失败: {errMsg}");
            }
            else
            {
                onLog?.Invoke($"[Google Photos] 同步完成! 上传: {completedCount}, 跳过: {skippedCount}, 失败: {failedCount}");
            }

            int totalUploaded = config.BaseUploadedFiles + completedCount;
            int totalSkipped = config.BaseSkippedFiles + skippedCount;
            int totalFailed = config.BaseFailedFiles + failedCount;
            long totalUploadedBytes = config.BaseUploadedBytes + uploadedBytes;

            return new GooglePhotosSyncResult(
                totalUploaded,
                totalSkipped,
                totalFailed,
                totalUploadedBytes,
                mediaKeys
            );
        }
        finally
        {
            journal?.Dispose();
            try { if (File.Exists(tempConfigFile)) File.Delete(tempConfigFile); } catch { }
        }
    }

    private static bool MatchesDate(ManifestEntry entry, DateTimeOffset? dateFrom, DateTimeOffset? dateEnd)
    {
        DateTimeOffset? captureDate = ParseCaptureDate(entry);
        if (!captureDate.HasValue) return true;
        if (dateFrom.HasValue && captureDate.Value < dateFrom.Value) return false;
        if (dateEnd.HasValue && captureDate.Value > dateEnd.Value) return false;
        return true;
    }

    private static DateTimeOffset? ParseCaptureDate(ManifestEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ExifDateTimeOriginalIso) &&
            DateTimeOffset.TryParse(entry.ExifDateTimeOriginalIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var exifDt))
        {
            return exifDt;
        }

        if (!string.IsNullOrEmpty(entry.SourceMtimeIso) &&
            DateTimeOffset.TryParse(entry.SourceMtimeIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var mtimeDt))
        {
            return mtimeDt;
        }

        return null;
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{(double)bytes / (1024 * 1024 * 1024):F2} GB",
            >= 1024L * 1024 => $"{(double)bytes / (1024 * 1024):F1} MB",
            >= 1024L => $"{(double)bytes / 1024:F0} KB",
            _ => $"{bytes} B"
        };
    }

    private const string EmbeddedBridgeSource = """
    #!/usr/bin/env python3
    # -*- coding: utf-8 -*-
    import sys
    import os
    import json
    import threading
    import warnings

    warnings.filterwarnings("ignore")
    try:
        import urllib3
        urllib3.disable_warnings()
    except Exception:
        pass

    import mimetypes
    try:
        mimetypes.add_type("image/heic", ".heic")
        mimetypes.add_type("image/heif", ".heif")
        mimetypes.add_type("image/dng", ".dng")
        mimetypes.add_type("image/webp", ".webp")
        mimetypes.add_type("video/quicktime", ".mov")
        mimetypes.add_type("video/mp4", ".mp4")
    except Exception:
        pass

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")

    def output_result(payload):
        text = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        print(f"__GPMC_RESULT__:{text}", file=sys.stdout, flush=True)

    def output_error(error_msg, code=1):
        output_result({"status": "error", "error": str(error_msg)})
        try:
            print(f"[gpmc error] {error_msg}", file=sys.stderr, flush=True)
        except Exception:
            pass
        sys.exit(code)

    def normalize_proxy(proxy_str):
        p = (proxy_str or "").strip()
        if not p:
            return ""
        if "://" not in p:
            if p.isdigit():
                return f"http://127.0.0.1:{p}"
            return f"http://{p}"
        return p

    def clean_auth_token(token_str):
        s = (token_str or "").strip()
        if not s:
            return ""
        if "androidId=" in s:
            return s
        if "oauth_token=" in s:
            for part in s.split(";"):
                part = part.strip()
                if part.startswith("oauth_token="):
                    return part.split("=", 1)[1].strip()
        return s

    def main():
        if len(sys.argv) < 2:
            output_error("Usage: gpmc_bridge.py <config.json>", code=1)

        config_path = sys.argv[1]
        if not os.path.exists(config_path):
            output_error(f"Config file not found: {config_path}", code=1)

        try:
            with open(config_path, "r", encoding="utf-8-sig") as f:
                config = json.load(f)
        except Exception as e:
            output_error(f"Failed to read config json: {e}", code=1)

        script_dir = os.path.dirname(os.path.abspath(__file__))
        while script_dir in sys.path:
            sys.path.remove(script_dir)
        sys.path.insert(0, script_dir)

        custom_gpmc = config.get("gpmc_path")
        if custom_gpmc and os.path.isdir(custom_gpmc) and custom_gpmc not in sys.path:
            sys.path.append(custom_gpmc)

        try:
            from gpmc import Client
            from gpmc import utils
        except Exception as e:
            output_error(f"Failed to import gpmc module: {e}", code=2)

        action = config.get("action", "upload")
        raw_auth_data = config.get("auth_data", "").strip()
        auth_data = clean_auth_token(raw_auth_data)
        proxy = normalize_proxy(config.get("proxy", ""))
        timeout = int(config.get("timeout", 60))

        if not auth_data:
            env_auth = os.getenv("GP_AUTH_DATA", "").strip()
            if env_auth:
                auth_data = clean_auth_token(env_auth)
            else:
                output_error("auth_data is required and GP_AUTH_DATA is not set", code=3)

        if action == "test":
            try:
                client = Client(auth_data=auth_data, proxy=proxy, timeout=timeout, log_level="WARNING")
                _ = client.api.bearer_token
                try:
                    email = utils.parse_email(client.auth_data)
                except Exception:
                    email = "Google Account"
                exchanged = client.auth_data if "androidId=" in client.auth_data and "androidId=" not in auth_data else None
                output_result({
                    "status": "ok",
                    "email": email,
                    "exchanged_auth_data": exchanged
                })
                sys.exit(0)
            except Exception as e:
                output_error(str(e), code=1)
        elif action == "upload":
            progress_lock = threading.Lock()
            def emit_progress(event):
                try:
                    with progress_lock:
                        print(json.dumps(event, ensure_ascii=False, separators=(",", ":")), file=sys.stderr, flush=True)
                except Exception:
                    pass

            target = config.get("target_dir")
            if not target:
                raw_paths = config.get("target_paths") or []
                valid_media = []
                valid_exts = {
                    ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".dng", ".gif",
                    ".tif", ".tiff", ".bmp", ".raw", ".mov", ".mp4", ".m4v", ".avi",
                    ".mkv", ".3gp", ".3g2", ".hevc"
                }
                for p in raw_paths:
                    ext = os.path.splitext(p)[1].lower()
                    m = mimetypes.guess_type(p)[0]
                    if (m and (m.startswith("image/") or m.startswith("video/"))) or ext in valid_exts:
                        valid_media.append(p)
                target = valid_media

            if not target:
                output_error("No target media files found to upload", code=4)

            album_name = config.get("album_name")
            album_id = config.get("album_id")
            threads = max(1, int(config.get("threads", 1)))
            use_quota = bool(config.get("use_quota", False))
            saver = bool(config.get("saver", False))
            skip_existing = bool(config.get("skip_existing_filenames", False))
            auto_retries = int(config.get("auto_retries", 3))
            retry_delay = float(config.get("retry_delay", 2.0))

            try:
                client = Client(auth_data=auth_data, proxy=proxy, timeout=timeout, log_level="WARNING")
                results = client.upload(
                    target=target,
                    album_name=album_name,
                    album_id=album_id,
                    use_quota=use_quota,
                    saver=saver,
                    recursive=True,
                    threads=threads,
                    skip_existing_filenames=skip_existing,
                    progress_callback=emit_progress,
                    auto_retries=auto_retries,
                    retry_delay=retry_delay,
                )
                output_result({"status": "ok", "results": results or {}})
                sys.exit(0)
            except Exception as e:
                output_error(str(e), code=1)
        else:
            output_error(f"Unknown action: {action}", code=5)

    if __name__ == "__main__":
        main()
    """;
}
