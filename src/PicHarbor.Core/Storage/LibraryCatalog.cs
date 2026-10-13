using System.Text.Json;
using PicHarbor.Core.Journal;

namespace PicHarbor.Core.Storage;

/// <summary>Portable member registry and rebuildable offline media index.</summary>
public sealed class LibraryCatalog
{
    private Dictionary<string,HashSet<string>>? relatedIndex;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string root;
    private CatalogState state;
    /// <summary>A copied archive is browsable but not eligible for automatic maintenance.</summary>
    public bool IsUnincludedCopy { get; private set; }

    private sealed class CatalogState
    {
        public string LibraryId { get; set; } = string.Empty;
        public List<LibraryMember> Members { get; set; } = [];
        public List<LibraryMedia> Media { get; set; } = [];
        public Dictionary<string,List<DeviceRecord>> Devices { get; set; } = new();
        public Dictionary<string,RunsSummary> Runs { get; set; } = new();
        public Dictionary<string,List<BackupSessionRecord>> Sessions { get; set; } = new();
        public Dictionary<string,List<BackupHistoryItemRecord>> Histories { get; set; } = new();
        public Dictionary<string,string> PrimaryCopies { get; set; } = new();
        public Dictionary<string,HashSet<string>> ComponentGroups { get; set; } = new();
        public Dictionary<string,List<SourceIdentity>> Sources { get; set; } = new();
        public Dictionary<string,PicHarbor.Core.Organize.OrganizeScheme> PendingLayouts { get; set; } = new();
    }

    /// <summary>Loads the member manifest. A fresh catalog contains only the requested archive.</summary>
    public LibraryCatalog(string archiveRoot)
    {
        root = PicHarbor.Core.Util.LongPath.NormalizeIdentityPath(archiveRoot);
        string manifest = Path.Combine(root, ".picharbor", "library.json");
        string? json = File.Exists(manifest) ? File.ReadAllText(manifest) : File.Exists(CachePath(root)) ? PicHarbor.Core.Util.UserProtectedFile.Read(CachePath(root)) : null;
        if (!File.Exists(TransferJournal.ResolveDatabasePath(root)))
        {
            state = json is null ? throw new FileNotFoundException("No library index available.") : JsonSerializer.Deserialize<CatalogState>(json,JsonOptions)!;
            return;
        }
        using var journal = TransferJournal.OpenReadOnly(root);
        var identity = journal.GetLibraryIdentity();
        state = json is not null
            ? JsonSerializer.Deserialize<CatalogState>(json, JsonOptions) ?? throw new InvalidDataException("Invalid library registry.")
            : new() { LibraryId = identity.LibraryId };
        if (state.LibraryId != identity.LibraryId) throw new InvalidDataException("Library identity conflict.");
        var known = state.Members.FindIndex(m => m.StorageId == identity.StorageId);
        if (known >= 0 && !SamePath(state.Members[known].Root, root) && File.Exists(TransferJournal.ResolveDatabasePath(state.Members[known].Root)))
        {
            using var original=TransferJournal.OpenReadOnly(state.Members[known].Root);
            if(original.GetLibraryIdentity()==identity)
            {
                IsUnincludedCopy=true;
                state.Members=[new(identity.LibraryId,identity.StorageId,root,false)];
                state.Media=state.Media.Where(m=>m.StorageId==identity.StorageId).Select(m=>m with{Root=root}).ToList();
                return;
            }
        }
        if (known >= 0) state.Members[known] = state.Members[known] with { Root = root };
        else state.Members.Add(new(identity.LibraryId, identity.StorageId, root));
    }

    /// <summary>Known member locations, including disconnected members.</summary>
    public IReadOnlyList<LibraryMember> Members => state.Members;

    /// <summary>Adds an explicitly authorized directory; copied directories receive a new storage identity.</summary>
    public void Include(string archiveRoot)
    {
        string full = PicHarbor.Core.Util.LongPath.NormalizeIdentityPath(archiveRoot);
        using var libraryLease = LibraryWriteCoordinator.AcquireRoots(state.Members.Where(m=>Directory.Exists(m.Root)).Select(m=>m.Root));
        using var lease = state.Members.Any(m=>SamePath(m.Root,full)) ? null : LibraryWriteCoordinator.Acquire(full);
        using var journal = TransferJournal.Open(full);
        var identity = journal.GetLibraryIdentity();
        bool copied = state.Members.Any(m => m.StorageId == identity.StorageId && !SamePath(m.Root, full));
        journal.JoinLibrary(state.LibraryId, copied);
        identity = journal.GetLibraryIdentity();
        if (!state.Members.Any(m => SamePath(m.Root, full))) state.Members.Add(new(state.LibraryId, identity.StorageId, full));
        ReadMedia();
        Save();
    }

    /// <summary>Rebuilds online entries and preserves offline entries without treating disconnection as deletion.</summary>
    public IReadOnlyList<LibraryMedia> ReadMedia()
    {
        var result = new List<LibraryMedia>();
        foreach (var member in state.Members)
        {
            try
            {
                using var journal = TransferJournal.OpenReadOnly(member.Root);
                if (journal.GetLibraryIdentity() != new LibraryIdentity(state.LibraryId, member.StorageId))
                    throw new InvalidDataException("Member identity conflict.");
                result.AddRange(journal.ReadLibraryMedia(member.Root));
                state.Devices[member.StorageId]=journal.ReadDevices().ToList();
                state.Runs[member.StorageId]=journal.ReadRunsSummary();
                state.Sources[member.StorageId]=journal.ReadSourceIdentities().ToList();
                state.Sessions[member.StorageId]=journal.ReadBackupSessions(string.Empty)
                    .Select(s=>s with {StorageId=member.StorageId,ArchiveRoot=member.Root}).ToList();
                foreach(string key in state.Histories.Keys.Where(k=>k.StartsWith(member.StorageId+":",StringComparison.Ordinal)).ToList()) state.Histories.Remove(key);
                foreach(var session in state.Sessions[member.StorageId])
                    state.Histories[member.StorageId+":"+session.Id]=journal.ReadBackupHistoryItems(session.Id).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                var status = Directory.Exists(member.Root) ? ReplicaState.Inaccessible : ReplicaState.Offline;
                result.AddRange(state.Media.Where(m => m.StorageId == member.StorageId).Select(m => m with { State = status }));
            }
        }
        state.Media = result;
        foreach(var memberSources in state.Sources)
        foreach(var group in memberSources.Value.Where(s=>s.DeviceId!="local-edit").GroupBy(s=>
            (string.IsNullOrEmpty(s.DeviceId) ? memberSources.Key : s.DeviceId)+"|"+Path.GetDirectoryName(s.SourcePath.Replace('\\','/'))+"|"+Path.GetFileNameWithoutExtension(s.SourcePath)))
        {
            if(!state.ComponentGroups.TryGetValue(group.Key,out var hashes)) state.ComponentGroups[group.Key]=hashes=[];
            hashes.UnionWith(group.Select(s=>s.MediaId));
        }
        relatedIndex=null;
        return result;
    }

    /// <summary>Writes the catalog atomically to available members, preserving portability.</summary>
    public void Save()
    {
        if(IsUnincludedCopy) throw new InvalidOperationException("Include this copied archive before changing its catalog.");
        string json = JsonSerializer.Serialize(state, JsonOptions);
        foreach (var member in state.Members)
        {
            string cache = CachePath(member.Root);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            PicHarbor.Core.Util.UserProtectedFile.Write(cache,json);
        }
        foreach (var member in state.Members)
        {
            if (!Directory.Exists(member.Root)) continue;
            using (var journal=TransferJournal.OpenReadOnly(member.Root))
                if (journal.GetLibraryIdentity() != new LibraryIdentity(state.LibraryId,member.StorageId))
                    throw new InvalidDataException("Member identity conflict; registry was not written.");
            string target = ArchivePathResolver.Resolve(member.Root, ".picharbor/library.json");
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, target, overwrite: true);
        }
    }

    /// <summary>Checks whether disconnected member information is available locally.</summary>
    public static bool HasCachedCatalog(string root) => File.Exists(CachePath(root));

    private static string CachePath(string root) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PicHarbor","Catalog",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(PicHarbor.Core.Util.LongPath.NormalizeIdentityPath(root).ToUpperInvariant()))) + ".json");

    /// <summary>Returns one logical display entry per content digest, preferring available media.</summary>
    public IReadOnlyList<LibraryMedia> ReadLogicalMedia() => ReadMedia()
        .Where(m => m.State != ReplicaState.Recycled)
        .GroupBy(m => m.Sha256 is null ? m.StorageId + ":" + m.RelativePath : m.Size + ":" + m.Sha256)
        .Where(g=>g.Any(m=>m.State is not ReplicaState.Missing and not ReplicaState.Changed))
        .Select(g => g.OrderBy(m => m.State == ReplicaState.Available ? 0 : 1).ThenBy(m=>IsPreferred(m) ? 0 : 1).ThenBy(m => m.StorageId, StringComparer.Ordinal).First()).ToList();

    internal bool IsPreferred(LibraryMedia media) => state.PrimaryCopies.GetValueOrDefault(media.Size+":"+media.Sha256)?.StartsWith(media.StorageId+":",StringComparison.Ordinal)==true;
    internal void SetPreferred(LibraryMedia media) => state.PrimaryCopies[media.Size+":"+media.Sha256]=media.StorageId+":"+media.RelativePath;
    /// <summary>Content relationships remain valid when redundant components move to different members.</summary>
    public IReadOnlyList<string> RelatedMedia(string mediaId)
    {
        if(relatedIndex is null)
        {
            relatedIndex=new();
            foreach(var group in state.ComponentGroups.Values.Where(g=>g.Count>1))
            foreach(string id in group)
            {
                if(!relatedIndex.TryGetValue(id,out var related)) relatedIndex[id]=related=[];
                related.UnionWith(group.Where(other=>other!=id));
            }
        }
        return relatedIndex.TryGetValue(mediaId,out var result) ? result.ToList() : [];
    }
    internal IReadOnlyDictionary<string,PicHarbor.Core.Organize.OrganizeScheme> PendingLayouts => state.PendingLayouts;
    internal void QueueLayout(PicHarbor.Core.Organize.OrganizeScheme scheme) { foreach(var member in state.Members) state.PendingLayouts[member.StorageId]=scheme; }
    internal void CompleteLayout(string storageId) => state.PendingLayouts.Remove(storageId);

    /// <summary>Queries logical media while preserving the original member location.</summary>
    public IReadOnlyList<ManifestSearchRow> ReadSearchRows()
    {
        var rows = new List<ManifestSearchRow>();
        var metadata = new Dictionary<string,ManifestSearchRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in state.Members)
        {
            if (!File.Exists(TransferJournal.ResolveDatabasePath(member.Root))) continue;
            try
            {
                using var journal = TransferJournal.OpenReadOnly(member.Root);
                if (journal.GetLibraryIdentity() != new LibraryIdentity(state.LibraryId,member.StorageId)) continue;
                foreach (var row in journal.ReadSearchRows()) metadata[member.StorageId + ":" + row.RelativePath] = row;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
        }
        foreach (var media in ReadLogicalMedia())
        {
            var row = metadata.GetValueOrDefault(media.StorageId + ":" + media.RelativePath) ??
                new ManifestSearchRow(media.RelativePath,media.CapturedAt,media.Size,null,null,null,null);
            rows.Add(row with { ArchiveRoot = media.Root, StorageId = media.StorageId, Availability = media.State, MediaId=media.Sha256,
                RelatedMediaIds=media.Sha256 is null ? [] : RelatedMedia(media.Sha256) });
        }
        return rows;
    }

    /// <summary>Returns cached device metadata for online and offline members.</summary>
    public IReadOnlyList<DeviceRecord> ReadDevices() => state.Devices.Values.SelectMany(d=>d).DistinctBy(d=>d.Udid).ToList();

    /// <summary>Returns the most recent known run across member disks.</summary>
    public RunsSummary ReadRunsSummary() => state.Runs.Values.OrderByDescending(r=>r.LatestRunAt).FirstOrDefault() ?? new(0,null,null,0,0,0,TimeSpan.Zero);

    /// <summary>Reads portable session history, including cached sessions on disconnected disks.</summary>
    public IReadOnlyList<BackupSessionRecord> ReadBackupSessions(string deviceUid) => state.Sessions.Values.SelectMany(s=>s)
        .Where(s=>s.DeviceUid==deviceUid).OrderByDescending(s=>s.StartedAt).ToList();

    /// <summary>Resolves session items by member identity, avoiding collisions between SQLite row IDs.</summary>
    public IReadOnlyList<BackupHistoryItemRecord> ReadBackupHistory(string storageId,long sessionId)
        => state.Histories.GetValueOrDefault(storageId+":"+sessionId) ?? [];

    /// <summary>Returns preserved source provenance, including disconnected member records.</summary>
    public IReadOnlyList<SourceIdentity> ReadSources() => state.Sources.Values.SelectMany(s=>s).Distinct().ToList();

    private static bool SamePath(string a, string b) => string.Equals(PicHarbor.Core.Util.LongPath.NormalizeIdentityPath(a), PicHarbor.Core.Util.LongPath.NormalizeIdentityPath(b), StringComparison.OrdinalIgnoreCase);
}
