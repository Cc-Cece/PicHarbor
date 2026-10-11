// PicHarbor.Vault — 登记根持久化
// 已登记的归档根必须跨服务重启存活：服务启动第一步要按这份清单做收回，
// 否则重启后 _roots 为空、收回无对象、照片裸露。

using System.IO;
using System.Text.Json;

namespace PicHarbor.Vault;

/// <summary>
/// 把已登记的根路径持久化到 <c>%ProgramData%\PicHarbor\vault_roots.json</c>。
/// 文件 ACL 只给 SYSTEM + Administrators，普通用户改不了。
/// </summary>
public static class RootStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>读取已登记的根路径列表。文件缺失或损坏时返回空列表。</summary>
    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(VaultStore.RootsPath)) return [];

            string json = File.ReadAllText(VaultStore.RootsPath);
            var roots = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            return roots is null
                ? []
                : roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] Load roots failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>覆盖写入登记根清单。</summary>
    public static void Save(IEnumerable<string> roots)
    {
        try
        {
            string json = JsonSerializer.Serialize(roots.ToList(), JsonOptions);
            VaultStore.WriteProtectedFile(VaultStore.RootsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] Save roots failed: {ex.Message}");
        }
    }

    /// <summary>更新密码文件时同样收紧 ACL。</summary>
    public static void HardenCredential()
    {
        try { VaultStore.HardenFile(VaultStore.CredentialPath); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Vault] Harden credential failed: {ex.Message}");
        }
    }
}
