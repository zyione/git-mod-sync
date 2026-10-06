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
    public string ResolvedRepositoryFolder => Path.GetFullPath(Path.Combine(PathUtils.GetDataDirectory(), Config.RepositoryFolder));

    public string InstanceStorageKey => Config.InstanceStorageId ?? PathUtils.InstanceKey(MinecraftFolder);
    public string BackupFolder => Path.Combine(Path.GetDirectoryName(_configFilePath)!, "backups", InstanceStorageKey);

    public string ResolvedResourcePackRepositoryFolder => string.IsNullOrWhiteSpace(Config.ResourcePackRepository) || SameRepository(Config.ResourcePackRepository, Config.ResourcePackBranch, Config.Repository, Config.Branch)
        ? ResolvedRepositoryFolder : ResolvedRepositoryFolder.TrimEnd(Path.DirectorySeparatorChar) + "-resourcepacks";
    public string ResolvedShaderPackRepositoryFolder => string.IsNullOrWhiteSpace(Config.ShaderPackRepository) || SameRepository(Config.ShaderPackRepository, Config.ShaderPackBranch, Config.Repository, Config.Branch)
        ? ResolvedRepositoryFolder : SameRepository(Config.ShaderPackRepository, Config.ShaderPackBranch, Config.ResourcePackRepository, Config.ResourcePackBranch)
            ? ResolvedResourcePackRepositoryFolder : ResolvedRepositoryFolder.TrimEnd(Path.DirectorySeparatorChar) + "-shaderpacks";
    private static bool SameRepository(string a, string branchA, string b, string branchB) =>
        a.Trim().TrimEnd('/').Equals(b.Trim().TrimEnd('/'), StringComparison.Ordinal) && branchA == branchB;

    public string MinecraftFolder => Path.GetDirectoryName(ResolvedModsFolder)!;
    public string ResolvedResourcePacksFolder => ResolveAssetFolder(Config.ResourcePacksFolder);
    public string ResolvedShaderPacksFolder => ResolveAssetFolder(Config.ShaderPacksFolder);
    private string ResolveAssetFolder(string path) => Path.GetFullPath(Path.Combine(MinecraftFolder, path));

    public ConfigService(LoggingService logger, string? configFilePath = null)
    {
        _logger = logger;
        _configFilePath = configFilePath ?? Path.Combine(PathUtils.GetDataDirectory(), "config.json");
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
            Config.PersonalModsAcknowledged ??= new();
            // Existing installations keep their selected folder when upgrading from versions without onboarding.
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.EnumerateObject().Any(p => p.Name.Equals("instanceSelectionCompleted", StringComparison.OrdinalIgnoreCase)) &&
                (Config.FirstSyncCompleted || Config.ModsFolder is not ("./mods" or "../mods" or "mods")))
                Config.InstanceSelectionCompleted = true;

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
        if (Path.GetFileName(normalized).Equals("mods", StringComparison.OrdinalIgnoreCase) && InstanceDiscoveryService.IsInstance(Path.GetDirectoryName(normalized)!))
            return SelectInstance(Path.GetDirectoryName(normalized)!);

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

    public bool SelectInstance(string instanceFolder)
    {
        if (!InstanceDiscoveryService.IsInstance(instanceFolder)) return false;
        var previous = Config;
        try
        {
            bool same = PathUtils.InstanceKey(instanceFolder) == PathUtils.InstanceKey(MinecraftFolder);
            if (Config.InstanceSelectionCompleted && !same)
            {
                Config.InstanceStorageId ??= PathUtils.InstanceKey(MinecraftFolder);
                Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath(MinecraftFolder))!);
                WriteAtomic(ProfilePath(MinecraftFolder), JsonSerializer.Serialize(Config, JsonOptions));
            }
            string profile = ProfilePath(instanceFolder);
            if (!same && File.Exists(profile))
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(profile), JsonOptions) ?? throw new IOException("Empty instance profile.");
            else if (!same && previous.InstanceSelectionCompleted) Config = AppStorage.ImportMatchingInstance(instanceFolder, Path.GetDirectoryName(_configFilePath)!) ?? new AppConfig();
            else Config = (!previous.InstanceSelectionCompleted ? AppStorage.ImportMatchingInstance(instanceFolder, Path.GetDirectoryName(_configFilePath)!) : null)
                ?? JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(previous, JsonOptions), JsonOptions)!;
            Config.InstanceStorageId ??= PathUtils.InstanceKey(instanceFolder);
            Config.ModsFolder = Path.GetRelativePath(PathUtils.GetAppDirectory(), Path.Combine(Path.GetFullPath(instanceFolder), "mods"));
            Config.InstanceSelectionCompleted = true;
            if (Save()) return true;
        }
        catch (Exception ex) { _logger.Error("Could not select instance", ex); }
        Config = previous;
        return false;
    }

    private string ProfilePath(string instance) => Path.Combine(Path.GetDirectoryName(_configFilePath)!, "instances", PathUtils.PortableInstanceKey(instance) + ".json");
    public bool IsKnownInstance(string instance) => Config.InstanceSelectionCompleted &&
        (PathUtils.InstanceKey(instance) == PathUtils.InstanceKey(MinecraftFolder) || File.Exists(ProfilePath(instance)));

    private static void WriteAtomic(string path, string json)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// Saves the current configuration to config.json.
    /// </summary>
    public bool Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Config, JsonOptions);
            string temporary = _configFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, json); File.Move(temporary, _configFilePath, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
