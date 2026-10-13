using System.Security.Cryptography;
using System.Text;

namespace PicHarbor.Core.Util;

/// <summary>Current-user protection for rebuildable local metadata; portable archive data stays on member disks.</summary>
internal static class UserProtectedFile
{
    internal static string Read(string path)
    {
        byte[] bytes=File.ReadAllBytes(path);
        if(OperatingSystem.IsWindows() && bytes.Length>0 && bytes[0]!=(byte)'{')
            bytes=ProtectedData.Unprotect(bytes,null,DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
    internal static void Write(string path,string text)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(text);
        if(OperatingSystem.IsWindows()) bytes=ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllBytes(temporary,bytes);File.Move(temporary,path,overwrite:true);
    }
}
