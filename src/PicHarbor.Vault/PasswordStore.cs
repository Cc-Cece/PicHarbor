// PicHarbor.Vault — 密码存储与校验
// 校验值存放在 %ProgramData%\PicHarbor\vault_credential，
// ACL 只给 SYSTEM 和 Administrators，普通用户不能改。

using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PicHarbor.Vault;

/// <summary>
/// 基于 PBKDF2-SHA512 的应用密码存储。
/// 存储格式（UTF-8 文本，两行）：
///   第 1 行：Base64(salt, 32 bytes)
///   第 2 行：Base64(hash, 64 bytes)
/// </summary>
public static class PasswordStore
{
    private const int SaltLength    = 32;
    private const int HashLength    = 64;   // SHA-512 输出
    private const int Iterations    = 200_000;
    private static readonly HashAlgorithmName Algo = HashAlgorithmName.SHA512;

    private static string StoreDir  => VaultStore.Directory;
    private static string StorePath => VaultStore.CredentialPath;

    /// <summary>是否已设置过密码。</summary>
    public static bool HasPassword => File.Exists(StorePath);

    /// <summary>
    /// 设置（或覆盖）密码。仅在服务以 SYSTEM 身份运行时调用。
    /// </summary>
    public static void SetPassword(string plaintext)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] hash = Pbkdf2(plaintext, salt);

        VaultStore.EnsureDirectory();
        string content = Convert.ToBase64String(salt) + "\n" + Convert.ToBase64String(hash);
        VaultStore.WriteProtectedFile(StorePath, content);
    }

    /// <summary>校验密码。文件不存在时返回 <c>false</c>。</summary>
    public static bool Verify(string plaintext)
    {
        if (!File.Exists(StorePath)) return false;
        try
        {
            string[] lines = File.ReadAllLines(StorePath, Encoding.UTF8);
            if (lines.Length < 2) return false;
            byte[] salt     = Convert.FromBase64String(lines[0]);
            byte[] expected = Convert.FromBase64String(lines[1]);
            byte[] actual   = Pbkdf2(plaintext, salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Pbkdf2(string plaintext, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(plaintext),
            salt,
            Iterations,
            Algo,
            HashLength);
}
