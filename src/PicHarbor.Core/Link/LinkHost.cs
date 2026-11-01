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
    /// <summary>Archive directory that receives the photos. Empty while the user has not chosen one.</summary>
    public string ArchiveRoot { get; set; } = "";

    /// <summary>Folder layout used for newly received files.</summary>
    public OrganizeScheme Scheme { get; set; } = OrganizeScheme.Month;

    /// <summary>Place each phone in its own folder inside the archive.</summary>
    public bool GroupByDevice { get; set; }

    /// <summary>Name shown to the phone. The computer name is used when this is empty.</summary>
    public string? ComputerName { get; init; }

    /// <summary>TCP port. Zero asks the system for a free port.</summary>
    public int Port { get; init; }

    /// <summary>Send a UDP announcement on the local network.</summary>
    public bool Beacon { get; init; } = true;

    /// <summary>Android identity already saved for FTP. A different phone must not replace it.</summary>
    public string? AndroidBaselineId { get; set; }

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
    private readonly LinkDesk desk = new();
    private readonly Dictionary<string, SessionRun> runs = new(StringComparer.Ordinal);
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
        QrText = BuildQr(LinkDesk.PreferredHost(Addresses));
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

    /// <summary>Raised when the online list or a request changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when a phone asks this computer to receive files.</summary>
    public event Action<LinkOffer>? OfferArrived;

    /// <summary>Phones that have checked in recently.</summary>
    public IReadOnlyList<LinkPeer> OnlinePeers => desk.Online(DateTimeOffset.UtcNow);

    /// <summary>Requests waiting for someone to confirm.</summary>
    public IReadOnlyList<LinkOffer> OpenOffers => desk.PendingForAll(DateTimeOffset.UtcNow);

    /// <summary>Store the UDID read over the cable. The phone writes the file itself.</summary>
    public void NoteIphoneUdid(string? udid) => desk.NoteIphoneUdid(udid);

    /// <summary>Result for the manual iPhone identity button.</summary>
    public LinkWriteOutcome IphoneWriteOutcome(string pairingId) => desk.IphoneWriteOutcome(pairingId, DateTimeOffset.UtcNow);

    /// <summary>Confirm a request from the computer side.</summary>
    public bool AcceptOffer(string offerId, string pairingId)
    {
        bool accepted = desk.Accept(offerId, pairingId, DateTimeOffset.UtcNow);
        if (accepted)
        {
            Changed?.Invoke();
        }

        return accepted;
    }

    /// <summary>Decline a request from the computer side.</summary>
    public void RejectOffer(string offerId, string pairingId)
    {
        desk.Reject(offerId, pairingId);
        Changed?.Invoke();
    }

    /// <summary>Ask an online phone to back up a scope. The phone confirms before any file moves.</summary>
    public LinkOffer? AskPhone(string pairingId, string scope, int count)
    {
        LinkOffer? offer = desk.CreateOffer(pairingId, "computer-to-phone", scope, count, DateTimeOffset.UtcNow);
        if (offer is not null)
        {
            Changed?.Invoke();
        }

        return offer;
    }

    /// <summary>Replace the pairing code so a newly shown QR stays valid for two minutes.</summary>
    public void RefreshPairingCode()
    {
        if (options.FixedCode is not null)
        {
            return;
        }

        code = NewCode();
        attempts = 0;
        codeExpires = DateTimeOffset.UtcNow.AddMinutes(2);
        QrText = BuildQr(LinkDesk.PreferredHost(Addresses));
        Report("配对码已更新。");
    }

    /// <summary>Point later copies at this archive. Listening does not require a folder.</summary>
    public void UseArchive(string? path, OrganizeScheme scheme, bool groupByDevice)
    {
        options.ArchiveRoot = path?.Trim() ?? "";
        options.Scheme = scheme;
        options.GroupByDevice = groupByDevice;
        if (!string.IsNullOrWhiteSpace(options.ArchiveRoot))
        {
            Directory.CreateDirectory(options.ArchiveRoot);
        }
    }

    /// <summary>Android codes already bound to a pairing key, without the <c>android:</c> prefix.</summary>
    public IReadOnlyList<string> BoundAndroidCodes() =>
        devices.Values
            .Select(device => device.ArchiveId)
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.StartsWith("android:", StringComparison.OrdinalIgnoreCase))
            .Select(id => id!["android:".Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Look up a request. An unanswered request older than two minutes is already gone.</summary>
    public LinkOffer? FindOffer(string offerId) => desk.Find(offerId, DateTimeOffset.UtcNow);

    /// <summary>Raised when a phone presents an identity that is not the one bound to its key.</summary>
    public event Action<string>? IdentityConflict;

    /// <summary>Starts listening.</summary>
    public static LinkHost Start(LinkHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.ArchiveRoot))
        {
            Directory.CreateDirectory(options.ArchiveRoot);
        }
        var certificate = LoadCertificate(options.EphemeralCertificate);
        var known = options.EphemeralCertificate ? [] : LoadDevices();
        var listener = Listen(options);
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
                await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(new { v = LinkProtocol.Version, name = ComputerName(), fp = Fingerprint, port = Port, hosts = Addresses }), lifetime.Token).ConfigureAwait(false);
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
            case "POST /v1/hello":
                await Hello(stream, request).ConfigureAwait(false);
                return;
            case "POST /v1/offers":
                await CreateOffer(stream, request).ConfigureAwait(false);
                return;
            case "GET /v1/offers":
                await ListOffers(stream, request).ConfigureAwait(false);
                return;
        }

        if (request.Method == "POST" && request.Path.StartsWith("/v1/offers/", StringComparison.Ordinal))
        {
            await AnswerOffer(stream, request).ConfigureAwait(false);
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
                QrText = BuildQr(LinkDesk.PreferredHost(Addresses));
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

    private async Task Hello(Stream stream, LinkRequest request)
    {
        if (!TrySession(request, out var device))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"sign_in"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        var body = JsonSerializer.Deserialize<HelloBody>(request.Body, LinkJson.Options);
        var state = LinkDesk.Classify(body?.ArchiveId, body?.Unreadable == true);
        LinkHelloResult result = desk.Hello(device.Id, body?.Name ?? device.Name, body?.Platform ?? device.Platform, body?.ArchiveId, state, body?.Directory == true, false, DateTimeOffset.UtcNow);
        if (result.ArchiveId is not null)
        {
            RememberArchive(device, result.ArchiveId);
        }

        if (result.Conflict)
        {
            IdentityConflict?.Invoke(result.ArchiveId ?? "");
        }

        Changed?.Invoke();
        await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(new { archiveId = result.ArchiveId, deliver = result.Deliver, conflict = result.Conflict }), lifetime.Token).ConfigureAwait(false);
    }

    private async Task CreateOffer(Stream stream, LinkRequest request)
    {
        if (!TrySession(request, out var device))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"sign_in"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ArchiveRoot))
        {
            await LinkResponses.WriteAsync(stream, 409, "Conflict", """{"error":"no_archive"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        var body = JsonSerializer.Deserialize<OfferBody>(request.Body, LinkJson.Options);
        LinkOffer? offer = desk.CreateOffer(device.Id, body?.Direction ?? "phone-to-computer", body?.Scope ?? "all", body?.Count ?? 0, DateTimeOffset.UtcNow);
        if (offer is null)
        {
            await LinkResponses.WriteAsync(stream, 409, "Conflict", """{"error":"no_offer"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        Changed?.Invoke();
        OfferArrived?.Invoke(offer);
        await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(offer), lifetime.Token).ConfigureAwait(false);
    }

    private async Task ListOffers(Stream stream, LinkRequest request)
    {
        if (!TrySession(request, out var device))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"sign_in"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await LinkResponses.WriteAsync(stream, 200, "OK", LinkJson.Text(new { offers = desk.Pending(device.Id, now), upload = desk.UploadAllowed(device.Id, now) }), lifetime.Token).ConfigureAwait(false);
    }

    private async Task AnswerOffer(Stream stream, LinkRequest request)
    {
        if (!TrySession(request, out var device))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"sign_in"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        string tail = request.Path["/v1/offers/".Length..];
        string id = tail.Split('/')[0];
        bool accept = tail.EndsWith("/accept", StringComparison.Ordinal);
        bool finish = tail.EndsWith("/finish", StringComparison.Ordinal);
        if (accept)
        {
            if (!desk.Accept(id, device.Id, DateTimeOffset.UtcNow))
            {
                await LinkResponses.WriteAsync(stream, 409, "Conflict", """{"error":"expired"}""", lifetime.Token).ConfigureAwait(false);
                return;
            }

            Changed?.Invoke();
            await LinkResponses.WriteAsync(stream, 200, "OK", """{"ok":true}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        if (finish)
        {
            FinishRun(device.Id);
            Changed?.Invoke();
            await LinkResponses.WriteAsync(stream, 200, "OK", """{"ok":true}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        desk.Reject(id, device.Id);
        Changed?.Invoke();
        await LinkResponses.WriteAsync(stream, 200, "OK", """{"ok":true}""", lifetime.Token).ConfigureAwait(false);
    }

    private async Task Manifest(Stream stream, LinkRequest request)
    {
        if (!TrySession(request, out var device))
        {
            await LinkResponses.WriteAsync(stream, 401, "Unauthorized", """{"error":"sign_in"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ArchiveRoot))
        {
            await LinkResponses.WriteAsync(stream, 409, "Conflict", """{"error":"no_archive"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        if (!desk.UploadAllowed(device.Id, DateTimeOffset.UtcNow))
        {
            await LinkResponses.WriteAsync(stream, 409, "Conflict", """{"error":"need_accept"}""", lifetime.Token).ConfigureAwait(false);
            return;
        }

        string archiveId = desk.ArchiveId(device.Id) ?? "";
        var body = JsonSerializer.Deserialize<ManifestBody>(request.Body, LinkJson.Options);
        var need = new List<string>();
        var have = new List<string>();
        using var journal = TransferJournal.Open(options.ArchiveRoot);
        journal.BindDevice(archiveId);
        journal.RegisterOrUpdateDevice(archiveId, device.Name, body?.Model, archiveId, device.Platform, DateTimeOffset.UtcNow);
        foreach (var item in body?.Items ?? [])
        {
            if (!LinkProtocol.IsSafeId(item.Id) || string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Sha256) && journal.HasCompletedHash(item.Sha256, options.ArchiveRoot))
            {
                have.Add(item.Id);
                continue;
            }

            string source = SourcePath(archiveId, item);
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

        if (!desk.UploadAllowed(device.Id, DateTimeOffset.UtcNow) || string.IsNullOrWhiteSpace(options.ArchiveRoot))
        {
            await LinkResponses.WriteAsync(stream, 409, "Conflict", """{"error":"need_accept"}""", lifetime.Token).ConfigureAwait(false);
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
        string? archiveId = desk.ArchiveId(device.Id);
        if (string.IsNullOrWhiteSpace(options.ArchiveRoot) || string.IsNullOrWhiteSpace(archiveId))
        {
            throw new InvalidOperationException("归档目录或设备身份还没有准备好。");
        }

        if (!string.IsNullOrWhiteSpace(sha))
        {
            using var probe = TransferJournal.Open(options.ArchiveRoot);
            probe.BindDevice(archiveId);
            if (probe.HasCompletedHash(sha, options.ArchiveRoot))
            {
                File.Delete(staging);
                return;
            }
        }

        using var journal = TransferJournal.Open(options.ArchiveRoot);
        journal.BindDevice(archiveId);
        string registered = journal.RegisterOrUpdateDevice(archiveId, device.Name, null, archiveId, device.Platform, DateTimeOffset.UtcNow);
        string source = SourcePath(archiveId, id, name);
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
        if (!runs.TryGetValue(device.Id, out SessionRun? run))
        {
            long sessionId = journal.BeginBackupSession(archiveId, registered, null, DateTimeOffset.UtcNow);
            run = new SessionRun(sessionId, 0, 0);
        }

        journal.RecordHistoryItem(run.SessionId, source, relative, size, DateTimeOffset.UtcNow, null);
        runs[device.Id] = run with { Files = run.Files + 1, Bytes = run.Bytes + size };
    }

    private void FinishRun(string pairingId)
    {
        desk.Complete(pairingId);
        if (!runs.Remove(pairingId, out SessionRun? run) || string.IsNullOrWhiteSpace(options.ArchiveRoot))
        {
            return;
        }

        using var journal = TransferJournal.Open(options.ArchiveRoot);
        journal.CompleteBackupSession(run.SessionId, run.Files, run.Bytes);
    }

    private void RememberArchive(PairedDevice device, string archiveId)
    {
        foreach (string old in devices.Where(pair => pair.Value.ArchiveId == archiveId && pair.Key != device.Id).Select(pair => pair.Key).ToList())
        {
            devices.TryRemove(old, out _);
        }

        devices[device.Id] = device with { ArchiveId = archiveId };
        SaveDevices();
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
        QrText = BuildQr(LinkDesk.PreferredHost(Addresses));
        Report("配对码已更新。");
    }

    private string BuildQr(string host)
    {
        string alts = string.Join(",", Addresses.Where(address => !string.Equals(address, host, StringComparison.Ordinal) && address != "127.0.0.1"));
        string text = $"picharbor://link?v=1&host={host}&port={Port}&fp={Fingerprint}&code={code}";
        return alts.Length == 0 ? text : text + "&alts=" + alts;
    }

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
                    byte[] payload = Encoding.UTF8.GetBytes(LinkJson.Text(new { v = LinkProtocol.Version, name = ComputerName(), host = address, hosts = Addresses, port = Port, fp = Fingerprint }));
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

    private static string SourcePath(string archiveId, string id, string name) => $"link/{archiveId}/{id}/{Path.GetFileName(name)}";

    private static string SourcePath(string archiveId, ItemBody item) => SourcePath(archiveId, item.Id, item.Name);

    private static TcpListener Listen(LinkHostOptions options)
    {
        if (options.EphemeralCertificate || options.Port != 0)
        {
            var listener = new TcpListener(IPAddress.Any, options.Port);
            listener.Start();
            return listener;
        }

        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicHarbor", "link");
        string portFile = Path.Combine(directory, "port.txt");
        if (int.TryParse(File.Exists(portFile) ? File.ReadAllText(portFile).Trim() : "", out int saved) && saved is > 0 and < 65536)
        {
            try
            {
                var reused = new TcpListener(IPAddress.Any, saved);
                reused.Start();
                return reused;
            }
            catch (SocketException)
            {
            }
        }

        var fresh = new TcpListener(IPAddress.Any, 0);
        fresh.Start();
        Directory.CreateDirectory(directory);
        File.WriteAllText(portFile, ((IPEndPoint)fresh.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return fresh;
    }

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

    private sealed record PairedDevice(string Id, string Name, string Platform, string PublicKey, string? ArchiveId = null);

    private sealed record HelloBody(string? ArchiveId, string? Name, string? Platform, bool Directory, bool Unreadable);

    private sealed record OfferBody(string? Direction, string? Scope, int Count);

    private sealed record SessionRun(long SessionId, int Files, long Bytes);

    private sealed record PairBody(string DeviceId, string? DeviceName, string? Platform, string? Nonce, string? Mac, string? PublicKey);

    private sealed record SessionBody(string? DeviceId, string? Nonce, string? Signature);

    private sealed record ManifestBody(string? Model, List<ItemBody>? Items);

    private sealed record ItemBody(string Id, string Name, long Size, long Created, string? Sha256, string? PairId, string? Role);
}
