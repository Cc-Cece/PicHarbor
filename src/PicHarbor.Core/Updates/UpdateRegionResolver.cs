using System.Net.NetworkInformation;
using System.Text.Json;

namespace PicHarbor.Core.Updates;

/// <summary>Persisted user override, independent of the device-local country cache.</summary>
public enum UpdateRegionMode
{
    /// <summary>Look up the public exit country.</summary>
    Automatic,
    /// <summary>Race direct and mirror routes immediately.</summary>
    Restricted,
    /// <summary>Try a quality-qualified direct route first.</summary>
    Other
}

/// <summary>Effective route-selection profile.</summary>
public enum UpdateNetworkRegion
{
    /// <summary>Lookup failed; race all non-proxy routes.</summary>
    Unknown,
    /// <summary>Mainland China or a user-selected restricted profile.</summary>
    Restricted,
    /// <summary>Other exit country or a user-selected direct-first profile.</summary>
    Other
}

/// <summary>Country cache invalidation and lookup seam used by the downloader and tests.</summary>
public interface IUpdateRegionResolver
{
    /// <summary>Resolves the current override or a bounded country lookup.</summary>
    Task<UpdateNetworkRegion> ResolveAsync(UpdateRegionMode mode, CancellationToken ct);
    /// <summary>Forgets any cached country after a network change or explicit reselection.</summary>
    void Invalidate();
}

/// <summary>Stores only an ISO country code and timestamp, never a public IP or raw trace response.</summary>
public sealed class UpdateRegionResolver : IUpdateRegionResolver, IDisposable
{
    private sealed record CountryCache(string Country, DateTimeOffset CheckedAt);
    private readonly string cachePath;
    private readonly TimeProvider time;
    private readonly Func<CancellationToken, Task<string>> lookup;
    private readonly bool watchNetwork;
    private CountryCache? cached;
    private int generation;
    private readonly object cacheSync = new();

    /// <summary>Creates a device-local cache with injectable lookup/time and optional network notifications.</summary>
    public UpdateRegionResolver(string cachePath, Func<CancellationToken, Task<string>>? lookup = null,
        TimeProvider? time = null, bool watchNetwork = true)
    {
        this.cachePath = cachePath;
        this.time = time ?? TimeProvider.System;
        this.lookup = lookup ?? LookupCountryAsync;
        this.watchNetwork = watchNetwork;
        try { if (File.Exists(cachePath)) cached = JsonSerializer.Deserialize<CountryCache>(File.ReadAllText(cachePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        if (!ValidCountry(cached?.Country)) cached = null;
        if (watchNetwork) NetworkChange.NetworkAddressChanged += NetworkChanged;
    }

    /// <inheritdoc/>
    public async Task<UpdateNetworkRegion> ResolveAsync(UpdateRegionMode mode, CancellationToken ct)
    {
        if (mode == UpdateRegionMode.Restricted) return UpdateNetworkRegion.Restricted;
        if (mode == UpdateRegionMode.Other) return UpdateNetworkRegion.Other;
        CountryCache? snapshot;
        lock (cacheSync) snapshot = cached;
        var age = snapshot is null ? TimeSpan.MaxValue : time.GetUtcNow() - snapshot.CheckedAt;
        if (snapshot is not null && age >= TimeSpan.Zero && age < TimeSpan.FromDays(7)) return Classify(snapshot.Country);
        int before = Volatile.Read(ref generation);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            string country = (await lookup(linked.Token).WaitAsync(TimeSpan.FromSeconds(2), time, ct).ConfigureAwait(false)).Trim().ToUpperInvariant();
            if (!ValidCountry(country)) return UpdateNetworkRegion.Unknown;
            lock (cacheSync)
            {
                if (before != Volatile.Read(ref generation)) return UpdateNetworkRegion.Unknown;
                cached = new(country, time.GetUtcNow());
                try { Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!); File.WriteAllText(cachePath, JsonSerializer.Serialize(cached)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return Classify(country);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or TimeoutException))
        { return UpdateNetworkRegion.Unknown; }
    }

    private static UpdateNetworkRegion Classify(string country) => country == "CN" ? UpdateNetworkRegion.Restricted : UpdateNetworkRegion.Other;
    private static bool ValidCountry(string? country) => country is { Length: 2 } && country != "XX" && country.All(c => c is >= 'A' and <= 'Z');

    private static async Task<string> LookupCountryAsync(CancellationToken ct)
    {
        using var client = UpdateHttp.CreateDirectClient();
        using var response = await client.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[8193];
        int length = 0;
        while (length < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > 8192) throw new InvalidDataException("Invalid region response.");
        string text = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
        return text.Split('\n').FirstOrDefault(line => line.StartsWith("loc=", StringComparison.Ordinal))?[4..].Trim() ?? "";
    }

    private void NetworkChanged(object? sender, EventArgs e) => Invalidate();
    /// <inheritdoc/>
    public void Invalidate()
    {
        lock (cacheSync)
        {
            Interlocked.Increment(ref generation);
            cached = null;
            try { File.Delete(cachePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    /// <inheritdoc/>
    public void Dispose() { if (watchNetwork) NetworkChange.NetworkAddressChanged -= NetworkChanged; }
}
