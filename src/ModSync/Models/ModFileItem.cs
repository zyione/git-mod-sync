namespace ModSync.Models;

/// <summary>
/// Represents a mod file item found in the mods directory or internal repository.
/// </summary>
public class ModFileItem
{
    /// <summary>
    /// Relative path within the mods or repository folder (e.g. "sodium-0.5.8.jar").
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Absolute full path on the local filesystem.
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// SHA-256 hex digest of the file contents.
    /// </summary>
    public string Sha256Hash { get; set; } = string.Empty;

    public override string ToString() => RelativePath;
}
