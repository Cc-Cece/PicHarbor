// PicHarbor.Vault — TOTP 引擎（RFC 6238）
// HMAC-SHA1 / 6 位验证码 / 30 秒时间步长，容许 ±1 个时间步的时钟偏差。
// 纯算法，不碰磁盘；密钥持久化与防重放/限流状态见 TotpStore。

using System.Security.Cryptography;
using System.Text;

namespace PicHarbor.Vault;

/// <summary>
/// RFC 6238 兼容的 TOTP 生成与校验。
/// </summary>
public static class TotpEngine
{
    /// <summary>时间步长（秒）。</summary>
    public const int StepSeconds = 30;

    /// <summary>验证码位数。</summary>
    public const int Digits = 6;

    /// <summary>允许的时间步偏差（±2 步 ≈ ±60 秒），用于容忍客户端时钟漂移。</summary>
    public const int AllowedDriftSteps = 2;

    /// <summary>生成一个随机密钥（20 字节 = 160 bit，RFC 6238 推荐的最小长度）。</summary>
    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>
    /// 计算指定时间步的验证码。
    /// </summary>
    /// <param name="secret">共享密钥。</param>
    /// <param name="unixTimeSeconds">Unix 时间戳（秒）。</param>
    public static string ComputeCode(byte[] secret, long unixTimeSeconds)
        => ComputeCodeForStep(secret, unixTimeSeconds / StepSeconds);

    /// <summary>计算指定时间步的验证码。</summary>
    public static string ComputeCodeForStep(byte[] secret, long step)
    {
        byte[] counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);   // 大端

        byte[] hash = HMACSHA1.HashData(secret, counter);

        int offset = hash[^1] & 0x0F;
        int binary =
            ((hash[offset]     & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8)  |
             (hash[offset + 3] & 0xFF);

        int otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString(new string('0', Digits));
    }

    /// <summary>
    /// 校验验证码是否在容许的偏差窗口内有效。
    /// 命中时通过 <paramref name="matchedStep"/> 返回匹配的时间步（供防重放使用）。
    /// </summary>
    /// <param name="secret">共享密钥。</param>
    /// <param name="code">用户输入的 6 位码。</param>
    /// <param name="nowUnixSeconds">当前 Unix 时间戳（秒）。</param>
    /// <param name="matchedStep">匹配到的时间步；未匹配时为 -1。</param>
    public static bool Verify(byte[] secret, string code, long nowUnixSeconds, out long matchedStep)
    {
        matchedStep = -1;
        if (string.IsNullOrWhiteSpace(code)) return false;

        string normalized = code.Trim().Replace(" ", "");
        if (normalized.Length != Digits || !normalized.All(char.IsAsciiDigit)) return false;

        long currentStep = nowUnixSeconds / StepSeconds;

        for (int drift = -AllowedDriftSteps; drift <= AllowedDriftSteps; drift++)
        {
            long step = currentStep + drift;
            if (step < 0) continue;

            string expected = ComputeCodeForStep(secret, step);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(normalized)))
            {
                matchedStep = step;
                return true;
            }
        }

        return false;
    }

    /// <summary>把密钥编码为 Base32（供 otpauth URI / 人工录入使用）。</summary>
    public static string ToBase32(byte[] secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int bits = 0, value = 0;

        foreach (byte b in secret)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(value >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(alphabet[(value << (5 - bits)) & 0x1F]);

        return sb.ToString();
    }

    /// <summary>
    /// 构造 Google Authenticator 兼容的 otpauth:// URI。
    /// </summary>
    public static string BuildOtpAuthUri(string secretBase32, string account, string issuer)
    {
        string label = Uri.EscapeDataString($"{issuer}:{account}");
        string paramsStr =
            $"secret={secretBase32}" +
            $"&issuer={Uri.EscapeDataString(issuer)}" +
            $"&algorithm=SHA1" +
            $"&digits={Digits}" +
            $"&period={StepSeconds}";
        return $"otpauth://totp/{label}?{paramsStr}";
    }
}
