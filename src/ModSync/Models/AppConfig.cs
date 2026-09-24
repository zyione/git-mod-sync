using System.Text.Json.Serialization;

namespace ModSync.Models;

/// <summary>
/// Configuration model loaded from config.json.
/// All relative paths are resolved relative to ModSync.exe directory,
/// not the current working directory.
/// </summary>
public class AppConfig
{
    [JsonPropertyName("repository")]
    public string Repository { get; set; } = "https://github.com/USERNAME/MinecraftMods.git";

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "main";

    [JsonPropertyName("modsFolder")]
    public string ModsFolder { get; set; } = "../mods";

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
}
