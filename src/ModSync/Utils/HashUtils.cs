using System.Security.Cryptography;

namespace ModSync.Utils;

/// <summary>
/// Cryptographic hashing utilities for verifying mod file integrity and detecting changes.
/// </summary>
public static class HashUtils
{
    /// <summary>
    /// Computes the SHA-256 hash of a file efficiently using streaming.
    /// </summary>
    public static string ComputeSha256(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found for hash calculation", filePath);
        }

        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Checks whether two files have identical content by comparing size and SHA-256 hash.
    /// </summary>
    public static bool FilesMatch(string fileA, string fileB)
    {
        if (!File.Exists(fileA) || !File.Exists(fileB))
            return false;

        var infoA = new FileInfo(fileA);
        var infoB = new FileInfo(fileB);

        // Quick check: if sizes are different, files cannot be identical
        if (infoA.Length != infoB.Length)
            return false;

        var hashA = ComputeSha256(fileA);
        var hashB = ComputeSha256(fileB);

        return string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
    }
}
