using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PicHarbor.Core.Android;

/// <summary>
/// Lightweight, cross-platform FTP client designed for LAN Android FTP servers.
/// Zero external dependencies; implements FTP Control Channel and PASV/EPSV Data Channel.
/// </summary>
public sealed class SimpleFtpClient : IDisposable
{
    private TcpClient? controlClient;
    private StreamReader? controlReader;
    private StreamWriter? controlWriter;
    private string host = "127.0.0.1";

    /// <summary>Gets a value indicating whether the FTP control connection is open.</summary>
    public bool IsConnected => controlClient is { Connected: true };

    /// <summary>
    /// Connects to the remote FTP server and authenticates with username and password.
    /// </summary>
    public async Task ConnectAsync(string host, int port, string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        this.host = host;
        Disconnect();

        controlClient = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        await controlClient.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);

        NetworkStream stream = controlClient.GetStream();
        controlReader = new StreamReader(stream, Encoding.UTF8);
        controlWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        string greeting = await ReadResponseAsync(timeoutCts.Token).ConfigureAwait(false);
        if (!greeting.StartsWith("220", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected FTP greeting from {host}:{port}: {greeting}");
        }

        string userResp = await SendCommandAsync($"USER {username}", timeoutCts.Token).ConfigureAwait(false);
        if (userResp.StartsWith("331", StringComparison.Ordinal))
        {
            string passResp = await SendCommandAsync($"PASS {password ?? string.Empty}", timeoutCts.Token).ConfigureAwait(false);
            if (!passResp.StartsWith("230", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"FTP authentication failed for user '{username}': {passResp}");
            }
        }
        else if (!userResp.StartsWith("230", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FTP USER command rejected: {userResp}");
        }

        // Set Binary transfer mode
        await SendCommandAsync("TYPE I", timeoutCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Recursively ensures that the specified remote directory path exists on the FTP server (using CWD / MKD).
    /// </summary>
    public async Task EnsureDirectoryExistsAsync(string remoteDir, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        string normalized = remoteDir.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = "";

        foreach (string segment in segments)
        {
            current += "/" + segment;
            string cwdResp = await SendCommandAsync($"CWD {current}", cancellationToken).ConfigureAwait(false);
            if (!cwdResp.StartsWith("250", StringComparison.Ordinal))
            {
                string mkdResp = await SendCommandAsync($"MKD {current}", cancellationToken).ConfigureAwait(false);
                if (!mkdResp.StartsWith("257", StringComparison.Ordinal) && !mkdResp.StartsWith("550", StringComparison.Ordinal))
                {
                    // Ignore 550 if directory already exists
                    System.Diagnostics.Debug.WriteLine($"FTP MKD warning for {current}: {mkdResp}");
                }
            }
        }

        // CWD back to root or target
        await SendCommandAsync($"CWD /{normalized}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a local file to a remote FTP path using Passive Mode (PASV / EPSV).
    /// </summary>
    public async Task UploadFileAsync(string localFilePath, string remoteFilePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException("Local file for FTP upload does not exist.", localFilePath);
        }

        string remotePath = remoteFilePath.Replace('\\', '/');

        using TcpClient dataClient = await OpenPassiveDataConnectionAsync(cancellationToken).ConfigureAwait(false);
        string storResp = await SendCommandAsync($"STOR {remotePath}", cancellationToken).ConfigureAwait(false);

        if (!storResp.StartsWith("150", StringComparison.Ordinal) && !storResp.StartsWith("125", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FTP STOR command rejected ({remotePath}): {storResp}");
        }

        using (NetworkStream dataStream = dataClient.GetStream())
        using (FileStream fileStream = File.OpenRead(localFilePath))
        {
            byte[] buffer = new byte[81920];
            int read;
            long totalSent = 0;

            while ((read = await fileStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dataStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                totalSent += read;
                progress?.Report(totalSent);
            }

            await dataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        string completeResp = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        if (!completeResp.StartsWith("226", StringComparison.Ordinal) && !completeResp.StartsWith("250", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FTP upload completing response error ({remotePath}): {completeResp}");
        }
    }

    /// <summary>
    /// Returns the size of a remote file on the FTP server in bytes, or null if the file does not exist or size is unavailable.
    /// </summary>
    public async Task<long?> GetFileSizeAsync(string remoteFilePath, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        string remotePath = remoteFilePath.Replace('\\', '/');
        try
        {
            string resp = await SendCommandAsync($"SIZE {remotePath}", cancellationToken).ConfigureAwait(false);
            if (resp.StartsWith("213", StringComparison.Ordinal))
            {
                string sizeStr = resp.Substring(4).Trim();
                if (long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
                {
                    return size;
                }
            }
        }
        catch
        {
            // Ignore error if SIZE is unsupported or file missing
        }
        return null;
    }

    /// <summary>
    /// Deletes a file on the remote FTP server (using DELE command).
    /// </summary>
    public async Task DeleteFileAsync(string remoteFilePath, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        string remotePath = remoteFilePath.Replace('\\', '/');
        string deleResp = await SendCommandAsync($"DELE {remotePath}", cancellationToken).ConfigureAwait(false);
        if (!deleResp.StartsWith("250", StringComparison.Ordinal) && !deleResp.StartsWith("200", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FTP DELE command failed ({remotePath}): {deleResp}");
        }
    }

    /// <summary>
    /// Uploads text content to a remote FTP path.
    /// </summary>
    public async Task UploadTextAsync(string remoteFilePath, string content, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        string remotePath = remoteFilePath.Replace('\\', '/');

        using TcpClient dataClient = await OpenPassiveDataConnectionAsync(cancellationToken).ConfigureAwait(false);
        string storResp = await SendCommandAsync($"STOR {remotePath}", cancellationToken).ConfigureAwait(false);

        if (!storResp.StartsWith("150", StringComparison.Ordinal) && !storResp.StartsWith("125", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FTP STOR text rejected ({remotePath}): {storResp}");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using (NetworkStream dataStream = dataClient.GetStream())
        {
            await dataStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await dataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        string completeResp = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        if (!completeResp.StartsWith("226", StringComparison.Ordinal) && !completeResp.StartsWith("250", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FTP upload text completing response error ({remotePath}): {completeResp}");
        }
    }

    /// <summary>
    /// Downloads a remote text file from FTP (or returns null if file does not exist).
    /// </summary>
    public async Task<string?> DownloadTextAsync(string remoteFilePath, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        string remotePath = remoteFilePath.Replace('\\', '/');

        TcpClient dataClient;
        try
        {
            dataClient = await OpenPassiveDataConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        using (dataClient)
        {
            string retrResp = await SendCommandAsync($"RETR {remotePath}", cancellationToken).ConfigureAwait(false);
            if (!retrResp.StartsWith("150", StringComparison.Ordinal) && !retrResp.StartsWith("125", StringComparison.Ordinal))
            {
                return null;
            }

            using NetworkStream dataStream = dataClient.GetStream();
            using StreamReader reader = new StreamReader(dataStream, Encoding.UTF8);
            string content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            string completeResp = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            return content;
        }
    }

    /// <summary>
    /// Tests connecting to an FTP server, creating target directory, and reading server response.
    /// </summary>
    public static async Task<bool> TestConnectionAsync(string host, int port, string user, string password, string remoteDir, CancellationToken cancellationToken = default)
    {
        using var client = new SimpleFtpClient();
        await client.ConnectAsync(host, port, user, password, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(remoteDir))
        {
            await client.EnsureDirectoryExistsAsync(remoteDir, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async Task<TcpClient> OpenPassiveDataConnectionAsync(CancellationToken cancellationToken)
    {
        // Try EPSV first (Extended Passive) then PASV fallback
        string epsvResp = await SendCommandAsync("EPSV", cancellationToken).ConfigureAwait(false);
        if (epsvResp.StartsWith("229", StringComparison.Ordinal))
        {
            int openParen = epsvResp.IndexOf("(|", StringComparison.Ordinal);
            int closeParen = epsvResp.IndexOf("|)", StringComparison.Ordinal);
            if (openParen >= 0 && closeParen > openParen)
            {
                string portStr = epsvResp.Substring(openParen + 3, closeParen - openParen - 3);
                if (int.TryParse(portStr, CultureInfo.InvariantCulture, out int dataPort))
                {
                    var dataClient = new TcpClient();
                    await dataClient.ConnectAsync(host, dataPort, cancellationToken).ConfigureAwait(false);
                    return dataClient;
                }
            }
        }

        // Fallback to PASV
        string pasvResp = await SendCommandAsync("PASV", cancellationToken).ConfigureAwait(false);
        if (!pasvResp.StartsWith("227", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"FTP PASV mode rejected by server: {pasvResp}");
        }

        Match match = Regex.Match(pasvResp, @"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)");
        if (!match.Success)
        {
            throw new InvalidOperationException($"Could not parse PASV response: {pasvResp}");
        }

        string pasvHost = $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}.{match.Groups[4].Value}";
        int p1 = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        int p2 = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
        int pasvPort = (p1 * 256) + p2;

        var pasvClient = new TcpClient();
        // Use host instead of pasvHost if pasvHost is 0.0.0.0 or private NAT
        string targetHost = (pasvHost == "0.0.0.0" || pasvHost.StartsWith("127.")) ? host : pasvHost;
        await pasvClient.ConnectAsync(targetHost, pasvPort, cancellationToken).ConfigureAwait(false);
        return pasvClient;
    }

    private async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await controlWriter!.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadResponseAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        string? line = await controlReader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            throw new InvalidOperationException("FTP server closed connection unexpectedly.");
        }

        // Multi-line FTP response check (e.g. 220-Header \n 220 Ready)
        if (line.Length >= 4 && line[3] == '-')
        {
            string code = line.Substring(0, 3);
            StringBuilder sb = new(line);
            while (true)
            {
                string? subLine = await controlReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (subLine is null) break;
                sb.AppendLine(subLine);
                if (subLine.StartsWith(code + " ", StringComparison.Ordinal))
                {
                    break;
                }
            }
            return sb.ToString();
        }

        return line;
    }

    private void EnsureConnected()
    {
        if (controlClient is not { Connected: true } || controlReader is null || controlWriter is null)
        {
            throw new InvalidOperationException("FTP client is not connected.");
        }
    }

    /// <summary>
    /// Closes the FTP control connection and releases network resources.
    /// </summary>
    public void Disconnect()
    {
        try
        {
            if (IsConnected && controlWriter is not null)
            {
                controlWriter.WriteLine("QUIT");
            }
        }
        catch { }
        finally
        {
            controlWriter?.Dispose();
            controlReader?.Dispose();
            controlClient?.Dispose();
            controlClient = null;
            controlReader = null;
            controlWriter = null;
        }
    }

    /// <summary>
    /// Disposes the FTP client and underlying streams.
    /// </summary>
    public void Dispose()
    {
        Disconnect();
    }
}
