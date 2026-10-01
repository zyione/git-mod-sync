namespace ModSync.Models;

/// <summary>
/// Status and version comparison information for the local Fabric Loader installation.
/// </summary>
public class FabricStatusInfo
{
    /// <summary>
    /// Whether Fabric Loader version enforcement is configured in config.json.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// The target/required Fabric Loader version specified in config.json (e.g. "0.16.9").
    /// </summary>
    public string? TargetLoaderVersion { get; set; }

    /// <summary>
    /// The installed Fabric Loader version detected in the local .minecraft folder (e.g. "0.15.11").
    /// Null if no Fabric installation was found.
    /// </summary>
    public string? InstalledLoaderVersion { get; set; }

    /// <summary>
    /// The Minecraft game version detected from the installed profile or modpack (e.g. "1.21.1").
    /// </summary>
    public string? MinecraftVersion { get; set; }

    /// <summary>
    /// Whether a local .minecraft directory was found co-located with the app.
    /// </summary>
    public bool IsMinecraftFound { get; set; }

    /// <summary>
    /// The resolved path to the local .minecraft directory.
    /// </summary>
    public string? MinecraftFolderPath { get; set; }

    /// <summary>
    /// Whether the installed loader version matches the target version.
    /// </summary>
    public bool IsUpToDate { get; set; }

    /// <summary>
    /// Human-readable detail or status note.
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Source where the target version was determined (e.g. "Repository (fabric-version.txt)" or "Local Configuration").
    /// </summary>
    public string? VersionSource { get; set; }

    /// <summary>
    /// Error message if version detection or profile reading encountered an issue.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
