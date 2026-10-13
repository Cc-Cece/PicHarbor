using PicHarbor.Core.Journal;

namespace PicHarbor.Core.Storage;

/// <summary>Deterministic export names for distinct content, preserving paired component stems.</summary>
public static class MediaExportPaths
{
    /// <summary>Maps stable selection identities to safe, noncolliding relative export paths.</summary>
    public static IReadOnlyDictionary<string,string> Build(IReadOnlyList<ManifestEntry> media, Func<ManifestEntry,string> target)
    {
        var parent=media.ToDictionary(m=>m.SelectionKey,m=>m.SelectionKey,StringComparer.OrdinalIgnoreCase);
        string Find(string key) { while(parent[key]!=key) {parent[key]=parent[parent[key]];key=parent[key];}return key; }
        void Join(string first,string second) {var a=Find(first);var b=Find(second);if(a!=b)parent[b]=a;}
        foreach(var item in media)
        foreach(string related in item.RelatedMediaIds)
            if(parent.ContainsKey("#media/"+related)) Join(item.SelectionKey,"#media/"+related);
        foreach(var group in media.GroupBy(m=>(m.ArchiveRoot ?? "")+"|"+Path.GetDirectoryName(m.DestPath)+"|"+Path.GetFileNameWithoutExtension(m.DestPath),StringComparer.OrdinalIgnoreCase))
        foreach(var component in group.Skip(1)) Join(group.First().SelectionKey,component.SelectionKey);
        string Group(ManifestEntry item) => Find(item.SelectionKey);
        var groupIds=media.GroupBy(Group,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,
            g=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                string.Join("|",g.Select(m=>m.SelectionKey).Order(StringComparer.Ordinal)))))[..12],StringComparer.OrdinalIgnoreCase);
        var proposed=media.Select(m=>(Media:m,Target:target(m).Replace('\\','/'))).ToList();
        var collisions=proposed.GroupBy(p=>p.Target,StringComparer.OrdinalIgnoreCase).Where(g=>g.Select(p=>p.Media.SelectionKey).Distinct().Count()>1)
            .SelectMany(g=>g.Select(p=>Group(p.Media))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var pair in proposed.OrderBy(p=>p.Media.SelectionKey,StringComparer.Ordinal))
        {
            string path=pair.Target;
            if(collisions.Contains(Group(pair.Media)))
                path=Path.Combine(Path.GetDirectoryName(path) ?? "",Path.GetFileNameWithoutExtension(path)+"_"+groupIds[Group(pair.Media)]+Path.GetExtension(path)).Replace('\\','/');
            if(!used.Add(path)) throw new InvalidDataException("Export file names conflict. No files were exported.");
            result.Add(pair.Media.SelectionKey,path);
        }
        return result;
    }
}
