using System.Text.Json;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>
/// Service responsible for loading, validating, creating, and updating config.json.
/// All relative paths are resolved against the ModSync.exe directory with intelligent
/// detection when ModSync is located directly inside the .minecraft directory.
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
    /// Intelligently resolves relative paths, ensuring that if ModSync is running directly
    /// inside .minecraft, mods are placed in .minecraft/mods and not outside.
    /// </summary>
    public string ResolvedModsFolder => ResolveIntelligentModsFolder(Config.ModsFolder);

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

            // Auto-heal old "../mods" if running directly inside .minecraft
            string appDir = PathUtils.GetAppDirectory();
            if (Config.ModsFolder == "../mods" && IsMinecraftDirectory(appDir))
            {
                _logger.Info("Detected ModSync running directly inside .minecraft directory. Automatically updating modsFolder from '../mods' to './mods'.");
                Config.ModsFolder = "./mods";
                Save();
            }

            if (Config.SavedRepositories == null || Config.SavedRepositories.Count == 0)
            {
                Config.SavedRepositories = new List<string> { Config.Repository };
            }
            else if (!Config.SavedRepositories.Contains(Config.Repository, StringComparer.OrdinalIgnoreCase))
            {
                Config.SavedRepositories.Insert(0, Config.Repository);
            }

            _logger.Info($"Loaded configuration successfully. Repo: {Config.Repository}, Branch: {Config.Branch}, Mods: {ResolvedModsFolder}");

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
    /// Switches the active repository to a new repository URL and saves config.json.
    /// </summary>
    public bool SwitchRepository(string newRepoUrl, string? newBranch = null)
    {
        if (string.IsNullOrWhiteSpace(newRepoUrl)) return false;

        string trimmedUrl = newRepoUrl.Trim();
        Config.Repository = trimmedUrl;

        if (!string.IsNullOrWhiteSpace(newBranch))
        {
            Config.Branch = newBranch.Trim();
        }

        if (Config.SavedRepositories == null)
            Config.SavedRepositories = new List<string>();

        if (!Config.SavedRepositories.Contains(trimmedUrl, StringComparer.OrdinalIgnoreCase))
        {
            Config.SavedRepositories.Add(trimmedUrl);
        }

        return Save();
    }

    /// <summary>
    /// Updates the configured mods folder path and saves config.json.
    /// </summary>
    public bool SetModsFolder(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath)) return false;

        string appDir = PathUtils.GetAppDirectory();
        string normalized = Path.GetFullPath(newPath.Trim());

        // Store relative path if inside or beside app directory for portability
        string relative = Path.GetRelativePath(appDir, normalized);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            Config.ModsFolder = "./" + relative.Replace('\\', '/');
        }
        else
        {
            Config.ModsFolder = normalized;
        }

        _logger.Info($"Updated mods folder to: {Config.ModsFolder} (Resolved: {ResolvedModsFolder})");
        return Save();
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

    /// <summary>
    /// Intelligently resolves the mods folder path.
    /// If ModSync.exe is running directly inside .minecraft (or instance with saves/versions/mods),
    /// relative paths like "../mods" are automatically corrected to "./mods".
    /// </summary>
    public static string ResolveIntelligentModsFolder(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            configuredPath = "./mods";

        string appDir = PathUtils.GetAppDirectory();

        // If explicitly absolute, return as-is
        if (Path.IsPathRooted(configuredPath))
            return Path.GetFullPath(configuredPath);

        // If configured as "../mods" or going up a directory, check if app is already inside .minecraft
        if (configuredPath.StartsWith("..", StringComparison.Ordinal))
        {
            if (IsMinecraftDirectory(appDir))
            {
                // Running directly in .minecraft! Place mods in ./mods (.minecraft/mods)
                return Path.GetFullPath(Path.Combine(appDir, "mods"));
            }
        }

        return PathUtils.ResolveAppPath(configuredPath);
    }

    /// <summary>
    /// Checks if a directory appears to be the root of a Minecraft installation (e.g. .minecraft).
    /// </summary>
    public static bool IsMinecraftDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;

        string dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (dirName.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check common Minecraft root markers
        if (Directory.Exists(Path.Combine(dir, "mods"))) return true;
        if (Directory.Exists(Path.Combine(dir, "saves"))) return true;
        if (Directory.Exists(Path.Combine(dir, "versions"))) return true;
        if (File.Exists(Path.Combine(dir, "options.txt"))) return true;
        if (File.Exists(Path.Combine(dir, "launcher_profiles.json"))) return true;

        return false;
    }

    private void CreateDefaultConfig()
    {
        var defaultConfig = new AppConfig
        {
            Repository = AppConfig.DefaultRepositoryUrl,
            SavedRepositories = new List<string> { AppConfig.DefaultRepositoryUrl },
            Branch = "main",
            ModsFolder = "./mods",
            RepositoryFolder = "./repository",
            RequireConfirmationBeforePush = true,
            RequireConfirmationBeforeSync = true,
            AllowedExtensions = new List<string> { ".jar" },
            SyncSubdirectories = false,
            WarnFileSizeMb = 50,
            MaxFileSizeMb = 100,
            CleanInstallOnFirstRun = true,
            BackupBeforeClean = true,
            FirstSyncCompleted = false,
            IgnoredMods = new List<string>(),
            AutoCheckUpdates = true,
            AppUpdateRepository = "https://github.com/zyione/git-mod-sync"
        };

        string json = JsonSerializer.Serialize(defaultConfig, JsonOptions);
        File.WriteAllText(_configFilePath, json);
    }
}
