using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PicHarbor.Vault;

/// <summary>Preserves original owner and access rules before taking control of an archive.</summary>
internal static class AclSnapshotStore
{
    private static string SnapshotPath(string root) => Path.Combine(VaultStore.Directory,
        "acl-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))) + ".json");

    internal static void Capture(string root)
    {
        AclEngine.EnableOwnershipPrivileges();
        string path = SnapshotPath(root);
        if (File.Exists(path)) return;
        var snapshots = new Dictionary<string,string>();
        foreach (string entry in Entries(root))
        {
            FileSystemSecurity acl = Directory.Exists(entry) ? new DirectoryInfo(entry).GetAccessControl() : new FileInfo(entry).GetAccessControl();
            snapshots[Path.GetRelativePath(root, entry)] = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        }
        VaultStore.WriteProtectedFile(path, JsonSerializer.Serialize(snapshots));
    }

    internal static void Restore(string root, SecurityIdentifier user)
    {
        AclEngine.EnableOwnershipPrivileges();
        if(!PathWhitelist.IsAllowed(root,out string reason)) throw new InvalidDataException(reason);
        // Offline/missing roots cannot count as successfully restored.
        _ = Directory.GetFileSystemEntries(root);
        string path = SnapshotPath(root);
        if (!File.Exists(path)) { AclEngine.Restore(root, user); return; }
        var snapshots = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid ACL recovery data.");
        foreach (var snapshot in snapshots.OrderByDescending(s => s.Key.Length))
        {
            string full = snapshot.Key == "." ? root : Path.GetFullPath(Path.Combine(root, snapshot.Key));
            if (snapshot.Key != "." && !full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid ACL recovery path.");
            for(DirectoryInfo? current=new(full);current is not null;current=current.Parent)
            {
                try
                {
                    if((File.GetAttributes(current.FullName)&FileAttributes.ReparsePoint)!=0)
                        throw new InvalidDataException("ACL recovery cannot traverse a linked directory.");
                }
                catch(FileNotFoundException) { }
                catch(DirectoryNotFoundException) { }
            }
            if (Directory.Exists(full))
            {
                var acl = new DirectorySecurity(); acl.SetSecurityDescriptorSddlForm(snapshot.Value);
                new DirectoryInfo(full).SetAccessControl(acl);
            }
            else if (File.Exists(full))
            {
                var acl = new FileSecurity(); acl.SetSecurityDescriptorSddlForm(snapshot.Value);
                new FileInfo(full).SetAccessControl(acl);
            }
        }
        // Keep the recovery descriptor until the registration change is durable.
    }

    internal static void Forget(string root) => File.Delete(SnapshotPath(root));

    internal static IEnumerable<string> Entries(string root)
    {
        yield return root;
        foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            yield return entry;
    }
}
