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
    /// Optional mmc-pack.json file override (primarily for automated testing).
    /// </summary>
    protected internal string? CustomMmcPackPath { get; set; }

    /// <summary>
    /// Searches for mmc-pack.json in the current instance structure (MultiMC / UltiMC / Prism Launcher).
    /// Returns the absolute path if found, or null if this is a standard vanilla .minecraft folder.
    /// </summary>
    public string? FindMmcPackPath()
    {
        if (!string.IsNullOrWhiteSpace(CustomMmcPackPath))
        {
            return Path.GetFullPath(CustomMmcPackPath);
        }

        string mcDir = GetMinecraftDirectory();
        string appDir = PathUtils.GetAppDirectory();

        var candidates = new List<string>
        {
            Path.Combine(mcDir, "mmc-pack.json"),
            Path.Combine(appDir, "mmc-pack.json"),
        };

        var parentOfMc = Directory.GetParent(mcDir)?.FullName;
        if (parentOfMc != null)
        {
            candidates.Add(Path.Combine(parentOfMc, "mmc-pack.json"));
        }

        var parentOfApp = Directory.GetParent(appDir)?.FullName;
        if (parentOfApp != null)
        {
            candidates.Add(Path.Combine(parentOfApp, "mmc-pack.json"));
            var grandParentOfApp = Directory.GetParent(parentOfApp)?.FullName;
            if (grandParentOfApp != null)
            {
                candidates.Add(Path.Combine(grandParentOfApp, "mmc-pack.json"));
            }
        }

        try
        {
            string modsFolder = _configService.ResolvedModsFolder;
            var parentOfMods = Directory.GetParent(modsFolder)?.FullName;
            if (parentOfMods != null)
            {
                candidates.Add(Path.Combine(parentOfMods, "mmc-pack.json"));
                var grandParentOfMods = Directory.GetParent(parentOfMods)?.FullName;
                if (grandParentOfMods != null)
                {
                    candidates.Add(Path.Combine(grandParentOfMods, "mmc-pack.json"));
                }
            }
        }
        catch { }

        foreach (var candidate in candidates.Distinct())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the local .minecraft directory for this ModSync instance.
    /// ModSync is portable — it only touches the .minecraft it is co-located with,
    /// never a global launcher path such as %APPDATA%\.minecraft.
    /// Priority:
    /// 1. CustomMinecraftDirectory override if set (used in tests).
    /// 2. Subdirectory ".minecraft" directly inside the app directory.
    /// 3. If the app itself is placed inside a .minecraft directory.
    /// 4. Subdirectory ".minecraft" in current working directory.
    /// 5. Default: co-located path (created on demand).
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
            _logger.Info($"Resolved .minecraft (co-located with exe): {nextToExe}");
            return Path.GetFullPath(nextToExe);
        }

        // 2. Running directly inside .minecraft
        if (string.Equals(Path.GetFileName(appDir), ".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info($"Resolved .minecraft (app lives inside it): {appDir}");
            return Path.GetFullPath(appDir);
        }

        // 3. Current working directory .minecraft
        string cwdCandidate = Path.Combine(Environment.CurrentDirectory, ".minecraft");
        if (Directory.Exists(cwdCandidate))
        {
            _logger.Info($"Resolved .minecraft (CWD): {cwdCandidate}");
            return Path.GetFullPath(cwdCandidate);
        }

        // 4. Default: co-located path; created on demand if needed
        _logger.Warning($"No .minecraft found near exe. Will use/create: {nextToExe}");
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
    /// Resolves the target Minecraft version from the repository source (Option A: minecraft-version.txt)
    /// or falls back to AppConfig if specified (defaults to "1.20.1").
    /// </summary>
    public async Task<string> ResolveTargetMinecraftVersionAsync()
    {
        // 1. Check local cloned repository folder (minecraft-version.txt)
        var localResult = ResolveTargetMinecraftVersionFromLocalRepo();
        if (localResult != null)
        {
            return localResult;
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
                string rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/minecraft-version.txt";

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
                            _logger.Info($"Resolved Minecraft version '{clean}' from remote repository: {rawUrl}");
                            return clean;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Could not fetch remote minecraft-version.txt from {rawUrl}: {ex.Message}");
                }
            }
        }

        // 3. Fallback to local config.json or default 1.20.1
        return ResolveTargetMinecraftVersionLocalFast();
    }

    /// <summary>
    /// Fast synchronous resolution of target Minecraft version from local repo or config.
    /// </summary>
    public string ResolveTargetMinecraftVersionLocalFast()
    {
        var localResult = ResolveTargetMinecraftVersionFromLocalRepo();
        if (localResult != null)
        {
            return localResult;
        }

        string? configVersion = _configService.Config.MinecraftVersion?.Trim();
        if (!string.IsNullOrWhiteSpace(configVersion))
        {
            return CleanVersionString(configVersion);
        }

        return "1.20.1";
    }

    private string? ResolveTargetMinecraftVersionFromLocalRepo()
    {
        try
        {
            string repoDir = !string.IsNullOrWhiteSpace(CustomRepositoryDirectory)
                ? CustomRepositoryDirectory
                : _configService.ResolvedRepositoryFolder;

            if (Directory.Exists(repoDir))
            {
                string[] candidates = { "minecraft-version.txt", "mc-version.txt", ".minecraft-version" };
                foreach (var candidate in candidates)
                {
                    string txtPath = Path.Combine(repoDir, candidate);
                    if (File.Exists(txtPath))
                    {
                        string content = File.ReadAllText(txtPath).Trim();
                        string clean = CleanVersionString(content);
                        if (!string.IsNullOrWhiteSpace(clean))
                        {
                            _logger.Info($"Resolved Minecraft version '{clean}' from local repo: {txtPath}");
                            return clean;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Error reading local minecraft-version.txt: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Asynchronously inspects the repository source and local .minecraft directory.
    /// </summary>
    public async Task<FabricStatusInfo> DetectFabricStatusAsync()
    {
        var (targetVersion, source) = await ResolveTargetFabricLoaderVersionAsync();
        string? targetMcVersion = await ResolveTargetMinecraftVersionAsync();
        return DetectFabricStatusInternal(targetVersion, source, targetMcVersion);
    }

    /// <summary>
    /// Inspects the local .minecraft directory and compares against the target version.
    /// </summary>
    public FabricStatusInfo DetectFabricStatus(string? targetVersionOverride = null, string? sourceOverride = null, string? mcVersionOverride = null)
    {
        if (targetVersionOverride != null)
        {
            return DetectFabricStatusInternal(targetVersionOverride, sourceOverride ?? "Specified Version", mcVersionOverride);
        }

        var (targetVersion, source) = ResolveTargetFabricLoaderVersionLocalFast();
        string targetMc = mcVersionOverride ?? ResolveTargetMinecraftVersionLocalFast();
        return DetectFabricStatusInternal(targetVersion, source, targetMc);
    }

    private FabricStatusInfo DetectFabricStatusInternal(string? targetVersion, string? versionSource, string? targetMcVersion = null)
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

        if (!mcFound)
        {
            info.MinecraftVersion = targetMcVersion ?? TryDetectMcVersionFromModsFolder() ?? ResolveTargetMinecraftVersionLocalFast();
            info.Details = $"Local .minecraft folder was not found at '{mcDir}'.";
            _logger.Warning($"Fabric check: .minecraft folder not found at '{mcDir}'");
            return info;
        }

        try
        {
            string versionsDir = Path.Combine(mcDir, "versions");
            string? installedLoader = null;
            string? detectedMcVersion = null;

            // 0. Check for UltiMC / MultiMC / Prism instance (mmc-pack.json)
            string? mmcPackPath = FindMmcPackPath();
            if (mmcPackPath != null && File.Exists(mmcPackPath))
            {
                try
                {
                    string instanceDir = Path.GetDirectoryName(mmcPackPath)!;
                    string content = File.ReadAllText(mmcPackPath);
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("components", out var componentsProp) && componentsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var comp in componentsProp.EnumerateArray())
                        {
                            if (comp.TryGetProperty("uid", out var uidProp))
                            {
                                string? uid = uidProp.GetString();
                                if (string.Equals(uid, "net.fabricmc.fabric-loader", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (comp.TryGetProperty("version", out var verProp))
                                        installedLoader = verProp.GetString();
                                    else if (comp.TryGetProperty("cachedVersion", out var cachedVerProp))
                                        installedLoader = cachedVerProp.GetString();
                                }
                                else if (string.Equals(uid, "net.minecraft", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (comp.TryGetProperty("version", out var mcProp))
                                        detectedMcVersion = mcProp.GetString();
                                    else if (comp.TryGetProperty("cachedVersion", out var cachedMcProp))
                                        detectedMcVersion = cachedMcProp.GetString();
                                }
                            }
                        }
                    }

                    // Check if a customized patch exists in patches/
                    string patchesDir = Path.Combine(instanceDir, "patches");
                    string patchPath = Path.Combine(patchesDir, "net.fabricmc.fabric-loader.json");
                    if (File.Exists(patchPath))
                    {
                        try
                        {
                            string patchContent = File.ReadAllText(patchPath);
                            using var pDoc = JsonDocument.Parse(patchContent);
                            if (pDoc.RootElement.TryGetProperty("version", out var pVer))
                            {
                                string? patchVer = pVer.GetString();
                                if (!string.IsNullOrWhiteSpace(patchVer))
                                {
                                    installedLoader = patchVer;
                                }
                            }
                        }
                        catch { }
                    }

                    if (installedLoader != null)
                    {
                        _logger.Info($"MultiMC/UltiMC instance detected at '{instanceDir}': Fabric={installedLoader}, MC={detectedMcVersion}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Could not parse mmc-pack.json at {mmcPackPath}: {ex.Message}");
                }
            }

            if (installedLoader == null && Directory.Exists(versionsDir))
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

            // 5. Fallback: check repository minecraft-version.txt or AppConfig
            if (detectedMcVersion == null)
            {
                detectedMcVersion = targetMcVersion ?? ResolveTargetMinecraftVersionLocalFast();
            }

            info.InstalledLoaderVersion = installedLoader;
            info.MinecraftVersion = detectedMcVersion ?? "1.20.1";

            if (!isConfigured)
            {
                info.IsUpToDate = false;
                info.Details = "Fabric Loader version is not set in repository or configuration.";
                return info;
            }

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
    /// Installs Fabric Loader into the local instance.
    /// Supports both:
    /// - UltiMC / MultiMC / Prism instances: updates mmc-pack.json and patches/net.fabricmc.fabric-loader.json
    /// - Standard vanilla .minecraft: downloads profile JSON to versions/ and updates launcher_profiles.json
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

        string cleanMcVer = mcVersion.Trim();
        string cleanLoaderVer = targetLoaderVersion.Trim();
        string mcDir = GetMinecraftDirectory();

        // 1. Check for UltiMC / MultiMC / Prism instance (mmc-pack.json)
        string? mmcPackPath = FindMmcPackPath();
        if (mmcPackPath != null && File.Exists(mmcPackPath))
        {
            _logger.Info($"Detected MultiMC/UltiMC instance manifest at '{mmcPackPath}'. Installing Fabric Loader for UltiMC...");
            var (mmcSuccess, mmcError) = await InstallFabricLoaderForMultiMcAsync(mmcPackPath, cleanMcVer, cleanLoaderVer, progressCallback);
            if (!mmcSuccess)
            {
                return (false, mmcError);
            }

            // Also write vanilla versions profile as secondary fallback if versions folder exists
            try
            {
                string versionsDir = Path.Combine(mcDir, "versions");
                if (Directory.Exists(versionsDir))
                {
                    await WriteVanillaFabricProfileAsync(mcDir, versionsDir, cleanMcVer, cleanLoaderVer, null);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Secondary vanilla profile write skipped: {ex.Message}");
            }

            progressCallback?.Invoke(SyncProgressInfo.Determinate("Complete", 100, $"Fabric Loader {cleanLoaderVer} installed successfully in UltiMC instance."));
            return (true, null);
        }

        // 2. Standard vanilla Minecraft Launcher installation
        if (!Directory.Exists(mcDir))
        {
            Directory.CreateDirectory(mcDir);
        }

        string standardVersionsDir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(standardVersionsDir))
        {
            Directory.CreateDirectory(standardVersionsDir);
        }

        return await WriteVanillaFabricProfileAsync(mcDir, standardVersionsDir, cleanMcVer, cleanLoaderVer, progressCallback);
    }

    /// <summary>
    /// Installs or updates Fabric Loader in an UltiMC / MultiMC / Prism instance by updating
    /// mmc-pack.json and creating/updating patches/net.fabricmc.fabric-loader.json.
    /// </summary>
    private async Task<(bool Success, string? Error)> InstallFabricLoaderForMultiMcAsync(
        string mmcPackPath,
        string mcVersion,
        string targetLoaderVersion,
        Action<SyncProgressInfo>? progressCallback = null)
    {
        try
        {
            string instanceDir = Path.GetDirectoryName(mmcPackPath)!;
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Updating UltiMC Configuration...", "Updating mmc-pack.json component versions..."));

            string content = await File.ReadAllTextAsync(mmcPackPath);
            var rootNode = JsonNode.Parse(content);
            if (rootNode is not JsonObject rootObj)
            {
                return (false, "Invalid mmc-pack.json format: root is not a JSON object.");
            }

            var componentsNode = rootObj["components"] as JsonArray;
            if (componentsNode == null)
            {
                componentsNode = new JsonArray();
                rootObj["components"] = componentsNode;
            }

            JsonObject? fabricComponent = null;
            foreach (var node in componentsNode)
            {
                if (node is JsonObject comp && string.Equals(comp["uid"]?.GetValue<string>(), "net.fabricmc.fabric-loader", StringComparison.OrdinalIgnoreCase))
                {
                    fabricComponent = comp;
                    break;
                }
            }

            if (fabricComponent != null)
            {
                fabricComponent["version"] = targetLoaderVersion;
                fabricComponent["cachedVersion"] = targetLoaderVersion;
                _logger.Info($"Updated existing Fabric component in mmc-pack.json to version '{targetLoaderVersion}'");
            }
            else
            {
                fabricComponent = new JsonObject
                {
                    ["cachedName"] = "Fabric Loader",
                    ["cachedRequires"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["equals"] = mcVersion,
                            ["suggests"] = mcVersion,
                            ["uid"] = "net.minecraft"
                        }
                    },
                    ["cachedVersion"] = targetLoaderVersion,
                    ["uid"] = "net.fabricmc.fabric-loader",
                    ["version"] = targetLoaderVersion
                };
                componentsNode.Add(fabricComponent);
                _logger.Info($"Added new Fabric component to mmc-pack.json with version '{targetLoaderVersion}'");
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(mmcPackPath, rootObj.ToJsonString(options));

            // Also fetch and write the patch to patches/net.fabricmc.fabric-loader.json
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Fetching Fabric Patch...", $"Fetching Fabric {targetLoaderVersion} patch for UltiMC..."));
            string patchesDir = Path.Combine(instanceDir, "patches");
            Directory.CreateDirectory(patchesDir);

            string patchFile = Path.Combine(patchesDir, "net.fabricmc.fabric-loader.json");
            string patchUrl = $"https://meta.multimc.org/v1/net.fabricmc.fabric-loader/{targetLoaderVersion}.json";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, patchUrl);
                request.Headers.Add("User-Agent", "ModSync-UltiMC-Installer");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await HttpClient.SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    string patchJson = await response.Content.ReadAsStringAsync();
                    await File.WriteAllTextAsync(patchFile, patchJson);
                    _logger.Info($"Successfully wrote MultiMC patch to {patchFile}");
                }
                else
                {
                    _logger.Warning($"Could not fetch MultiMC patch from {patchUrl} (HTTP {(int)response.StatusCode}). mmc-pack.json was updated and UltiMC will download metadata on launch.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to download patch from {patchUrl}: {ex.Message}. mmc-pack.json was updated.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update Fabric Loader in UltiMC instance", ex);
            return (false, $"UltiMC Fabric update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads the Fabric Loader profile JSON from Fabric Meta API and installs it
    /// into the local .minecraft/versions/ directory.
    /// Also updates launcher_profiles.json if present.
    /// </summary>
    private async Task<(bool Success, string? Error)> WriteVanillaFabricProfileAsync(
        string mcDir,
        string versionsDir,
        string cleanMcVer,
        string cleanLoaderVer,
        Action<SyncProgressInfo>? progressCallback)
    {
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
    /// Helper to inspect JAR files in the mods or repo folder to extract the Minecraft version dependency.
    /// </summary>
    private string? TryDetectMcVersionFromModsFolder()
    {
        try
        {
            var searchDirs = new List<string>();
            string modsFolder = _configService.ResolvedModsFolder;
            if (Directory.Exists(modsFolder)) searchDirs.Add(modsFolder);

            string repoFolder = !string.IsNullOrWhiteSpace(CustomRepositoryDirectory)
                ? CustomRepositoryDirectory
                : _configService.ResolvedRepositoryFolder;
            if (Directory.Exists(repoFolder) && !searchDirs.Contains(repoFolder)) searchDirs.Add(repoFolder);

            foreach (var dir in searchDirs)
            {
                var jarFiles = Directory.GetFiles(dir, "*.jar", SearchOption.TopDirectoryOnly);

                // 1. Fast check: filename regex (e.g. AmbientSounds_FABRIC_v6.3.8_mc1.20.1.jar, AttributeFix-Fabric-1.20.1-21.0.4.jar)
                foreach (var jarPath in jarFiles)
                {
                    string fname = Path.GetFileName(jarPath);
                    var match = Regex.Match(fname, @"(?:mc|fabric)[-_]?(?<mc>1\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string ver = match.Groups["mc"].Value;
                        _logger.Info($"Detected Minecraft version '{ver}' from jar filename: {fname}");
                        return ver;
                    }
                }

                // 2. Deep check: read fabric.mod.json
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
                                            _logger.Info($"Detected Minecraft version '{match.Value}' from fabric.mod.json in {Path.GetFileName(jarPath)}");
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
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not scan mods/repo folders for Minecraft version: {ex.Message}");
        }

        return null;
    }
}
