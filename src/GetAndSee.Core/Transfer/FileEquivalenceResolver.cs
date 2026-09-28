using System.Security.Cryptography;

namespace GetAndSee.Core.Transfer;

/// <summary>
/// Resolves file equivalence between a source and a target destination.
/// Follows low-cost checks first (existence, size, mtime) and falls back to optional targeted hash validation.
/// Returns Equivalent, NotEquivalent, or Unknown. Unknown MUST NEVER be assumed equivalent.
/// </summary>
public sealed class FileEquivalenceResolver
{
    /// <summary>
    /// Compares source identity with target identity.
    /// </summary>
    public static FileEquivalenceResult Resolve(
        FileIdentity source,
        FileIdentity target,
        bool allowHashCheck = false)
    {
        // Different size => Definitely not equivalent
        if (source.Size != target.Size)
        {
            return FileEquivalenceResult.NotEquivalent;
        }

        // If both hashes are present, compare them
        if (!string.IsNullOrWhiteSpace(source.Sha256) && !string.IsNullOrWhiteSpace(target.Sha256))
        {
            return string.Equals(source.Sha256, target.Sha256, StringComparison.OrdinalIgnoreCase)
                ? FileEquivalenceResult.Equivalent
                : FileEquivalenceResult.NotEquivalent;
        }

        // Check modified times if available
        if (source.LastModified.HasValue && target.LastModified.HasValue)
        {
            double diffSeconds = Math.Abs((target.LastModified.Value - source.LastModified.Value).TotalSeconds);
            if (diffSeconds < 2.0)
            {
                return FileEquivalenceResult.Equivalent;
            }

            // Size matches but modified time differs.
            if (allowHashCheck && File.Exists(source.Path) && File.Exists(target.Path))
            {
                string srcHash = ComputeFileSha256(source.Path);
                string tgtHash = ComputeFileSha256(target.Path);
                return string.Equals(srcHash, tgtHash, StringComparison.OrdinalIgnoreCase)
                    ? FileEquivalenceResult.Equivalent
                    : FileEquivalenceResult.NotEquivalent;
            }

            return FileEquivalenceResult.NotEquivalent;
        }

        // Size matches but mtime is missing/unspecified
        if (allowHashCheck && File.Exists(source.Path) && File.Exists(target.Path))
        {
            string srcHash = ComputeFileSha256(source.Path);
            string tgtHash = ComputeFileSha256(target.Path);
            return string.Equals(srcHash, tgtHash, StringComparison.OrdinalIgnoreCase)
                ? FileEquivalenceResult.Equivalent
                : FileEquivalenceResult.NotEquivalent;
        }

        return FileEquivalenceResult.Unknown;
    }

    /// <summary>
    /// Evaluates equivalence for two local files by inspecting file info.
    /// </summary>
    public static FileEquivalenceResult ResolveLocalFile(string sourceAbsPath, string targetAbsPath, bool allowHashCheck = false)
    {
        if (string.IsNullOrWhiteSpace(sourceAbsPath) || string.IsNullOrWhiteSpace(targetAbsPath))
        {
            return FileEquivalenceResult.NotEquivalent;
        }

        var srcInfo = new FileInfo(sourceAbsPath);
        var tgtInfo = new FileInfo(targetAbsPath);

        if (!srcInfo.Exists || !tgtInfo.Exists)
        {
            return FileEquivalenceResult.NotEquivalent;
        }

        var sourceId = new FileIdentity(srcInfo.FullName, srcInfo.Length, srcInfo.LastWriteTimeUtc);
        var targetId = new FileIdentity(tgtInfo.FullName, tgtInfo.Length, tgtInfo.LastWriteTimeUtc);

        return Resolve(sourceId, targetId, allowHashCheck);
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }
}
