using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;

namespace PicHarbor.Core.Link;

/// <summary>Settings for a computer that is waiting for a phone.</summary>
public sealed class LinkHostOptions
{
    /// <summary>Archive directory that receives the photos.</summary>
    public required string ArchiveRoot { get; init; }

    /// <summary>Folder layout used for newly received files.</summary>
    public OrganizeScheme Scheme { get; init; } = OrganizeScheme.Month;

    /// <summary>Place each phone in its own folder inside the archive.</summary>
    public bool GroupByDevice { get; init; }

    /// <summary>Name shown to the phone. The computer name is used when this is empty.</summary>
    public string? ComputerName { get; init; }

    /// <summary>TCP port. Zero asks the system for a free port.</summary>
    public int Port { get; init; }

    /// <summary>Send a UDP announcement on the local network.</summary>
    public bool Beacon { get; init; } = true;

    /// <summary>Use this pairing code instead of a random one. Intended for tests.</summary>
    public string? FixedCode { get; init; }

    /// <summary>Keep the certificate only in memory. Intended for tests.</summary>
    public bool EphemeralCertificate { get; init; }
}

/// <summary>Listens for a paired phone and copies photos into the archive.</summary>
public sealed class LinkHost : IAsyncDisposable
{
    private const int MaxManifest = 32 * 1024 * 1024;
    private const int MaxChunk = 8 * 1024 * 1024;
    private readonly LinkHostOptions options;
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, PairedDevice> devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> sessions = new(StringComparer.Ordinal);
    private readonly string stagingRoot;
    private int attempts;
    private string code;
    private DateTimeOffset codeExpires;

    private LinkHost(LinkHostOptions options, TcpListener listener, X509Certificate2 certificate, IEnumerable<PairedDevice> known)
    {
        this.options = options;
        this.listener = listener;
        this.certificate = certificate;
        foreach (var device in known)
        {
            devices[device.Id] = device;
        }

        stagingRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicHarbor", "link-staging");
        Directory.CreateDirectory(stagingRoot);
        code = string.IsNullOrWhiteSpace(options.FixedCode) ? NewCode() : options.FixedCode.Trim();
        codeExpires = DateTimeOffset.UtcNow.AddMinutes(2);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Fingerprint = LinkBytes.Hex(SHA256.HashData(certificate.RawData));
        Addresses = ListAddresses();
        QrText = BuildQr(Addresses.FirstOrDefault() ?? "127.0.0.1");
    }

    /// <summary>TCP port the phone connects to.</summary>
    public int Port { get; }

    /// <summary>SHA-256 of the computer certificate, as lowercase hex.</summary>
    public string Fingerprint { get; }

    /// <summary>Current six-digit pairing code.</summary>
    public string Code => code;

    /// <summary>Private addresses embedded in the QR code.</summary>
    public IReadOnlyList<string> Addresses { get; }

    /// <summary>Text encoded by the QR code.</summary>
    public string QrText { get; private set; }

    /// <summary>Raised when the code, QR text, or a status line changes.</summary>
    public event Action<string>? Status;

    /// <summary>Starts listening.</summary>
    public static LinkHost Start(LinkHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ArchiveRoot);
        Directory.CreateDirectory(options.ArchiveRoot);
        var certificate = LoadCertificate(options.EphemeralCertificate);
        var known = options.EphemeralCertificate ? [] : LoadDevices();
        var listener = new TcpListener(IPAddress.Any, options.Port);
        listener.Start();
        var host = new LinkHost(options, listener, certificate, known);
        _ = host.AcceptLoop();
        if (options.Beacon)
        {
            _ = host.BeaconLoop();
        }

        host.Report("正在等待手机连接。");
        return host;
    }

    /// <summary>Stops listening and closes the port.</summary>
    public ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        certificate.Dispose();
        lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoop()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            _ = Task.Run(() => Handle(client), CancellationToken.None);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var raw = client.GetStream();
                await using var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                }, lifetime.Token).ConfigureAwait(false);
                var request = await LinkRequest.ReadAsync(ssl, MaxManifest, lifetime.Token).ConfigureAwait(false);
                await Dispatch(ssl, request).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(ex.Message);
            }
        }
    }

    private async Task Dispatch(Stream stream, LinkRequest request)
    {
        RotateCode();
        switch (request.Method + " " + request.Path)
        {
            case "GET /v1/info":
                await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(new { v = LinkProtocol.Version, name = ComputerName(), fp = Fingerprint, port = Port }), lifetime.Token).ConfigureAwait(false);
                return;
            case "POST /v1/pair":
                await Pair(stream, request).ConfigureAwait(false);
                return;
            case "POST /v1/session":
                await Session(stream, request).ConfigureAwait(false);
                return;
            case "POST /v1/manifest":
                await Manifest(stream, request).ConfigureAwait(false);
                return;
        }

        if (request.Method == "PUT" && request.Path.StartsWith("/v1/blobs/", StringComparison.Ordinal))
        {
            await Blob(stream, request).ConfigureAwait(false);
            return;
        }

        await LinkResponses.WriteAsync(stream, 404, "Not Found", """{"error":"not_found"}""", lifetime.Token).ConfigureAwait(false);
    }

    private async Task Pair(Stream stream, LinkRequest request)
    {
        var body = JsonSerializer.Deserialize<PairBody>(request.Body, LinkJson.Options);
        if (body is null || !LinkProtocol.IsSafeId(body.DeviceId) || string.IsNullOrWhiteSpace(body.Nonce) || string.IsNullOrWhiteSpace(body.PublicKey))
        {
            await LinkResponses.WriteAsync(stream, 400, "Bad Request", """{"error":"bad_pair"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        string expected = LinkProtocol.PairMac(code, body.DeviceId, body.Nonce, Fingerprint);
        if (!LinkBytes.FixedTimeEquals(expected, body.Mac ?? ""))
        {
            if (Interlocked.Increment(ref attempts) >= 5)
            {
                code = NewCode();
                attempts = 0;
                codeExpires = DateTimeOffset.UtcNow.AddMinutes(2);
                QrText = BuildQr(Addresses.FirstOrDefault() ?? "127.0.0.1");
                Report("配对码已更换。");
            }

            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"bad_code"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        var device = new PairedDevice(body.DeviceId, body.DeviceName ?? "手机", body.Platform ?? "phone", body.PublicKey);
        devices[body.DeviceId] = device;
        SaveDevices();
        string token = Issue(body.DeviceId);
        Report($"已连接 {device.Name}。");
        await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(new { token, name = ComputerName(), fp = Fingerprint }), lifetime.Token).ConfigureAwait(false);
    }

    private async Task Session(Stream stream, LinkRequest request)
    {
        var body = JsonSerializer.Deserialize<SessionBody>(request.Body, LinkJson.Options);
        if (body is null || !devices.TryGetValue(body.DeviceId ?? "", out var device) || string.IsNullOrWhiteSpace(body.Nonce) || string.IsNullOrWhiteSpace(body.Signature))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"unknown_device"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        if (!Verify(device.PublicKey, body.Nonce, body.Signature))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"bad_signature"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        string token = Issue(device.Id);
        Report($"已重新连接 {device.Name}。");
        await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(new { token, name = ComputerName() }), lifetime.Token).ConfigureAwait(false);
    }

    private async Task Manifest(Stream stream, LinkRequest request)
    {
        if (!TrySession(request, out var device))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"sign_in"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        var body = JsonSerializer.Deserialize<ManifestBody>(request.Body, LinkJson.Options);
        var need = new List<string>();
        var have = new List<string>();
        using var journal = TransferJournal.Open(options.ArchiveRoot);
        journal.BindDevice(device.Id);
        journal.RegisterOrUpdateDevice(device.Id, device.Name, body?.Model, null, device.Platform, DateTimeOffset.UtcNow);
        foreach (var item in body?.Items ?? [])
        {
            if (!LinkProtocol.IsSafeId(item.Id) || string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            string source = SourcePath(device.Id, item);
            var remote = new RemoteFile(source, item.Size, FromUnix(item.Created));
            journal.EnsurePending(remote);
            var state = journal.GetState(source, item.Size);
            string? dest = journal.GetDestRelativePath(source, item.Size);
            bool stored = state == FileState.Done && dest is not null && File.Exists(Path.Combine(options.ArchiveRoot, dest));
            if (stored && !string.IsNullOrWhiteSpace(item.Sha256))
            {
                stored = string.Equals(Sha256(Path.Combine(options.ArchiveRoot, dest!)), item.Sha256, StringComparison.OrdinalIgnoreCase);
            }

            (stored ? have : need).Add(item.Id);
        }

        await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(new { need, have }), lifetime.Token).ConfigureAwait(false);
    }

    private async Task Blob(Stream stream, LinkRequest request)
    {
        if (!TrySession(request, out var device))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"sign_in"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        string id = request.Path["/v1/blobs/".Length..];
        if (!LinkProtocol.IsSafeId(id))
        {
            await LinkResponses.WriteAsync(stream, 400, "Bad Request", """{"error":"bad_id"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        if (request.Body.Length > MaxChunk)
        {
            await LinkResponses.WriteAsync(stream, 413, "Payload Too Large", """{"error":"chunk"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        string name = request.Headers.GetValueOrDefault("X-File-Name") ?? id;
        name = Path.GetFileName(Uri.UnescapeDataString(name));
        long size = long.Parse(request.Headers.GetValueOrDefault("X-Size") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        long offset = long.Parse(request.Headers.GetValueOrDefault("X-Offset") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        long created = long.Parse(request.Headers.GetValueOrDefault("X-Created") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        string sha = request.Headers.GetValueOrDefault("X-Sha256") ?? "";
        string staging = Path.Combine(stagingRoot, device.Id + "-" + id + ".part");
        Directory.CreateDirectory(stagingRoot);
        var info = new FileInfo(staging);
        long have = info.Exists ? info.Length : 0;
        if (offset != have)
        {
            await LinkResponses.WriteAsync(stream, 409, "Conflict", LinkJson.Text(new { received = have }), lifetime.Token).ConfigureAwait(false);
            return;
        }

        await using (var file = new FileStream(staging, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await file.WriteAsync(request.Body, lifetime.Token).ConfigureAwait(false);
        }

        long received = new FileInfo(staging).Length;
        bool complete = size > 0 && received == size;
        if (complete)
        {
            if (!string.IsNullOrWhiteSpace(sha) && !string.Equals(Sha256(staging), sha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(staging);
                await LinkResponses.WriteAsync(stream, 400, "Bad Request", """{"error":"hash"}""", lifetime.Token).ConfigureAwait(false);
                return;
            }

            try
            {
                Import(device, id, name, size, created, sha, staging);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Report($"保存 {name} 失败: {ex.Message}");
                await LinkResponses.WriteAsync(stream, 500, "Error", LinkJson.Text(new { error = "import", received }), lifetime.Token).ConfigureAwait(false);
                return;
            }

            Report($"已收到 {name}");
        }

        await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(new { received, complete }), lifetime.Token).ConfigureAwait(false);
    }

    private void Import(PairedDevice device, string id, string name, long size, long created, string sha, string staging)
    {
        using var journal = TransferJournal.Open(options.ArchiveRoot);
        journal.BindDevice(device.Id);
        journal.RegisterOrUpdateDevice(device.Id, device.Name, null, null, device.Platform, DateTimeOffset.UtcNow);
        string source = $"wireless/{device.Id}/{id}/{name}";
        var when = FromUnix(created);
        var remote = new RemoteFile(source, size, when);
        journal.EnsurePending(remote);
        journal.MarkInProgress(source, size, DateTimeOffset.UtcNow);
        string? existing = journal.GetDestRelativePath(source, size);
        var metadata = new ExifMetadataExtractor().Extract(staging);
        if (metadata.DateTimeOriginal is null && when is not null)
        {
            metadata = metadata with { DateTimeOriginal = when.Value.LocalDateTime };
        }

        OrganizeScheme? recorded = journal.GetOrganizeScheme();
        OrganizeScheme scheme = recorded ?? options.Scheme;
        if (recorded is null)
        {
            journal.SetOrganizeScheme(scheme);
        }

        string relative;
        if (existing is not null)
        {
            relative = existing;
        }
        else
        {
            var used = journal.GetUsedDestPaths();
            string proposed = new DateFolderOrganizer().GetRelativeDestination(
                remote, metadata, scheme, options.GroupByDevice ? device.Name : null);
            relative = CollisionSuffix.Resolve(proposed, candidate =>
                !used.Contains(candidate) && !File.Exists(Path.Combine(options.ArchiveRoot, candidate)));
        }

        string full = Path.Combine(options.ArchiveRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        MoveIntoPlace(staging, full);
        journal.MarkDone(source, size, relative, metadata, DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(sha) ? Sha256(full) : sha);
    }

    private bool TrySession(LinkRequest request, out PairedDevice device)
    {
        device = null!;
        string header = request.Headers.GetValueOrDefault("Authorization") ?? "";
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !sessions.TryGetValue(header[7..].Trim(), out string? id))
        {
            return false;
        }

        return devices.TryGetValue(id, out device!);
    }

    private string Issue(string deviceId)
    {
        string token = LinkBytes.Hex(RandomNumberGenerator.GetBytes(32));
        sessions[token] = deviceId;
        return token;
    }

    private static void MoveIntoPlace(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: false);
            File.Delete(source);
        }
    }

    private bool Verify(string publicKey, string nonce, string signature)
    {
        try
        {
            byte[] point = Convert.FromBase64String(publicKey);
            byte[] sig = Convert.FromBase64String(signature);
            if (point.Length != 65 || point[0] != 4 || sig.Length != 64)
            {
                return false;
            }

            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33], Y = point[33..65] },
            });
            return ecdsa.VerifyData(LinkProtocol.SessionMessage(nonce, Fingerprint), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private void RotateCode()
    {
        if (options.FixedCode is not null || DateTimeOffset.UtcNow < codeExpires)
        {
            return;
        }

        code = NewCode();
        attempts = 0;
        codeExpires = DateTimeOffset.UtcNow.AddMinutes(2);
        QrText = BuildQr(Addresses.FirstOrDefault() ?? "127.0.0.1");
        Report("配对码已更新。");
    }

    private string BuildQr(string host) =>
        $"picharbor://link?v=1&host={host}&port={Port}&fp={Fingerprint}&code={code}";

    private string ComputerName() =>
        string.IsNullOrWhiteSpace(options.ComputerName) ? Environment.MachineName : options.ComputerName.Trim();

    private async Task BeaconLoop()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                foreach (string address in Addresses)
                {
                    byte[] payload = Encoding.UTF8.GetBytes(LinkJson.Text(new { v = LinkProtocol.Version, name = ComputerName(), host = address, port = Port, fp = Fingerprint }));
                    await udp.SendAsync(payload, new IPEndPoint(IPAddress.Parse(LinkProtocol.BeaconGroup), LinkProtocol.BeaconPort), lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                if (lifetime.IsCancellationRequested)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static List<string> ListAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                {
                    addresses.Add(address.ToString());
                }
            }
        }
        catch (SocketException)
        {
        }

        if (addresses.Count == 0)
        {
            addresses.Add("127.0.0.1");
        }

        return addresses;
    }

    private void Report(string message) => Status?.Invoke(message);

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    private static string SourcePath(string deviceId, ItemBody item) => $"wireless/{deviceId}/{item.Id}/{Path.GetFileName(item.Name)}";

    private static DateTimeOffset? FromUnix(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return LinkBytes.Hex(SHA256.HashData(stream));
    }

    private static X509Certificate2 LoadCertificate(bool ephemeral)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicHarbor", "link");
        string stored = Path.Combine(directory, "host.pfx");
        if (!ephemeral && File.Exists(stored))
        {
            try
            {
                byte[] protectedBytes = File.ReadAllBytes(stored);
                byte[] pfx = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser)
                    : protectedBytes;
                return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            }
            catch (CryptographicException)
            {
            }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PicHarbor Link", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        byte[] exported = created.Export(X509ContentType.Pfx);
        if (!ephemeral)
        {
            Directory.CreateDirectory(directory);
            byte[] storedBytes = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser)
                : exported;
            File.WriteAllBytes(stored, storedBytes);
        }

        return X509CertificateLoader.LoadPkcs12(exported, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    private void SaveDevices()
    {
        if (options.EphemeralCertificate)
        {
            return;
        }

        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicHarbor", "link");
        Directory.CreateDirectory(directory);
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(devices.Values.ToList(), LinkJson.Options));
        byte[] stored = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser)
            : json;
        File.WriteAllBytes(Path.Combine(directory, "devices.bin"), stored);
    }

    private static List<PairedDevice> LoadDevices()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicHarbor", "link", "devices.bin");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            byte[] stored = File.ReadAllBytes(path);
            byte[] json = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)
                : stored;
            return JsonSerializer.Deserialize<List<PairedDevice>>(json, LinkJson.Options) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return [];
        }
    }

    private sealed record PairedDevice(string Id, string Name, string Platform, string PublicKey);

    private sealed record PairBody(string DeviceId, string? DeviceName, string? Platform, string? Nonce, string? Mac, string? PublicKey);

    private sealed record SessionBody(string? DeviceId, string? Nonce, string? Signature);

    private sealed record ManifestBody(string? Model, List<ItemBody>? Items);

    private sealed record ItemBody(string Id, string Name, long Size, long Created, string? Sha256, string? PairId, string? Role);
}
