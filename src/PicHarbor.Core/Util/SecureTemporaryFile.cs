using System.Security.AccessControl;
using System.Security.Principal;

namespace PicHarbor.Core.Util;

internal static class SecureTemporaryFile
{
    internal static string CreatePath(string prefix)
    {
        string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PicHarbor","TemporaryAuth");
        Directory.CreateDirectory(directory);
        if(OperatingSystem.IsWindows())
        {
            using var identity=WindowsIdentity.GetCurrent();
            var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{identity.User!,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
        }
        return Path.Combine(directory,prefix+"_"+Guid.NewGuid().ToString("N")+".json");
    }
}
