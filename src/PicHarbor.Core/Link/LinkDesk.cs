using PicHarbor.Core.Device;

namespace PicHarbor.Core.Link;

/// <summary>Whether a phone could present its archive identity.</summary>
public enum LinkIdentityState
{
    /// <summary>The value can be used as the archive device id.</summary>
    Ok,

    /// <summary>The phone looked and the file is absent.</summary>
    Missing,

    /// <summary>The file exists but the value fails the serial rules.</summary>
    Invalid,

    /// <summary>The phone could not read the directory, so a new code must not be minted.</summary>
    Unreadable,
}

/// <summary>Result of asking the computer to hand the iPhone its UDID.</summary>
public enum LinkWriteOutcome
{
    /// <summary>The phone echoed the UDID back after writing it.</summary>
    Written,

    /// <summary>The phone is online and its identity directory does not exist.</summary>
    DirectoryMissing,

    /// <summary>The phone is offline, the UDID is unknown, or the write did not stick.</summary>
    Failed,
}

/// <summary>One in-flight backup request. Bytes move only after <see cref="LinkOffer.Accepted"/>.</summary>
public sealed record LinkOffer(
    string Id,
    string PairingId,
    string ArchiveId,
    string Direction,
    string Scope,
    int Count,
    DateTimeOffset Expires,
    bool Accepted);

/// <summary>A phone that has checked in recently.</summary>
public sealed record LinkPeer(string PairingId, string Name, string Platform, string? ArchiveId, bool DirectoryReady, DateTimeOffset Seen);

/// <summary>
/// Presence, identity binding, and the confirm-before-copy desk.
/// The computer never writes the iPhone; it only supplies the UDID for the app to store.
/// </summary>
public sealed class LinkDesk
{
    /// <summary>An unanswered request expires after this long.</summary>
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(2);

    /// <summary>A phone that has not checked in for this long is offline.</summary>
    public static readonly TimeSpan PresenceLifetime = TimeSpan.FromSeconds(7);

    private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkOffer> offers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> keyToArchive = new(StringComparer.Ordinal);
    private string? iphoneUdid;

    /// <summary>Remember the UDID read from the cable. This does not touch the phone.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void NoteIphoneUdid(string? udid)
    {
        if (DeviceSerialValidator.IsUsable(udid))
        {
            iphoneUdid = udid!.Trim();
        }
    }

    /// <summary>Classify a value the phone claims to have read.</summary>
    public static LinkIdentityState Classify(string? value, bool unreadable)
    {
        if (unreadable)
        {
            return LinkIdentityState.Unreadable;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return LinkIdentityState.Missing;
        }

        string trimmed = value.Trim();
        if (DeviceSerialGenerator.IsInternalId(trimmed) || DeviceSerialValidator.IsUsable(trimmed))
        {
            return LinkIdentityState.Ok;
        }

        return LinkIdentityState.Invalid;
    }

    /// <summary>Archive key for a qualified identity. Android codes keep the <c>android:</c> prefix.</summary>
    public static string? ArchiveDeviceId(string? platform, string? archiveId)
    {
        if (Classify(archiveId, false) != LinkIdentityState.Ok)
        {
            return null;
        }

        string trimmed = archiveId!.Trim();
        if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.StartsWith("android:", StringComparison.OrdinalIgnoreCase) ? trimmed : "android:" + trimmed;
        }

        return trimmed;
    }

    /// <summary>Put addresses on the phone's own subnet ahead of the others.</summary>
    public static IReadOnlyList<string> OrderAddresses(IReadOnlyList<string> hosts, string? phoneAddress)
    {
        string prefix = Subnet(phoneAddress);
        return hosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(host => prefix.Length > 0 && host.StartsWith(prefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(host => host, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Address to embed in a QR code: a private LAN address ahead of link-local and loopback.</summary>
    public static string PreferredHost(IReadOnlyList<string> addresses)
    {
        var list = addresses.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList();
        string? privateAddress = list.FirstOrDefault(IsPrivateLan);
        if (privateAddress is not null)
        {
            return privateAddress;
        }

        return list.FirstOrDefault(item => item != "127.0.0.1") ?? list.FirstOrDefault() ?? "127.0.0.1";
    }

    /// <summary>Record a check-in. A new Android with a confirmed-empty file receives one generated code.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public LinkHelloResult Hello(string pairingId, string name, string platform, string? archiveId, LinkIdentityState state, bool directoryReady, bool replaceBaseline, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);
        Expire(now);
        string? supplied = ArchiveDeviceId(platform, archiveId);
        string? bound = keyToArchive.TryGetValue(pairingId, out string? existing) ? existing : null;
        string? deliver = null;

        bool ios = string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase);
        if (ios && directoryReady && iphoneUdid is not null
            && (state is LinkIdentityState.Missing or LinkIdentityState.Invalid
                || (supplied is not null && !string.Equals(supplied, iphoneUdid, StringComparison.Ordinal))))
        {
            deliver = iphoneUdid;
            Bind(pairingId, deliver);
            bound = deliver;
        }
        else if (supplied is not null)
        {
            if (bound is not null && !string.Equals(bound, supplied, StringComparison.Ordinal))
            {
                peers[pairingId] = new Peer(pairingId, name, platform, bound, directoryReady, now, state);
                return new LinkHelloResult(bound, null, true);
            }

            Bind(pairingId, supplied);
            bound = supplied;
        }
        else if (bound is null && state == LinkIdentityState.Missing && string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase) && !replaceBaseline)
        {
            deliver = DeviceSerialGenerator.CreateInternalId();
            Bind(pairingId, "android:" + deliver);
            bound = "android:" + deliver;
        }
        else if (state is LinkIdentityState.Missing or LinkIdentityState.Invalid
            && string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)
            && directoryReady
            && iphoneUdid is not null)
        {
            deliver = iphoneUdid;
            Bind(pairingId, deliver);
            bound = deliver;
        }

        peers[pairingId] = new Peer(pairingId, name, platform, bound, directoryReady, now, state);
        return new LinkHelloResult(bound, deliver, false);
    }

    /// <summary>Refresh presence when an authenticated phone exchanges data.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void Touch(string pairingId, DateTimeOffset now)
    {
        if (peers.TryGetValue(pairingId, out var peer)) peers[pairingId] = peer with { Seen = now };
    }

    /// <summary>Phones seen inside <see cref="PresenceLifetime"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public IReadOnlyList<LinkPeer> Online(DateTimeOffset now) =>
        peers.Values
            .Where(peer => now - peer.Seen <= PresenceLifetime)
            .Select(peer => new LinkPeer(peer.PairingId, peer.Name, peer.Platform, peer.ArchiveId, peer.DirectoryReady, peer.Seen))
            .ToList();

    /// <summary>What the manual iPhone write button should say, from the latest check-in.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public LinkWriteOutcome IphoneWriteOutcome(string pairingId, DateTimeOffset now)
    {
        if (iphoneUdid is null || !peers.TryGetValue(pairingId, out Peer? peer) || now - peer.Seen > PresenceLifetime)
        {
            return LinkWriteOutcome.Failed;
        }

        if (!peer.DirectoryReady)
        {
            return LinkWriteOutcome.DirectoryMissing;
        }

        return string.Equals(peer.ArchiveId, iphoneUdid, StringComparison.Ordinal) && peer.State == LinkIdentityState.Ok
            ? LinkWriteOutcome.Written
            : LinkWriteOutcome.Failed;
    }

    /// <summary>Create a request. It carries no file bytes.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public LinkOffer? CreateOffer(string pairingId, string direction, string scope, int count, DateTimeOffset now)
    {
        Expire(now);
        if (!keyToArchive.TryGetValue(pairingId, out string? archiveId) || count < 0)
        {
            return null;
        }

        if (offers.Values.Any(offer => offer.PairingId == pairingId && (offer.Accepted || offer.Expires > now)))
        {
            return null;
        }

        var offer = new LinkOffer(Guid.NewGuid().ToString("N"), pairingId, archiveId, direction, scope, count, now.Add(OfferLifetime), false);
        offers[offer.Id] = offer;
        return offer;
    }

    /// <summary>Unanswered requests for every phone.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public IReadOnlyList<LinkOffer> PendingForAll(DateTimeOffset now)
    {
        Expire(now);
        return offers.Values.Where(offer => !offer.Accepted && offer.Expires > now).ToList();
    }

    /// <summary>Requests this phone still has to answer.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public IReadOnlyList<LinkOffer> Pending(string pairingId, DateTimeOffset now)
    {
        Expire(now);
        return offers.Values.Where(offer => offer.PairingId == pairingId && !offer.Accepted && offer.Expires > now).ToList();
    }

    /// <summary>Mark a request confirmed. Returns false when it is missing or expired.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public bool Accept(string offerId, string pairingId, DateTimeOffset now)
    {
        Expire(now);
        if (!offers.TryGetValue(offerId, out LinkOffer? offer) || offer.PairingId != pairingId || offer.Expires <= now)
        {
            return false;
        }

        offers[offerId] = offer with { Accepted = true };
        return true;
    }

    /// <summary>Drop a request without copying.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void Reject(string offerId, string pairingId)
    {
        if (offers.TryGetValue(offerId, out LinkOffer? offer) && offer.PairingId == pairingId)
        {
            offers.Remove(offerId);
        }
    }

    /// <summary>The confirmed transfer finished, so this phone may open another request.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void Complete(string pairingId)
    {
        foreach (string id in offers.Where(pair => pair.Value.PairingId == pairingId && pair.Value.Accepted).Select(pair => pair.Key).ToList())
        {
            offers.Remove(id);
        }
    }

    /// <summary>The request still on the desk, after unanswered ones past two minutes have been dropped.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public LinkOffer? Find(string offerId, DateTimeOffset now)
    {
        Expire(now);
        return offers.TryGetValue(offerId, out LinkOffer? offer) ? offer : null;
    }

    /// <summary>True after confirm, until <see cref="Complete"/>. The two-minute limit applies only before confirm.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public bool UploadAllowed(string pairingId, DateTimeOffset now)
    {
        Expire(now);
        return offers.Values.Any(offer => offer.PairingId == pairingId && offer.Accepted);
    }

    /// <summary>Archive id bound to this pairing key, if the identity step finished.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public string? ArchiveId(string pairingId) => keyToArchive.TryGetValue(pairingId, out string? id) ? id : null;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    private void Bind(string pairingId, string archiveId)
    {
        foreach (string old in keyToArchive.Where(pair => pair.Value == archiveId && pair.Key != pairingId).Select(pair => pair.Key).ToList())
        {
            keyToArchive.Remove(old);
            peers.Remove(old);
        }

        keyToArchive[pairingId] = archiveId;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    private void Expire(DateTimeOffset now)
    {
        foreach (string id in offers.Where(pair => pair.Value.Expires <= now && !pair.Value.Accepted).Select(pair => pair.Key).ToList())
        {
            offers.Remove(id);
        }
    }

    private static bool IsPrivateLan(string host) =>
        host.StartsWith("10.", StringComparison.Ordinal)
        || host.StartsWith("192.168.", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(host, @"^172\.(1[6-9]|2\d|3[0-1])\.");

    private static string Subnet(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "";
        }

        string[] parts = address.Trim().Split('.');
        return parts.Length == 4 ? string.Join('.', parts[..3]) + "." : "";
    }

    private sealed record Peer(string PairingId, string Name, string Platform, string? ArchiveId, bool DirectoryReady, DateTimeOffset Seen, LinkIdentityState State);
}

/// <summary>What the phone should store after a check-in.</summary>
/// <param name="ArchiveId">Identity bound on the computer.</param>
/// <param name="Deliver">Value the phone writes into its identity file, when one must be issued.</param>
/// <param name="Conflict">The phone presented a different identity than the one already bound to this key.</param>
public sealed record LinkHelloResult(string? ArchiveId, string? Deliver, bool Conflict);
