using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace PicHarbor.Vault;

internal static class ServiceImageStore
{
    internal static void DeleteDeployment(string image)
    {
        string basePath=Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"PicHarbor","Service"));
        string directory=Path.GetDirectoryName(Path.GetFullPath(image))!;
        if(!string.Equals(Path.GetDirectoryName(directory),basePath,StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory),@"\A[A-F0-9]{64}-[a-f0-9]{32}\z"))
            throw new InvalidDataException("Invalid staged service deployment.");
        if(!Directory.Exists(directory)) return;
        EnsurePrivateDirectory(directory);
        // Never follow links while deleting the specifically generated deployment tree.
        static void Remove(DirectoryInfo entry)
        {
            if((entry.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked staged directory.");
            foreach(var child in entry.EnumerateDirectories()) Remove(child);
            foreach(var file in entry.EnumerateFiles())
            {
                if((file.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked staged file.");
                file.Delete();
            }
            entry.Delete();
        }
        Remove(new DirectoryInfo(directory));
    }

    internal static string Prepare(string executable,string? directory=null,bool singleFileOnly=false)
    {
        AclEngine.EnableOwnershipPrivileges();
        string basePath=Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"PicHarbor","Service"));
        for(DirectoryInfo? parent=new(basePath);parent is not null;parent=parent.Parent)
            if(parent.Exists && (parent.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Service files cannot be installed through a linked directory.");
        if(directory is null) EnsurePrivateDirectory(Path.GetDirectoryName(basePath)!);
        EnsurePrivateDirectory(basePath);
        using var source=new FileStream(executable,FileMode.Open,FileAccess.Read,FileShare.Read);
        string version=Convert.ToHexString(SHA256.HashData(source));
        string target=Path.Combine(basePath,version+"-"+Guid.NewGuid().ToString("N"));
        EnsurePrivateDirectory(target);
        EnsurePrivateDirectory(Path.Combine(basePath,"BundleCache"));
        var sourceDirectory=Path.GetDirectoryName(executable)!;
        foreach(string file in Directory.EnumerateFiles(sourceDirectory))
        {
            string name=Path.GetFileName(file);
            if(singleFileOnly && !string.Equals(file,executable,StringComparison.OrdinalIgnoreCase)) continue;
            if(!string.Equals(file,executable,StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".deps.json",StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".runtimeconfig.json",StringComparison.OrdinalIgnoreCase)) continue;
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked service dependencies are not allowed.");
            string destination=Path.GetFullPath(Path.Combine(target,name));
            if(!destination.StartsWith(basePath+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid service deployment path.");
            if(File.Exists(destination) && (File.GetAttributes(destination)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked service destinations are not allowed.");
            if(!string.Equals(file,destination,StringComparison.OrdinalIgnoreCase)) File.Copy(file,destination,true);
            var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null));new FileInfo(destination).SetAccessControl(acl);
        }
        return Path.Combine(target,Path.GetFileName(executable));
    }

    private static void EnsurePrivateDirectory(string path)
    {
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var administrators=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        var directory=new DirectoryInfo(path);
        if(directory.Exists)
        {
            if((directory.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked service directories are not allowed.");
            var existing=directory.GetAccessControl();
            var owner=existing.GetOwner(typeof(SecurityIdentifier));
            if(!existing.AreAccessRulesProtected || (!system.Equals(owner) && !administrators.Equals(owner)) ||
                existing.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r=>r.AccessControlType==AccessControlType.Allow && !r.IdentityReference.Equals(system) && !r.IdentityReference.Equals(administrators)))
                throw new InvalidDataException("The service directory was created with untrusted permissions. Administrator repair is required.");
            return;
        }
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);security.SetOwner(system);
        foreach(var sid in new[]{system,administrators})
            security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        directory.Create(security);
    }
}
