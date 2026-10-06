using System.Text.Json.Serialization;

namespace ModSync.Models;

/// <summary>
/// Configuration model loaded from config.json.
/// All relative paths are resolved relative to ModSync.exe directory,
/// not the current working directory.
/// </summary>
public class AppConfig
{
    public string? InstanceStorageId { get; set; }
    public const string DefaultRepositoryUrl = "https://github.com/zyione/4stoogies-mod-list.git";

    [JsonPropertyName("repository")]
    public string Repository { get; set; } = DefaultRepositoryUrl;

    public const string DefaultResourcePackRepositoryUrl = "https://github.com/zyione/4stoogies-resourcepack-list.git";
    public const string DefaultShaderPackRepositoryUrl = "https://github.com/zyione/4stoogies-shaderpack-list.git";

    [JsonPropertyName("resourcePackRepository")]
    public string ResourcePackRepository { get; set; } = DefaultResourcePackRepositoryUrl;

    [JsonPropertyName("shaderPackRepository")]
    public string ShaderPackRepository { get; set; } = DefaultShaderPackRepositoryUrl;

    [JsonPropertyName("resourcePackBranch")]
    public string ResourcePackBranch { get; set; } = "main";

    [JsonPropertyName("shaderPackBranch")]
    public string ShaderPackBranch { get; set; } = "main";

    [JsonPropertyName("savedRepositories")]
    public List<string> SavedRepositories { get; set; } = new() { DefaultRepositoryUrl };

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "main";

    [JsonPropertyName("instanceSelectionCompleted")]
    public bool InstanceSelectionCompleted { get; set; }

    [JsonPropertyName("personalModsAcknowledged")]
    public Dictionary<string, List<string>> PersonalModsAcknowledged { get; set; } = new();

    [JsonPropertyName("modsFolder")]
    public string ModsFolder { get; set; } = "./mods";

    [JsonPropertyName("resourcePacksFolder")]
    public string ResourcePacksFolder { get; set; } = "resourcepacks";

    [JsonPropertyName("shaderPacksFolder")]
    public string ShaderPacksFolder { get; set; } = "shaderpacks";

    [JsonPropertyName("syncResourcePacks")]
    public bool SyncResourcePacks { get; set; } = true;

    [JsonPropertyName("syncShaderPacks")]
    public bool SyncShaderPacks { get; set; } = true;

    [JsonPropertyName("enforcePackOrder")]
    // Legacy preference retained for config compatibility; resource-pack sync always applies shared order.
    public bool EnforcePackOrder { get; set; } = true;

    [JsonPropertyName("enforceActiveShader")]
    public bool EnforceActiveShader { get; set; } = true;

    [JsonPropertyName("publishResourcePackOrder")]
    // Legacy preference retained for config compatibility; resource-pack push always includes saved order.
    public bool PublishResourcePackOrder { get; set; } = false;

    [JsonPropertyName("repositoryFolder")]
    public string RepositoryFolder { get; set; } = "./repository";

    [JsonPropertyName("requireConfirmationBeforePush")]
    public bool RequireConfirmationBeforePush { get; set; } = true;

    [JsonPropertyName("requireConfirmationBeforeSync")]
    public bool RequireConfirmationBeforeSync { get; set; } = true;

    [JsonPropertyName("allowedExtensions")]
    public List<string> AllowedExtensions { get; set; } = new() { ".jar" };

    [JsonPropertyName("syncSubdirectories")]
    public bool SyncSubdirectories { get; set; } = false;

    [JsonPropertyName("warnFileSizeMb")]
    public long WarnFileSizeMb { get; set; } = 50;

    [JsonPropertyName("maxFileSizeMb")]
    public long MaxFileSizeMb { get; set; } = 100;

    [JsonPropertyName("cleanInstallOnFirstRun")]
    public bool CleanInstallOnFirstRun { get; set; } = true;

    [JsonPropertyName("backupBeforeClean")]
    public bool BackupBeforeClean { get; set; } = true;

    [JsonPropertyName("firstSyncCompleted")]
    public bool FirstSyncCompleted { get; set; } = false;

    /// <summary>
    /// List of mod filenames or wildcard patterns to ignore during sync and push.
    /// Excluded mods are never deleted during sync, and never uploaded during push.
    /// Supports exact names (e.g. "optifine.jar") or wildcards (e.g. "*zoom*", "iris-*").
    /// </summary>
    [JsonPropertyName("ignoredMods")]
    public List<string> IgnoredMods { get; set; } = new();

    /// <summary>
    /// Whether to automatically check for ModSync.exe updates on launch.
    /// </summary>
    [JsonPropertyName("autoCheckUpdates")]
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// GitHub repository URL used for checking application releases and updates.
    /// </summary>
    [JsonPropertyName("appUpdateRepository")]
    public string AppUpdateRepository { get; set; } = "https://github.com/zyione/git-mod-sync";

    /// <summary>
    /// Target Fabric Loader version to enforce (e.g. "0.16.9").
    /// If null or whitespace, Fabric Loader sync is pulled from repository fabric-version.txt.
    /// </summary>
    [JsonPropertyName("fabricLoaderVersion")]
    public string? FabricLoaderVersion { get; set; } = null;

    /// <summary>
    /// Whether to automatically sync the Fabric Loader version when syncing mods.
    /// Default is true.
    /// </summary>
    [JsonPropertyName("syncFabricLoader")]
    public bool SyncFabricLoader { get; set; } = true;

    /// <summary>
    /// Target or fallback Minecraft version (e.g. "1.20.1").
    /// Inferred automatically from installed profiles or repository (minecraft-version.txt).
    /// </summary>
    [JsonPropertyName("minecraftVersion")]
    public string MinecraftVersion { get; set; } = "1.20.1";
}
