using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PicHarbor.Core.Updates;

/// <summary>Persisted update preference; zero keeps existing configurations automatic.</summary>
public enum UpdateMode
{
    /// <summary>Check on startup and install on normal exit.</summary>
    Automatic,
    /// <summary>Only check, download and restart when requested.</summary>
    Manual,
    /// <summary>Open the release page for a user-managed replacement.</summary>
    Browser
}

/// <summary>A validated stable Windows GUI release and its exact attachment pair.</summary>
/// <param name="Tag">GitHub release tag.</param>
/// <param name="Version">Normalized numeric version.</param>
/// <param name="Download">Official executable attachment URL.</param>
/// <param name="Checksum">Official checksum attachment URL.</param>
/// <param name="Size">Expected executable length in bytes.</param>
public sealed record ReleaseUpdate(string Tag, Version Version, Uri Download, Uri Checksum, long Size)
{
    /// <summary>Canonical public upstream repository.</summary>
    public const string Repository = "Cc-Cece/PicHarbor";
    /// <summary>Browser fallback for all update modes.</summary>
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases/latest";
    /// <summary>Exact GUI asset name used by the release workflow.</summary>
    public const string AssetName = "PicHarbor.Gui.exe";
}

/// <summary>Pure policy shared by the settings UI, exit path and updater tests.</summary>
public static class UpdatePolicy
{
    /// <summary>The exit permission dialog is exclusive to a ready, scheduled update that needs UAC.</summary>
    public static bool ShowExitPrompt(bool ready, bool installOnExit, bool requiresElevation)
        => ready && installOnExit && requiresElevation;

    /// <summary>Persists checkbox choices on both confirmation and cancellation; skipping always suppresses installation.</summary>
    public static (UpdateMode Mode, string SkippedVersion, bool Install) ApplyConsent(UpdateMode mode, string skippedVersion,
        string targetVersion, bool accepted, bool skip, bool disableAutomatic)
        => (disableAutomatic ? UpdateMode.Manual : mode, skip ? targetVersion : skippedVersion, accepted && !skip);

    /// <summary>Parses stable SemVer/numeric MinVer versions, normalizing to four components.</summary>
    public static Version? ParseVersion(string? value, bool stableOnly = true)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:\+[^\s]+)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success && !stableOnly)
            return ParseVersion(value.Split('-')[0], true);
        if (!match.Success) return null;
        return Version.TryParse(string.Join(".", Enumerable.Range(1, 4)
            .Select(i => match.Groups[i].Success ? match.Groups[i].Value : "0")), out var version) ? version : null;
    }

    /// <summary>Requires a SHA-256 sum bound to the expected GUI filename.</summary>
    public static string ParseChecksum(string text)
    {
        var match = Regex.Match(text.Trim(), @"\A([a-fA-F0-9]{64})\s+\*?PicHarbor\.Gui\.exe\z");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant()
            : throw new InvalidDataException("Invalid SHA-256 attachment.");
    }

    /// <summary>Validates size, content hash and embedded product version before execution.</summary>
    public static async Task VerifyAsync(string path, ReleaseUpdate release, string hash, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != release.Size) throw new InvalidDataException("Update size mismatch.");
        if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update SHA-256 mismatch.");
        if (ParseVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion) != release.Version)
            throw new InvalidDataException("Update executable version mismatch.");
    }
}

/// <summary>Published version metadata, independent of whether installable attachments are present.</summary>
/// <param name="Tag">Published stable tag.</param>
/// <param name="Version">Normalized version.</param>
/// <param name="Metadata">Release response retained for subsequent attachment validation.</param>
public sealed record PublishedRelease(string Tag, Version Version, JsonElement Metadata)
{
    /// <summary>Resolves and validates the attachment pair only when download is actually requested.</summary>
    public ReleaseUpdate GetUpdate() => ReleaseClient.Parse(Metadata)
        ?? throw new InvalidDataException("Release is not a stable update.");
}

/// <summary>Anonymous GitHub release access. No credential or arbitrary mirror URL is accepted.</summary>
public sealed class ReleaseClient(HttpClient http)
{
    /// <summary>Reads GitHub's latest published stable release.</summary>
    public async Task<ReleaseUpdate?> LatestAsync(CancellationToken ct)
        => Parse(await GetJsonAsync("latest", ct));

    /// <summary>Checks the latest version even for development builds or releases without GUI attachments.</summary>
    public async Task<PublishedRelease?> LatestReleaseAsync(CancellationToken ct)
        => Describe(await GetJsonAsync("latest", ct));

    /// <summary>Re-fetches a particular stable release before committing installation.</summary>
    public async Task<ReleaseUpdate> ByTagAsync(string tag, CancellationToken ct)
        => Parse(await GetJsonAsync("tags/" + Uri.EscapeDataString(tag), ct))
            ?? throw new InvalidDataException("Release is not a stable update.");

    private async Task<JsonElement> GetJsonAsync(string suffix, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{ReleaseUpdate.Repository}/releases/{suffix}");
        request.Headers.UserAgent.ParseAdd("PicHarbor-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct);
        // Do not retry 403/429 in a loop; the next user action/start can retry after GitHub's limit expires.
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.Clone();
    }

    /// <summary>Rejects incomplete or foreign release attachment URLs.</summary>
    public static ReleaseUpdate? Parse(JsonElement json)
    {
        var published = Describe(json);
        if (published is null) return null;
        string tag = published.Tag;
        var version = published.Version;
        var assets = json.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement Asset(string name) => assets.Single(a => a.GetProperty("name").GetString() == name);
        Uri Url(JsonElement asset)
        {
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            string prefix = $"/{ReleaseUpdate.Repository}/releases/download/{Uri.EscapeDataString(tag)}/";
            if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected release asset URL.");
            return uri;
        }
        var exe = Asset(ReleaseUpdate.AssetName);
        long size = exe.GetProperty("size").GetInt64();
        if (size <= 0 || size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid executable size.");
        return new(tag, version, Url(exe), Url(Asset(ReleaseUpdate.AssetName + ".sha256")), size);
    }

    private static PublishedRelease? Describe(JsonElement json)
    {
        if (json.GetProperty("draft").GetBoolean() || json.GetProperty("prerelease").GetBoolean()) return null;
        string tag = json.GetProperty("tag_name").GetString()!;
        return UpdatePolicy.ParseVersion(tag) is { } version ? new(tag, version, json) : null;
    }

    /// <summary>Downloads the expected SHA-256 sum over HTTPS.</summary>
    public async Task<string> ChecksumAsync(ReleaseUpdate release, CancellationToken ct)
        => UpdatePolicy.ParseChecksum(await http.GetStringAsync(release.Checksum, ct));

    /// <summary>Streams a bounded attachment to a new temporary file; caller owns cleanup.</summary>
    public async Task DownloadAsync(ReleaseUpdate release, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > release.Size) throw new InvalidDataException("Update exceeds expected size.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            progress?.Report(total * 100d / release.Size);
        }
        if (total != release.Size) throw new InvalidDataException("Incomplete update download.");
    }
}
