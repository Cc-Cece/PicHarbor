namespace PicHarbor.Core.Storage;

/// <summary>Exclusive cross-process archive writer lease. Reading remains available.</summary>
public static class LibraryWriteCoordinator
{
    /// <summary>Acquires a lease; an existing writer causes an IOException.</summary>
    public static IDisposable Acquire(string root, bool reclaimAbandonedWriter = false)
    {
        string metadata = Path.Combine(root, ".picharbor");
        Directory.CreateDirectory(metadata);
        string path = ArchivePathResolver.Resolve(root, ".picharbor/writer.lock");
        var stream=new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { return new WriterLease(stream,reclaimAbandonedWriter); }
        catch { stream.Dispose();throw; }
    }

    /// <summary>Acquires all online members in a stable order to exclude concurrent cross-volume mutation.</summary>
    public static IDisposable AcquireLibrary(string root)
    {
        var roots = File.Exists(Path.Combine(root,".picharbor","library.json"))
            ? new LibraryCatalog(root).Members.Where(m => Directory.Exists(m.Root)).OrderBy(m => m.StorageId,StringComparer.Ordinal).Select(m => m.Root).ToList()
            : [root];
        return AcquireRoots(roots);
    }

    internal static IDisposable AcquireRoots(IEnumerable<string> roots)
    {
        var leases = new List<IDisposable>();
        try { foreach (string member in roots.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)) leases.Add(Acquire(member)); return new LeaseSet(leases); }
        catch { foreach(var lease in leases) lease.Dispose(); throw; }
    }

    /// <summary>Explicitly reclaims an abandoned cross-computer writer after all members are reconnected.</summary>
    public static void ReclaimLibraryWriter(LibraryCatalog catalog)
    {
        if(catalog.Members.Any(m=>!Directory.Exists(m.Root))) throw new IOException("Reconnect all member disks before reclaiming an abandoned writer.");
        var leases=new List<IDisposable>();
        try { foreach(var member in catalog.Members.OrderBy(m=>m.Root,StringComparer.OrdinalIgnoreCase)) leases.Add(Acquire(member.Root,true)); }
        finally { foreach(var lease in leases) lease.Dispose(); }
    }

    private sealed record WriterStamp(string Machine, int Process, bool Active);
    private static string MachineIdentity()
    {
        string identity=Environment.MachineName;
        if(OperatingSystem.IsWindows())
        {
            using var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            identity=key?.GetValue("MachineGuid") as string ?? identity;
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
    }
    private sealed class WriterLease : IDisposable
    {
        private readonly FileStream stream;
        private readonly string machine=MachineIdentity();
        public WriterLease(FileStream stream,bool reclaim)
        {
            this.stream=stream;
            if(stream.Length>0)
            {
                using var reader=new StreamReader(stream,leaveOpen:true);
                string previous=reader.ReadToEnd();
                if(!string.IsNullOrWhiteSpace(previous))
                {
                    var owner=System.Text.Json.JsonSerializer.Deserialize<WriterStamp>(previous);
                    if(owner is {Active:true} && owner.Machine!=machine && !reclaim)
                        throw new IOException("A writer from another computer was interrupted. End its work and reclaim the library writer before modifying this archive.");
                }
            }
            Write(true);
        }
        private void Write(bool active)
        {
            stream.Position=0;stream.SetLength(0);
            byte[] bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new WriterStamp(machine,Environment.ProcessId,active));
            stream.Write(bytes);stream.Flush(flushToDisk:true);
        }
        public void Dispose() { try { Write(false); } finally { stream.Dispose(); } }
    }

    private sealed class LeaseSet(List<IDisposable> leases) : IDisposable
    {
        public void Dispose() { foreach(var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }
}
