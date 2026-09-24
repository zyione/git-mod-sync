using System.Text.Json;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>
/// Service responsible for loading, validating, creating, and updating config.json.
/// All relative paths are resolved against the ModSync.exe directory.
/// </summary>
public class ConfigService
{
    private readonly LoggingService _logger;
    private readonly string _configFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppConfig Config { get; private set; } = new();

    public string ConfigFilePath => _configFilePath;

    /// <summary>
    /// Absolute path to the user's Minecraft mods folder.
    /// </summary>
    public string ResolvedModsFolder => PathUtils.ResolveAppPath(Config.ModsFolder);

    /// <summary>
    /// Absolute path to the internal Git repository folder.
    /// </summary>
    public string ResolvedRepositoryFolder => PathUtils.ResolveAppPath(Config.RepositoryFolder);

    public ConfigService(LoggingService logger)
    {
        _logger = logger;
        _configFilePath = Path.Combine(PathUtils.GetAppDirectory(), "config.json");
    }

    /// <summary>
    /// Loads and validates configuration from config.json.
    /// Returns (Success, IsNewlyCreated, ErrorMessage).
    /// </summary>
    public (bool Success, bool IsNewlyCreated, string? ErrorMessage) Load()
    {
        if (!File.Exists(_configFilePath))
        {
            try
            {
                CreateDefaultConfig();
                _logger.Info($"Created default config template at {_configFilePath}");
                return (false, true, "config.json has been created.\n\nPlease configure your GitHub repository in config.json before using ModSync.");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to create default config.json", ex);
                return (false, false, $"Could not create config.json: {ex.Message}");
            }
        }

        try
        {
            string json = File.ReadAllText(_configFilePath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);

            if (loaded == null)
            {
                return (false, false, "config.json is empty or contains an invalid structure.");
            }

            Config = loaded;
            _logger.Info($"Loaded configuration successfully. Repo: {Config.Repository}, Branch: {Config.Branch}");

            // Validate placeholder
            if (Config.Repository.Contains("USERNAME/MinecraftMods") || Config.Repository.Contains("USERNAME/REPOSITORY"))
            {
                return (false, false, "config.json still contains the template repository URL.\nPlease edit config.json and set your real GitHub repository URL.");
            }

            return (true, false, null);
        }
        catch (JsonException jex)
        {
            string err = $"Syntax error in config.json at Line {jex.LineNumber}, Byte {jex.BytePositionInLine}: {jex.Message}";
            _logger.Error(err, jex);
            return (false, false, err);
        }
        catch (Exception ex)
        {
            string err = $"Failed to read config.json: {ex.Message}";
            _logger.Error(err, ex);
            return (false, false, err);
        }
    }

    /// <summary>
    /// Saves the current configuration to config.json.
    /// </summary>
    public bool Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Config, JsonOptions);
            File.WriteAllText(_configFilePath, json);
            _logger.Info("Saved configuration updates to config.json");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to save config.json", ex);
            return false;
        }
    }

    private void CreateDefaultConfig()
    {
        var defaultConfig = new AppConfig
        {
            Repository = "https://github.com/USERNAME/MinecraftMods.git",
            Branch = "main",
            ModsFolder = "../mods",
            RepositoryFolder = "./repository",
            RequireConfirmationBeforePush = true,
            RequireConfirmationBeforeSync = true,
            AllowedExtensions = new List<string> { ".jar" },
            SyncSubdirectories = false,
            WarnFileSizeMb = 50,
            MaxFileSizeMb = 100
        };

        string json = JsonSerializer.Serialize(defaultConfig, JsonOptions);
        File.WriteAllText(_configFilePath, json);
    }
}
