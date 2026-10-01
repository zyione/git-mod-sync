using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>
/// Service responsible for detecting, comparing, and installing Fabric Loader profiles
/// in the local .minecraft folder co-located with the application.
/// </summary>
public class FabricService
{
    private readonly ConfigService _configService;
    private readonly LoggingService _logger;
    private readonly AuthenticationService? _authService;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public FabricService(ConfigService configService, LoggingService logger, AuthenticationService? authService = null)
    {
        _configService = configService;
        _logger = logger;
        _authService = authService;
    }

    /// <summary>
    /// Optional directory override (primarily for automated testing).
    /// </summary>
    protected internal string? CustomMinecraftDirectory { get; set; }

    /// <summary>
    /// Optional repository folder override (primarily for automated testing).
    /// </summary>
    protected internal string? CustomRepositoryDirectory { get; set; }

    /// <summary>
    /// Resolves the local .minecraft directory co-located with the application.
    /// Priority:
    /// 1. CustomMinecraftDirectory override if set.
    /// 2. Subdirectory ".minecraft" directly inside the app directory.
    /// 3. If the app itself is placed inside a .minecraft directory.
    /// 4. Subdirectory ".minecraft" in current working directory.
    /// </summary>
    public string GetMinecraftDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CustomMinecraftDirectory))
        {
            return Path.GetFullPath(CustomMinecraftDirectory);
        }

        string appDir = PathUtils.GetAppDirectory();

        // 1. Co-located .minecraft next to ModSync.exe
        string nextToExe = Path.Combine(appDir, ".minecraft");
        if (Directory.Exists(nextToExe))
        {
            return Path.GetFullPath(nextToExe);
        }

        // 2. Running directly inside .minecraft
        if (string.Equals(Path.GetFileName(appDir), ".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(appDir);
        }

        // 3. Current working directory .minecraft
        string cwdCandidate = Path.Combine(Environment.CurrentDirectory, ".minecraft");
        if (Directory.Exists(cwdCandidate))
        {
            return Path.GetFullPath(cwdCandidate);
        }

        // Default path even if it does not yet exist
        return Path.GetFullPath(nextToExe);
    }

    /// <summary>
    /// Resolves the target Fabric Loader version from the repository source (Option A: fabric-version.txt)
    /// or falls back to AppConfig if specified.
    /// Returns (TargetVersion, SourceDescription).
    /// </summary>
    public async Task<(string? Version, string? Source)> ResolveTargetFabricLoaderVersionAsync()
    {
        // 1. Check local cloned repository folder (fabric-version.txt)
        var localResult = ResolveTargetFabricLoaderVersionFromLocalRepo();
        if (localResult != null)
        {
            return (localResult, "Repository (fabric-version.txt)");
        }

        // 2. Check remote GitHub raw URL if repository is configured
        string repoUrl = _configService.Config.Repository;
        if (!string.IsNullOrWhiteSpace(repoUrl))
        {
            var (owner, repo) = PathUtils.ParseGitHubOwnerAndRepo(repoUrl);
            if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo))
            {
                string branch = !string.IsNullOrWhiteSpace(_configService.Config.Branch)
                    ? _configService.Config.Branch.Trim()
                    : "main";
                string rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/fabric-version.txt";

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, rawUrl);
                    request.Headers.Add("User-Agent", "ModSync-Fabric-Version-Checker");

                    string? token = _authService?.GetStoredToken();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var response = await HttpClient.SendAsync(request, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string content = (await response.Content.ReadAsStringAsync()).Trim();
                        string clean = CleanVersionString(content);
                        if (!string.IsNullOrWhiteSpace(clean))
                        {
                            _logger.Info($"Resolved Fabric Loader version '{clean}' from remote repository: {rawUrl}");
                            return (clean, "Remote Repository (fabric-version.txt)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Could not fetch remote fabric-version.txt from {rawUrl}: {ex.Message}");
                }
            }
        }

        // 3. Fallback to local config.json if set
        string? configVersion = _configService.Config.FabricLoaderVersion?.Trim();
        if (!string.IsNullOrWhiteSpace(configVersion))
        {
            return (CleanVersionString(configVersion), "Configuration (config.json)");
        }

        return (null, null);
    }

    /// <summary>
    /// Fast synchronous resolution from local files only (local repository or config).
    /// </summary>
    public (string? Version, string? Source) ResolveTargetFabricLoaderVersionLocalFast()
    {
        var localResult = ResolveTargetFabricLoaderVersionFromLocalRepo();
        if (localResult != null)
        {
            return (localResult, "Repository (fabric-version.txt)");
        }

        string? configVersion = _configService.Config.FabricLoaderVersion?.Trim();
        if (!string.IsNullOrWhiteSpace(configVersion))
        {
            return (CleanVersionString(configVersion), "Configuration (config.json)");
        }

        return (null, null);
    }

    private string? ResolveTargetFabricLoaderVersionFromLocalRepo()
    {
        try
        {
            string repoDir = !string.IsNullOrWhiteSpace(CustomRepositoryDirectory)
                ? CustomRepositoryDirectory
                : _configService.ResolvedRepositoryFolder;

            if (Directory.Exists(repoDir))
            {
                string txtPath = Path.Combine(repoDir, "fabric-version.txt");
                if (File.Exists(txtPath))
                {
                    string content = File.ReadAllText(txtPath).Trim();
                    string clean = CleanVersionString(content);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        _logger.Info($"Resolved Fabric Loader version '{clean}' from local repo: {txtPath}");
                        return clean;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Error reading local fabric-version.txt: {ex.Message}");
        }

        return null;
    }

    private static string CleanVersionString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string firstLine = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.TrimStart('v', 'V').Trim();
    }

    /// <summary>
    /// Asynchronously inspects the repository source and local .minecraft directory.
    /// </summary>
    public async Task<FabricStatusInfo> DetectFabricStatusAsync()
    {
        var (targetVersion, source) = await ResolveTargetFabricLoaderVersionAsync();
        return DetectFabricStatusInternal(targetVersion, source);
    }

    /// <summary>
    /// Inspects the local .minecraft directory and compares against the target version.
    /// </summary>
    public FabricStatusInfo DetectFabricStatus(string? targetVersionOverride = null, string? sourceOverride = null)
    {
        if (targetVersionOverride != null)
        {
            return DetectFabricStatusInternal(targetVersionOverride, sourceOverride ?? "Specified Version");
        }

        var (targetVersion, source) = ResolveTargetFabricLoaderVersionLocalFast();
        return DetectFabricStatusInternal(targetVersion, source);
    }

    private FabricStatusInfo DetectFabricStatusInternal(string? targetVersion, string? versionSource)
    {
        bool isConfigured = !string.IsNullOrWhiteSpace(targetVersion);
        string mcDir = GetMinecraftDirectory();
        bool mcFound = Directory.Exists(mcDir);

        var info = new FabricStatusInfo
        {
            IsConfigured = isConfigured,
            TargetLoaderVersion = targetVersion,
            VersionSource = versionSource,
            IsMinecraftFound = mcFound,
            MinecraftFolderPath = mcDir
        };

        if (!isConfigured)
        {
            info.Details = "Fabric Loader version is not set in repository or configuration.";
            return info;
        }

        if (!mcFound)
        {
            info.Details = $"Local .minecraft folder was not found at '{mcDir}'.";
            _logger.Warning($"Fabric check: .minecraft folder not found at '{mcDir}'");
            return info;
        }

        try
        {
            string versionsDir = Path.Combine(mcDir, "versions");
            string? installedLoader = null;
            string? detectedMcVersion = null;

            if (Directory.Exists(versionsDir))
            {
                var dirInfo = new DirectoryInfo(versionsDir);
                var subDirs = dirInfo.GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).ToList();

                // 1. Search for existing Fabric installations
                foreach (var dir in subDirs)
                {
                    string dirName = dir.Name;
                    string jsonPath = Path.Combine(dir.FullName, $"{dirName}.json");

                    // Try folder name regex: fabric-loader-{loaderVersion}-{mcVersion}
                    var match = Regex.Match(dirName, @"^fabric-loader-(?<loader>\d+\.\d+\.\d+(?:[a-zA-Z0-9\.\+\-]*))-(?<mc>\d+\.\d+(?:\.\d+)?(?:[a-zA-Z0-9\.\+\-]*))$", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        installedLoader ??= match.Groups["loader"].Value;
                        detectedMcVersion ??= match.Groups["mc"].Value;
                    }

                    // Check version JSON file for confirmation / details
                    if (File.Exists(jsonPath))
                    {
                        try
                        {
                            string jsonContent = File.ReadAllText(jsonPath);
                            using var doc = JsonDocument.Parse(jsonContent);
                            var root = doc.RootElement;

                            if (root.TryGetProperty("inheritsFrom", out var inheritsProp))
                            {
                                string? inherits = inheritsProp.GetString();
                                if (!string.IsNullOrWhiteSpace(inherits))
                                {
                                    detectedMcVersion ??= inherits;
                                }
                            }

                            if (root.TryGetProperty("libraries", out var libsProp) && libsProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var lib in libsProp.EnumerateArray())
                                {
                                    if (lib.TryGetProperty("name", out var nameProp))
                                    {
                                        string? libName = nameProp.GetString();
                                        if (libName != null && libName.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase))
                                        {
                                            string ver = libName["net.fabricmc:fabric-loader:".Length..].Trim();
                                            if (!string.IsNullOrWhiteSpace(ver))
                                            {
                                                installedLoader = ver;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            if (installedLoader != null && detectedMcVersion != null)
                            {
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"Could not parse version JSON at {jsonPath}: {ex.Message}");
                        }
                    }
                }

                // 2. If Minecraft version not found from Fabric folders, check vanilla version folders
                if (detectedMcVersion == null)
                {
                    foreach (var dir in subDirs)
                    {
                        if (Regex.IsMatch(dir.Name, @"^\d+\.\d+(\.\d+)?$"))
                        {
                            detectedMcVersion = dir.Name;
                            break;
                        }
                    }
                }
            }

            // 3. Fallback: inspect launcher_profiles.json if present
            if (detectedMcVersion == null)
            {
                string launcherProfilesPath = Path.Combine(mcDir, "launcher_profiles.json");
                if (File.Exists(launcherProfilesPath))
                {
                    try
                    {
                        string content = File.ReadAllText(launcherProfilesPath);
                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.TryGetProperty("profiles", out var profilesProp) && profilesProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in profilesProp.EnumerateObject())
                            {
                                if (prop.Value.TryGetProperty("lastVersionId", out var lvidProp))
                                {
                                    string? lvid = lvidProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(lvid))
                                    {
                                        var m = Regex.Match(lvid, @"^fabric-loader-(?<loader>\d+\.\d+\.\d+(?:[a-zA-Z0-9\.\+\-]*))-(?<mc>\d+\.\d+(?:\.\d+)?(?:[a-zA-Z0-9\.\+\-]*))$", RegexOptions.IgnoreCase);
                                        if (m.Success)
                                        {
                                            installedLoader ??= m.Groups["loader"].Value;
                                            detectedMcVersion ??= m.Groups["mc"].Value;
                                            break;
                                        }

                                        var mcMatch = Regex.Match(lvid, @"(?<mc>\d+\.\d+(\.\d+)?)");
                                        if (mcMatch.Success)
                                        {
                                            detectedMcVersion ??= mcMatch.Groups["mc"].Value;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Could not read launcher_profiles.json: {ex.Message}");
                    }
                }
            }

            // 4. Fallback: check mods folder for fabric.mod.json to detect Minecraft version
            if (detectedMcVersion == null)
            {
                detectedMcVersion = TryDetectMcVersionFromModsFolder();
            }

            info.InstalledLoaderVersion = installedLoader;
            info.MinecraftVersion = detectedMcVersion;

            if (installedLoader != null && targetVersion != null)
            {
                info.IsUpToDate = string.Equals(installedLoader.Trim(), targetVersion.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                info.IsUpToDate = false;
            }

            if (info.IsUpToDate)
            {
                info.Details = $"Fabric Loader is up to date ({installedLoader}).";
            }
            else if (installedLoader == null)
            {
                info.Details = $"Fabric Loader is not installed. Target version: {targetVersion}.";
            }
            else
            {
                info.Details = $"Fabric Loader version mismatch: installed {installedLoader}, required {targetVersion}.";
            }

            _logger.Info($"Fabric detection: Installed='{installedLoader}', Target='{targetVersion}', MC='{detectedMcVersion}', UpToDate={info.IsUpToDate}");
            return info;
        }
        catch (Exception ex)
        {
            _logger.Error("Error detecting Fabric Loader status", ex);
            info.ErrorMessage = $"Failed to check Fabric Loader: {ex.Message}";
            return info;
        }
    }

    /// <summary>
    /// Downloads the Fabric Loader profile JSON from Fabric Meta API and installs it
    /// into the local .minecraft/versions/ directory.
    /// Also updates launcher_profiles.json if present.
    /// </summary>
    public async Task<(bool Success, string? Error)> InstallFabricLoaderAsync(
        string mcVersion,
        string targetLoaderVersion,
        Action<SyncProgressInfo>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            return (false, "Cannot install Fabric Loader: Minecraft version could not be determined.");
        }

        if (string.IsNullOrWhiteSpace(targetLoaderVersion))
        {
            return (false, "Cannot install Fabric Loader: Target version is not specified.");
        }

        string mcDir = GetMinecraftDirectory();
        if (!Directory.Exists(mcDir))
        {
            Directory.CreateDirectory(mcDir);
        }

        string versionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(versionsDir))
        {
            Directory.CreateDirectory(versionsDir);
        }

        string cleanMcVer = mcVersion.Trim();
        string cleanLoaderVer = targetLoaderVersion.Trim();
        string apiUrl = $"https://meta.fabricmc.net/v2/versions/loader/{cleanMcVer}/{cleanLoaderVer}/profile/json";

        try
        {
            _logger.Info($"Fetching Fabric profile from: {apiUrl}");
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Connecting to Fabric Meta...", "Fetching version profile from meta.fabricmc.net..."));

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("User-Agent", "ModSync-Fabric-Installer");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return (false, $"Fabric Loader version '{cleanLoaderVer}' was not found for Minecraft '{cleanMcVer}' on Fabric Meta.");
                }

                return (false, $"Fabric Meta API returned: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Installing Fabric...", "Saving Fabric version profile..."));

            string jsonContent = await response.Content.ReadAsStringAsync();

            // Validate that returned JSON is valid and extract id
            string versionId = $"fabric-loader-{cleanLoaderVer}-{cleanMcVer}";
            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    string? idVal = idProp.GetString();
                    if (!string.IsNullOrWhiteSpace(idVal))
                    {
                        versionId = idVal;
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, $"Received invalid JSON from Fabric Meta API: {ex.Message}");
            }

            // Write version profile
            string targetFolder = Path.Combine(versionsDir, versionId);
            Directory.CreateDirectory(targetFolder);

            string targetJsonPath = Path.Combine(targetFolder, $"{versionId}.json");
            await File.WriteAllTextAsync(targetJsonPath, jsonContent);
            _logger.Info($"Successfully wrote Fabric profile JSON to {targetJsonPath}");

            // Update launcher_profiles.json if present
            UpdateLauncherProfiles(mcDir, versionId, cleanMcVer);

            progressCallback?.Invoke(SyncProgressInfo.Determinate("Complete", 100, $"Fabric Loader {cleanLoaderVer} installed successfully."));
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to install Fabric Loader profile", ex);
            return (false, $"Fabric Loader installation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates or creates a Fabric profile in launcher_profiles.json if the file exists.
    /// </summary>
    private void UpdateLauncherProfiles(string mcDir, string newVersionId, string mcVersion)
    {
        string profilesPath = Path.Combine(mcDir, "launcher_profiles.json");
        if (!File.Exists(profilesPath))
        {
            return;
        }

        try
        {
            string content = File.ReadAllText(profilesPath);
            var rootNode = JsonNode.Parse(content);
            if (rootNode is JsonObject rootObj && rootObj["profiles"] is JsonObject profilesObj)
            {
                bool profileUpdated = false;

                // Look for existing fabric profiles to update
                foreach (var kvp in profilesObj)
                {
                    if (kvp.Value is JsonObject profile)
                    {
                        string? lastVer = profile["lastVersionId"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(lastVer) && lastVer.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase))
                        {
                            profile["lastVersionId"] = newVersionId;
                            profileUpdated = true;
                            _logger.Info($"Updated existing launcher profile '{kvp.Key}' lastVersionId to '{newVersionId}'");
                        }
                    }
                }

                // If no fabric profile existed, create a new one
                if (!profileUpdated)
                {
                    var newProfile = new JsonObject
                    {
                        ["name"] = $"Fabric Loader {mcVersion}",
                        ["type"] = "custom",
                        ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        ["lastUsed"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        ["icon"] = "Fabric",
                        ["lastVersionId"] = newVersionId
                    };

                    profilesObj["fabric"] = newProfile;
                    _logger.Info($"Created new launcher profile 'fabric' with lastVersionId '{newVersionId}'");
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(profilesPath, rootNode.ToJsonString(options));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not update launcher_profiles.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Helper to inspect JAR files in the mods folder to extract the Minecraft version dependency.
    /// </summary>
    private string? TryDetectMcVersionFromModsFolder()
    {
        try
        {
            string modsFolder = _configService.ResolvedModsFolder;
            if (!Directory.Exists(modsFolder))
            {
                return null;
            }

            var jarFiles = Directory.GetFiles(modsFolder, "*.jar", SearchOption.TopDirectoryOnly);
            foreach (var jarPath in jarFiles)
            {
                try
                {
                    using var archive = ZipFile.OpenRead(jarPath);
                    var entry = archive.GetEntry("fabric.mod.json");
                    if (entry != null)
                    {
                        using var stream = entry.Open();
                        using var doc = JsonDocument.Parse(stream);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("depends", out var dependsProp) && dependsProp.ValueKind == JsonValueKind.Object)
                        {
                            if (dependsProp.TryGetProperty("minecraft", out var mcProp))
                            {
                                string? mcDep = mcProp.ValueKind == JsonValueKind.String ? mcProp.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(mcDep))
                                {
                                    var match = Regex.Match(mcDep, @"\d+\.\d+(?:\.\d+)?");
                                    if (match.Success)
                                    {
                                        _logger.Info($"Detected Minecraft version '{match.Value}' from mod {Path.GetFileName(jarPath)}");
                                        return match.Value;
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore individual unreadable jars
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not scan mods folder for Minecraft version: {ex.Message}");
        }

        return null;
    }
}
