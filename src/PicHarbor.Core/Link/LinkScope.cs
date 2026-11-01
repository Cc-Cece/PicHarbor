using System.Globalization;
using System.Text.Json;

namespace PicHarbor.Core.Link;

/// <summary>A computer-requested media selection. The phone applies it before confirmation.</summary>
public sealed record LinkScopeSpec
{
    /// <summary>Selection preset: default, all, date or advanced.</summary>
    public string Mode { get; init; } = "default";
    /// <summary>Stable phone collection identifiers; used by advanced mode.</summary>
    public string[] Albums { get; init; } = [];
    /// <summary>Apply the local-calendar date bounds.</summary>
    public bool DateEnabled { get; init; }
    /// <summary>Inclusive first day, formatted yyyy-MM-dd.</summary>
    public string? From { get; init; }
    /// <summary>Inclusive last day, formatted yyyy-MM-dd.</summary>
    public string? To { get; init; }
    /// <summary>Include image assets, including whole Live Photos.</summary>
    public bool Photos { get; init; } = true;
    /// <summary>Include independent video assets.</summary>
    public bool Videos { get; init; } = true;
    /// <summary>Exclude ordinary images with a known size below the threshold.</summary>
    public bool IgnoreSmallImages { get; init; } = true;
    /// <summary>Image threshold in KiB, from 10 through 1024.</summary>
    public int MinImageKb { get; init; } = 100;
    /// <summary>Exclude known cache and trash locations.</summary>
    public bool FilterCache { get; init; } = true;

    /// <summary>Return a user-facing error, or null for a valid selection.</summary>
    public string? Validate()
    {
        if (Albums is null) return "相册清单无效。";
        if (Mode is not ("default" or "all" or "date" or "advanced")) return "范围模式无效。";
        if (!Photos && !Videos) return "请至少选择一种媒体类型。";
        if (Mode == "advanced" && Albums.Length == 0) return "请至少选择一个相册或文件夹。";
        if (MinImageKb is < 10 or > 1024) return "小图阈值须在 10–1024 KB 之间。";
        if (DateEnabled)
        {
            if (!string.IsNullOrEmpty(From) && !DateOnly.TryParseExact(From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "起始日期无效。";
            if (!string.IsNullOrEmpty(To) && !DateOnly.TryParseExact(To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "结束日期无效。";
            if (!string.IsNullOrEmpty(From) && !string.IsNullOrEmpty(To) && string.CompareOrdinal(From, To) > 0) return "起始日期不能晚于结束日期。";
        }
        return null;
    }

    /// <summary>Encode an explicitly versioned phone selection.</summary>
    public string ToWire() => "scope2:" + JsonSerializer.Serialize(this, LinkJson.Options);
}

/// <summary>One authorized phone media collection.</summary>
public sealed record LinkAlbum(string Id, string Name, int Count, long Bytes, bool IsDefault);
/// <summary>A revisioned inventory and preview request.</summary>
public sealed record LinkCatalogRequest(long Revision, string Scope);
/// <summary>Phone-computed inventory and preview; byte totals omit unknown sizes.</summary>
public sealed record LinkCatalogReport(long Revision, string TimeZone, LinkAlbum[] Albums, int Count, long Bytes, int UnknownSize, int UnknownDate, string? Error = null);
/// <summary>A remembered pairing with its current presence and advertised capabilities.</summary>
public sealed record LinkKnownPeer(string PairingId, string Name, string Platform, string? ArchiveId, bool Online, bool Ready, bool ScopeV2);
