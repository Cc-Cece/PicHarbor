using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Scope;
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

    /// <summary>
    /// Directory of the private CPython runtime.
    /// Path: %LocalAppData%\PicHarbor\runtime\python\
    /// </summary>
    public static string PythonDirectory => Path.Combine(RuntimeDirectory, "python");

    /// <summary>
    /// Extracts the embedded CPython runtime when its hash changes and returns python.exe.
    /// The interpreter is isolated by its ._pth file and is never resolved from PATH.
    /// </summary>
    public static string LocatePythonExecutable()
    {
        byte[] bundleBytes = ReadEmbeddedBytes("python_runtime.zip")
            ?? throw new FileNotFoundException("未找到内置 Python 运行时。请重新安装 PicHarbor。", "python_runtime.zip");

        string versionMarker = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bundleBytes));
        string pythonDir = PythonDirectory;
        string pythonExe = Path.Combine(pythonDir, "python.exe");
        string markerFile = Path.Combine(pythonDir, "python_runtime_version.txt");
        if (File.Exists(pythonExe)
            && File.Exists(markerFile)
            && string.Equals(File.ReadAllText(markerFile).Trim(), versionMarker, StringComparison.OrdinalIgnoreCase))
        {
            return pythonExe;
        }

        string staging = pythonDir + ".new";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            ExtractZip(bundleBytes, staging);
            File.WriteAllText(Path.Combine(staging, "python_runtime_version.txt"), versionMarker, Utf8NoBom);

            if (Directory.Exists(pythonDir))
            {
                string backup = pythonDir + ".old";
                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                Directory.Move(pythonDir, backup);
                try
                {
                    Directory.Move(staging, pythonDir);
                    Directory.Delete(backup, recursive: true);
                }
                catch
                {
                    if (!Directory.Exists(pythonDir) && Directory.Exists(backup))
                    {
                        Directory.Move(backup, pythonDir);
                    }
                    throw;
                }
            }
            else
            {
                Directory.Move(staging, pythonDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("内置 Python 正在使用，请结束正在进行的 Google 相册任务后重试。", ex);
        }

        if (!File.Exists(pythonExe))
        {
            throw new FileNotFoundException("内置 Python 运行时解压失败。", pythonExe);
        }

        return pythonExe;
    }

    private static ProcessStartInfo CreateGpmcProcess(string bridgePath, string tempConfigFile)
    {
        var start = new ProcessStartInfo
        {
            FileName = LocatePythonExecutable(),
            Arguments = $"\"{bridgePath}\" \"{tempConfigFile}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        start.EnvironmentVariables["PYTHONUTF8"] = "1";
        start.EnvironmentVariables["PYTHONPATH"] = RuntimeDirectory;
        return start;
    }

    private static byte[]? ReadEmbeddedBytes(string logicalName)
    {
        var assembly = typeof(GooglePhotosSyncEngine).Assembly;
        Stream? resourceStream = assembly.GetManifestResourceStream(logicalName);
        if (resourceStream == null)
        {
            string? resName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(logicalName, StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                resourceStream = assembly.GetManifestResourceStream(resName);
            }
        }

        if (resourceStream == null) return null;
        using (resourceStream)
        using (var ms = new MemoryStream())
        {
            resourceStream.CopyTo(ms);
            return ms.ToArray();
        }
    }

    private static void ExtractZip(byte[] zipBytes, string destination)
    {
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative)) continue;
            string target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("内置 Python 运行时包含非法路径。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string Redact(string text,GooglePhotosSyncConfig config)
    {
        foreach(string secret in new[]{config.AuthData,config.OAuthTokenCookie,config.EffectiveAuthCredential})
            if(!string.IsNullOrEmpty(secret)) text=text.Replace(secret,"[redacted]",StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(text,@"(?i)(Token|oauth_token|password|auth_data)\s*[=:]\s*[^\s&,}]+","$1=[redacted]");
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
    /// Builds Chromium / WebView2 browser arguments for network proxy (e.g. --proxy-server=http://127.0.0.1:7890).
    /// Returns string.Empty if proxy is null, empty, or whitespace.
    /// </summary>
    public static string BuildWebView2ProxyArguments(string? proxyStr)
    {
        string normalized = NormalizeProxy(proxyStr);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : $"--proxy-server={normalized}";
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
        string tempConfigFile = SecureTemporaryFile.CreatePath("gpmc_test");

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

            var psi = CreateGpmcProcess(bridgePath, tempConfigFile);

            using var process = new Process { StartInfo = psi };
            process.Start();
            using var stopOnCancellation=cancellationToken.Register(() =>
            {
                try { if(!process.HasExited) process.Kill(entireProcessTree:true); }
                catch(InvalidOperationException) { }
                catch(System.ComponentModel.Win32Exception) { }
            });

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
            return new GooglePhotosAuthTestResult(false, null, Redact(combinedError,config));
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
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default,
        IProgress<GooglePhotosPreparationProgress>? preparationProgress = null)
    {
        var plan = PrepareUpload(pcDestinationRoot, config, onLog, cancellationToken, preparationProgress);
        return (plan.Files.Select(f => f.Path).ToList(), plan.TargetDirectory, plan.TotalBytes);
    }

    /// <summary>Filters and verifies each candidate in one pass, retaining the exact upload selection.</summary>
    public static GooglePhotosUploadPlan PrepareUpload(
        string? pcDestinationRoot,
        GooglePhotosSyncConfig config,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default,
        IProgress<GooglePhotosPreparationProgress>? preparationProgress = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        List<GooglePhotosPreparedFile> preparedFiles = [];
        string? targetDirectory = null;
        void AddFile(string path, string? sha256 = null)
        {
            var file = new FileInfo(path);
            preparedFiles.Add(new(Path.GetFullPath(path), file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), sha256));
        }
        var progressClock = Stopwatch.StartNew();
        int scannedFiles = 0;
        void ReportPreparation(int processed, int? total, string file = "", bool force = false)
        {
            if (preparationProgress is null || (!force && progressClock.ElapsedMilliseconds < 150)) return;
            preparationProgress.Report(new(processed, total, Path.GetFileName(file), Math.Max(scannedFiles, processed)));
            progressClock.Restart();
        }
        ReportPreparation(0, null, force: true);

        if (config.ExplicitTargetFiles != null && config.ExplicitTargetFiles.Count > 0)
        {
            int processed = 0;
            ReportPreparation(0, config.ExplicitTargetFiles.Count, force: true);
            foreach (string filePath in config.ExplicitTargetFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(filePath))
                {
                    AddFile(filePath);
                }
                ReportPreparation(++processed, config.ExplicitTargetFiles.Count, filePath);
            }
            ReportPreparation(config.ExplicitTargetFiles.Count, config.ExplicitTargetFiles.Count, force: true);
            return new GooglePhotosUploadPlan(preparedFiles, null);
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
                foreach (string file in Directory.EnumerateFiles(targetDirectory, "*.*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddFile(file);
                    ReportPreparation(preparedFiles.Count, null, file);
                }
            }
            else if (File.Exists(config.CustomTargetPath))
            {
                AddFile(config.CustomTargetPath);
            }
            else
            {
                throw new FileNotFoundException($"找不到指定的上传路径: {config.CustomTargetPath}");
            }
            ReportPreparation(preparedFiles.Count, preparedFiles.Count, force: true);
            return new GooglePhotosUploadPlan(preparedFiles, targetDirectory);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pcDestinationRoot);
        using var journal = TransferJournal.OpenReadOnly(pcDestinationRoot);

        IReadOnlyList<ManifestEntry> fullManifest = PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(pcDestinationRoot);
        bool baseIsManual = config.ScopeMode == GooglePhotosScopeMode.ManualSelection;
        HashSet<string> manualSet = baseIsManual
            ? (config.ManualSelectedPaths != null ? new HashSet<string>(config.ManualSelectedPaths, StringComparer.OrdinalIgnoreCase) : journal.GetGooglePhotosManualSelections())
            : [];

        // Resolve combined filters: new Filters property wins; otherwise derive from legacy exclusive fields.
        HashSet<string>? subfolderFilter;
        DateTimeOffset? dateStart;
        DateTimeOffset? dateEnd;
        UndatedFilePolicy undatedPolicy;
        if (config.Filters is { } filters)
        {
            subfolderFilter = filters.HasSubfolderFilter ? filters.SelectedSubfolders : null;
            dateStart = filters.DateFrom;
            dateEnd = filters.DateTo?.AddDays(1).AddTicks(-1);
            undatedPolicy = filters.UndatedPolicy;
        }
        else
        {
#pragma warning disable CS0618 // legacy single-select compatibility
            subfolderFilter = config.ScopeMode == GooglePhotosScopeMode.Subfolder
                ? (string.IsNullOrWhiteSpace(config.SelectedSubfolder)
                    ? config.SelectedSubfolders
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.SelectedSubfolder })
                : null;
#pragma warning restore CS0618
            if (config.ScopeMode == GooglePhotosScopeMode.DateRange)
            {
                dateStart = config.DateRangeStart;
                dateEnd = config.DateRangeEnd;
            }
            else
            {
                dateStart = null;
                dateEnd = null;
            }

            undatedPolicy = UndatedFilePolicy.Include;
        }

        bool dateFilterActive = dateStart is not null || dateEnd is not null;
        int undatedCount = 0;

        int checkedFiles = 0;
        int nonMediaCount = 0;
        foreach (var item in fullManifest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedFiles++;
            ReportPreparation(checkedFiles, null, item.DestPath);
            if (baseIsManual && !manualSet.Contains(item.SelectionKey) && !manualSet.Contains(item.DestPath))
            {
                continue;
            }

            if (subfolderFilter is not null && !ScopeFilterSet.MatchSubfolder(item.DestPath, subfolderFilter))
            {
                continue;
            }

            if (dateFilterActive)
            {
                DateTimeOffset? captureDate = ParseCaptureDate(item);
                if (!captureDate.HasValue)
                {
                    if (undatedPolicy == UndatedFilePolicy.Skip)
                    {
                        undatedCount++;
                        continue;
                    }
                }
                else if ((dateStart.HasValue && captureDate.Value < dateStart.Value) ||
                         (dateEnd.HasValue && captureDate.Value > dateEnd.Value))
                {
                    continue;
                }
            }

            if (MediaTypeClassifier.Classify(item.DestPath) == MediaType.Other)
            {
                nonMediaCount++;
                continue;
            }

            // Verify as soon as a candidate passes the filters; do not build a second batch.
            ReportPreparation(checkedFiles, null, item.DestPath);
            bool verified = false;
            try
            {
                string path = PicHarbor.Core.Storage.ArchivePathResolver.Resolve(item.ArchiveRoot ?? pcDestinationRoot, item.DestPath);
                var before = new FileInfo(path);
                long size = before.Length;
                DateTime modified = before.LastWriteTimeUtc;
                if (PicHarbor.Core.Storage.MediaFileGuard.TryVerify(item, pcDestinationRoot, out string fullPath, cancellationToken))
                {
                    before.Refresh();
                    if (before.Exists && before.Length == size && before.LastWriteTimeUtc == modified)
                    {
                        preparedFiles.Add(new(Path.GetFullPath(fullPath), size,
                            new DateTimeOffset(modified).ToUnixTimeMilliseconds(), item.MediaId)
                        { SelectionKey = item.SelectionKey });
                        verified = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!verified)
            {
                onLog?.Invoke($"[Google Photos] Selected photo unavailable or changed: {item.DestPath}");
            }
            checkedFiles++;
            ReportPreparation(checkedFiles, null, item.DestPath);
        }

        if (nonMediaCount > 0)
        {
            onLog?.Invoke($"[Google Photos] 已自动排除 {nonMediaCount:N0} 个非图片/视频侧车文件 (如 .AAE 等)。");
        }

        if (dateFilterActive && undatedCount > 0)
        {
            string policyText = undatedPolicy == UndatedFilePolicy.Include ? "已包含" : "已跳过";
            onLog?.Invoke($"[Google Photos] {undatedCount:N0} 项无拍摄日期（无 EXIF 且无修改时间），按策略{policyText}。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReportPreparation(checkedFiles, checkedFiles, force: true);

        return new GooglePhotosUploadPlan(preparedFiles, null);
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
        CancellationToken cancellationToken = default,
        IProgress<GooglePhotosPreparationProgress>? preparationProgress = null,
        GooglePhotosUploadPlan? preparedPlan = null)
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

        cancellationToken.ThrowIfCancellationRequested();
        var plan = preparedPlan ?? PrepareUpload(pcDestinationRoot, config, onLog, cancellationToken, preparationProgress);
        var targetAbsoluteFiles = plan.Files.Select(f => f.Path).ToList();
        long totalBytes = plan.TotalBytes;
        string? targetDirectory = plan.TargetDirectory;
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

        string tempConfigFile = SecureTemporaryFile.CreatePath("gpmc_upload");

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
                // Always send the fixed list, including external directories. Never rescan in Python.
                target_dir = (string?)null,
                target_paths = targetAbsoluteFiles,
                prepared_files = plan.Files.Select(f => new
                {
                    path = f.Path,
                    size = f.Size,
                    modified_ms = f.ModifiedUnixMilliseconds,
                    sha256 = f.Sha256
                }),
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

            var psi = CreateGpmcProcess(bridgePath, tempConfigFile);

            onLog?.Invoke($"[Google Photos] 启动 gpmc 引擎 (并发线程: {payload.threads}, 自动重试: {payload.auto_retries}次, 无限原画: {config.UnlimitedQuality})...");

            using var process = new Process { StartInfo = psi };
            process.Start();
            using var stopOnCancellation=cancellationToken.Register(() =>
            {
                try { if(!process.HasExited) process.Kill(entireProcessTree:true); }
                catch(InvalidOperationException) { }
                catch(System.ComponentModel.Win32Exception) { }
            });

            int completedCount = 0;
            int skippedCount = 0;
            int failedCount = 0;
            long uploadedBytes = 0;
            var stopwatch = Stopwatch.StartNew();

            void ReportStage(string stage, int processed, int total)
            {
                int done = config.BaseUploadedFiles + completedCount + config.BaseSkippedFiles + skippedCount + config.BaseFailedFiles + failedCount;
                progressTarget?.Report(new GooglePhotosProgressSnapshot(
                    effectiveTotalFiles, config.BaseUploadedFiles + completedCount,
                    config.BaseSkippedFiles + skippedCount, config.BaseFailedFiles + failedCount,
                    effectiveTotalBytes, config.BaseUploadedBytes + uploadedBytes,
                    effectiveTotalFiles > 0 ? Math.Clamp(100d * done / effectiveTotalFiles, 0, 100) : 0,
                    0, "", "", 0)
                { Stage = stage, StageProcessed = processed, StageTotal = total });
            }

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
                                )
                                {
                                    Stage = phase == "archiving" ? "archiving"
                                        : effectiveTotalFiles > 0 && processedFiles >= effectiveTotalFiles ? "finishing" : null,
                                    StageProcessed = evt.PhaseProcessed,
                                    StageTotal = evt.PhaseTotal
                                };

                                progressTarget?.Report(snapshot);
                            }
                        }
                        catch
                        {
                            if (!line.Contains("InsecureRequestWarning") && !line.Contains("warnings.warn"))
                            {
                                onLog?.Invoke($"[gpmc] {Redact(line,config)}");
                            }
                        }
                    }
                    else
                    {
                        if (!line.Contains("InsecureRequestWarning") && !line.Contains("warnings.warn"))
                        {
                            onLog?.Invoke($"[gpmc] {Redact(line,config)}");
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
            if (journal != null && mediaKeys.Count > 0)
            {
                ReportStage("preparing_records", 0, 0);
                var records = PrepareSyncRecords(pcDestinationRoot, config.ScopeMode, plan, mediaKeys, albumName,
                    () => BuildSyncSelectionMap(pcDestinationRoot, ArchiveRepository.ReadLibraryManifest(pcDestinationRoot),
                        (processed, total) => ReportStage("preparing_records", processed, total), cancellationToken),
                    cancellationToken);
                ReportStage("saving_records", 0, records.Count);
                journal.RecordGooglePhotosSyncBatch(records,
                    saved => ReportStage("saving_records", saved, records.Count), cancellationToken);
            }

            onLog?.Invoke($"[Google Photos] 同步完成! 上传: {completedCount}, 跳过: {skippedCount}, 失败: {failedCount}");

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

    internal static IReadOnlyList<GooglePhotosSyncRecord> PrepareSyncRecords(string root, GooglePhotosScopeMode scope,
        GooglePhotosUploadPlan plan, IReadOnlyDictionary<string, string> mediaKeys, string? albumName,
        Func<Dictionary<string, string>> readLegacySelections, CancellationToken cancellationToken = default)
    {
        var files = plan.Files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? legacySelections = null;
        var records = new List<GooglePhotosSyncRecord>();
        var uploadedAt = DateTimeOffset.UtcNow;
        foreach (var (path, mediaKey) in mediaKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only persist results belonging to this prepared upload, including resumed/retried subsets.
            if (!files.TryGetValue(path, out var file)) continue;
            string? selection = scope == GooglePhotosScopeMode.CustomTarget
                ? Path.GetRelativePath(root, path).Replace('\\', '/')
                : file.SelectionKey ?? (legacySelections ??= readLegacySelections()).GetValueOrDefault(path);
            if (selection is not null)
                records.Add(new(selection, mediaKey, uploadedAt, albumName, file.Size));
        }
        return records;
    }

    internal static Dictionary<string, string> BuildSyncSelectionMap(string root, IEnumerable<ManifestEntry> manifest,
        Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int total = manifest.TryGetNonEnumeratedCount(out int count) ? count : 0;
        int processed = 0;
        var clock = Stopwatch.StartNew();
        progress?.Invoke(0, total);
        foreach (var entry in manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string fullPath = PicHarbor.Core.Storage.ArchivePathResolver.Resolve(entry.ArchiveRoot ?? root, entry.DestPath);
                result.TryAdd(fullPath, entry.SelectionKey);
            }
            catch (IOException) { }
            processed++;
            if (processed == total || clock.ElapsedMilliseconds >= 150)
            {
                progress?.Invoke(processed, total);
                clock.Restart();
            }
        }
        if (total == 0) progress?.Invoke(processed, processed);
        return result;
    }

    private const string EmbeddedBridgeSource = """
    #!/usr/bin/env python3
    # -*- coding: utf-8 -*-
    import sys
    import os
    import json
    import threading
    import warnings
    from pathlib import Path

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
                client.prepared_files = {
                    os.path.normcase(os.path.abspath(item["path"])): item
                    for item in config.get("prepared_files", [])
                } if "prepared_files" in config else None
                if client.prepared_files is not None:
                    target = {Path(p): None for p in target}
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
