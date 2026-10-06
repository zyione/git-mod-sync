using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>
/// Core synchronization engine between Minecraft mods folder,
/// internal Git repository, and remote GitHub.
/// Fully supports real-time progress callbacks for streaming download/upload percentages, details, speeds, and ETA.
/// </summary>
public class ModSyncService
{
    private readonly ConfigService _configService;
    private readonly IGitService _gitService;
    private readonly AuthenticationService _authService;
    private readonly Func<string?> _getToken;
    private readonly MinecraftCheckService _mcCheckService;
    private readonly ModIgnoreService _ignoreService;
    private readonly LoggingService _logger;
    private readonly Func<(bool IsRunning, string? Details)> _minecraftStatus;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public ModIgnoreService IgnoreService => _ignoreService;

    public SyncSummary InspectCachedFiles(SyncScope scope = SyncScope.All)
    {
        var config = _configService.Config;
        var local = ScanFolder(_configService.ResolvedModsFolder, config.AllowedExtensions, config.SyncSubdirectories, false, scope: scope);
        var repository = ScanFolder(_configService.ResolvedRepositoryFolder, config.AllowedExtensions, config.SyncSubdirectories, true, scope: scope);
        var summary = CalculateDifferences(repository, local);
        summary.Changes.AddRange(new PackSyncService(_configService).Plan(scope: scope).Changes);
        return summary;
    }

    public static string PlanFingerprint(SyncSummary summary) => JsonSerializer.Serialize(summary.Changes
        .OrderBy(c => c.RelativePath, StringComparer.Ordinal).ThenBy(c => c.DestinationPath, StringComparer.Ordinal)
        .Select(c => new { c.RelativePath, c.Type, c.DestinationPath, c.NewContent, c.OriginalContent,
            Source = c.SourceItem?.Sha256Hash, Target = c.TargetItem?.Sha256Hash, c.IsInternal }));

    public async Task<(bool Success, string? Error)> ApplyReviewedAsync(SyncSummary reviewed, SyncScope scope,
        Action<SyncProgressInfo>? progress = null)
    {
        // No repository refresh here: approval belongs to the exact preview, including removals.
        var (running, details) = _minecraftStatus();
        if (running) return (false, $"Close Minecraft before updating ({details}).");
        if (PlanFingerprint(reviewed) != PlanFingerprint(InspectCachedFiles(scope)))
            return (false, "Files or exclusions changed since review. Check again before applying updates.");
        var result = await ApplyChangesCoreAsync(reviewed, _configService.ResolvedRepositoryFolder,
            _configService.ResolvedModsFolder, progress);
        if (result.Success && scope.HasFlag(SyncScope.Mods))
        {
            _configService.Config.FirstSyncCompleted = true;
            if (!_configService.Save()) return (false, "Files updated, but settings could not be saved. Check again.");
        }
        return result;
    }

    public ModSyncService(
        ConfigService configService,
        IGitService gitService,
        AuthenticationService authService,
        MinecraftCheckService mcCheckService,
        ModIgnoreService ignoreService,
        LoggingService logger, Func<string?>? tokenProvider = null,
        Func<(bool IsRunning, string? Details)>? minecraftStatus = null)
    {
        _configService = configService;
        _gitService = gitService;
        _authService = authService;
        _getToken = tokenProvider ?? authService.GetStoredToken;
        _mcCheckService = mcCheckService;
        _ignoreService = ignoreService;
        _logger = logger;
        _minecraftStatus = minecraftStatus ?? mcCheckService.CheckIfMinecraftRunning;
    }

    /// <summary>
    /// Synchronizes mods from GitHub repository down into the local Minecraft mods folder.
    /// (GitHub -> Internal Repo -> ../mods)
    /// </summary>
    public async Task<(bool Success, SyncSummary? Summary, string? Message)> SyncModsAsync(
        Action<SyncProgressInfo>? progressCallback = null,
        bool forceIfMinecraftRunning = false,
        bool skipConfirmation = false,
        SyncScope scope = SyncScope.All)
    {
        var config = _configService.Config;
        string modsFolder = _configService.ResolvedModsFolder;
        string repoFolder = _configService.ResolvedRepositoryFolder;

        _logger.Info("Starting Sync Mods workflow...");
        progressCallback?.Invoke(SyncProgressInfo.Indeterminate(
            "Checking environment...",
            "Verifying Minecraft process status..."));

        // Step 1: Minecraft running check
        var (mcRunning, mcDetails) = _mcCheckService.CheckIfMinecraftRunning();
        if (mcRunning && !forceIfMinecraftRunning)
        {
            if (Environment.UserInteractive && !Console.IsInputRedirected)
            {
                Console.WriteLine();
                ConsoleUI.PrintWarning("========================================");
                ConsoleUI.PrintWarning("                WARNING");
                ConsoleUI.PrintWarning(" Minecraft appears to currently be running!");
                if (!string.IsNullOrEmpty(mcDetails))
                    ConsoleUI.PrintWarning($" Detected: {mcDetails}");
                ConsoleUI.PrintWarning(" Modifying mods while Minecraft is running may cause crashes or file lock errors.");
                ConsoleUI.PrintWarning(" Close Minecraft before syncing.");
                ConsoleUI.PrintWarning("========================================");
                Console.WriteLine();

                if (!ConsoleUI.Confirm("Continue anyway?", defaultYes: false))
                {
                    _logger.Info("Sync cancelled by user due to running Minecraft.");
                    return (false, null, "Sync cancelled: Please close Minecraft and try again.");
                }
            }
            else
            {
                return (false, null, $"Minecraft is currently running ({mcDetails}). Please close Minecraft and try again.");
            }
        }

        // Step 2: Ensure internal repository is up-to-date with remote branch
        progressCallback?.Invoke(SyncProgressInfo.Indeterminate(
            "Checking repository updates...",
            "Connecting to GitHub..."));
        string? token = _getToken();

        try { await new RepositorySyncService(_configService, _gitService).PrepareAsync(scope, download: true, token, progressCallback); }
        catch (Exception ex) { return (false, null, ex.Message); }

        // Step 3: Scan both directories
        if (scope.HasFlag(SyncScope.Mods)) PathUtils.EnsureDirectoryExists(modsFolder);

        var repoFiles = ScanFolder(
            repoFolder,
            config.AllowedExtensions,
            config.SyncSubdirectories,
            isRepoFolder: true,
            scope: scope,
            onProgress: (i, total, file) =>
            {
                double pct = ((double)i / Math.Max(1, total)) * 100.0;
                progressCallback?.Invoke(SyncProgressInfo.Determinate(
                    "Verifying repository mods...",
                    pct,
                    $"{file} ({i} of {total})"));
            });

        var localFiles = ScanFolder(
            modsFolder,
            config.AllowedExtensions,
            config.SyncSubdirectories,
            isRepoFolder: false,
            scope: scope,
            onProgress: (i, total, file) =>
            {
                double pct = ((double)i / Math.Max(1, total)) * 100.0;
                progressCallback?.Invoke(SyncProgressInfo.Determinate(
                    "Verifying local mods...",
                    pct,
                    $"{file} ({i} of {total})"));
            });

        // Step 4: Calculate differences
        progressCallback?.Invoke(SyncProgressInfo.Indeterminate(
            "Comparing mods...",
            "Calculating checksum differences..."));

        var summary = CalculateDifferences(sourceFiles: repoFiles, targetFiles: localFiles);
        try { summary.Changes.AddRange(new PackSyncService(_configService).Plan(scope: scope).Changes); }
        catch (Exception ex) { return (false, null, ex.Message); }

        if (!summary.HasChanges)
        {
            _logger.Info("Sync check completed: No changes detected. Mods are up to date.");
            progressCallback?.Invoke(SyncProgressInfo.Determinate("Mods up to date", 100, "All mods match GitHub repository."));
            return (true, summary, "Already up to date. No files were changed.");
        }

        // Step 5: Display summary and ask confirmation if required
        ConsoleUI.PrintChangesSummary(summary, "Checking for updates...");

        if (config.RequireConfirmationBeforeSync && !skipConfirmation)
        {
            if (Environment.UserInteractive && !Console.IsInputRedirected)
            {
                if (!ConsoleUI.Confirm("Apply these changes?", defaultYes: true))
                {
                    _logger.Info("Sync cancelled by user at confirmation prompt.");
                    return (false, null, "Sync cancelled: No files were changed.");
                }
            }
        }

        // Step 6: Safely apply changes to ../mods with progress, speeds, and ETA
        var applyResult = await ApplyChangesAsync(summary, sourceDir: repoFolder, targetDir: modsFolder, progressCallback);
        if (!applyResult.Success)
        {
            return (false, summary, applyResult.Error);
        }

        if (scope.HasFlag(SyncScope.Mods)) config.FirstSyncCompleted = true;
        _configService.Save();

        _logger.Info($"Sync complete: +{summary.AddedCount}, ~{summary.UpdatedCount}, -{summary.RemovedCount}");
        return (true, summary, null);
    }

    /// <summary>
    /// Pushes local mod modifications to the GitHub repository.
    /// (../mods -> Internal Repo -> GitHub)
    /// </summary>
    public async Task<(bool Success, SyncSummary? Summary, string? Message)> PushModsAsync(Action<SyncProgressInfo>? progressCallback = null, SyncScope scope = SyncScope.All)
    {
        string? token = _getToken();
        if (string.IsNullOrWhiteSpace(token)) return (false, null, "GitHub login is required to push updates.");
        var uploaded = new List<string>();
        var summary = new SyncSummary();
        try
        {
            var repositories = await PreparePushAsync(scope, token, progressCallback);
            // Validate every plan and size limit before changing any repository checkout.
            var plans = repositories.Select(repo => (Repo: repo, Plan: BuildPushPlan(repo.Scope))).ToArray();
            foreach (var (_, plan) in plans)
            {
                summary.Changes.AddRange(plan.Changes);
                foreach (var change in plan.Added.Concat(plan.Updated))
                    if (change.SourceItem != null && change.SourceItem.SizeBytes >= _configService.Config.MaxFileSizeMb * 1024 * 1024)
                        return (false, summary, $"File '{change.RelativePath}' exceeds the {_configService.Config.MaxFileSizeMb}MB upload limit.");
            }
            summary.PendingRepositories.AddRange(repositories.Where(repo => repo.PendingCommits > 0).Select(repo => repo.Label));
            if (!summary.HasChanges) return (true, summary, "No changes detected. Nothing to push.");
            ConsoleUI.PrintChangesSummary(summary, "Changes to upload:");
            if (_configService.Config.RequireConfirmationBeforePush && Environment.UserInteractive && !Console.IsInputRedirected && !ConsoleUI.Confirm("Push these changes to GitHub?", defaultYes: true))
                return (false, null, "Push cancelled: No files were changed on GitHub.");
            foreach (var (repo, plan) in plans.Where(p => p.Plan.HasChanges || p.Repo.PendingCommits > 0))
            {
                if (plan.HasChanges)
                {
                    var applied = await ApplyChangesAsync(plan, _configService.ResolvedModsFolder, repo.Folder, progressCallback);
                    if (!applied.Success) throw new IOException($"{repo.Label}: {applied.Error}");
                    var paths = plan.Changes.Where(c => c.Type is ChangeType.Added or ChangeType.Updated or ChangeType.Removed)
                        .Select(c => Path.GetRelativePath(repo.Folder, c.DestinationPath ?? Path.Combine(PackSyncService.RepositoryModsFolder(repo.Folder), c.RelativePath)).Replace('\\', '/')).Distinct().ToArray();
                    var committed = await _gitService.StageAndCommitAsync(repo.Folder,
                        $"{repo.Label} update: +{plan.AddedCount} added, -{plan.RemovedCount} removed, ~{plan.UpdatedCount} updated - {DateTime.Now:yyyy-MM-dd HH:mm}", progressCallback, paths);
                    if (!committed.Success) throw new IOException($"{repo.Label}: {committed.Error}");
                }
                var pushed = await _gitService.PushAsync(repo.Folder, repo.Branch, token, progressCallback);
                if (!pushed.Success) throw new IOException($"{repo.Label}: {pushed.Error}");
                uploaded.Add(repo.Label);
            }
            return (true, summary, null);
        }
        catch (Exception ex)
        {
            string completed = uploaded.Count == 0 ? "No repositories were uploaded." : $"Already uploaded: {string.Join(", ", uploaded)}.";
            return (false, summary, ex.Message + "\n" + completed);
        }
    }

    private SyncSummary BuildPushPlan(SyncScope scope)
    {
        var cfg = _configService.Config;
        var local = ScanFolder(_configService.ResolvedModsFolder, cfg.AllowedExtensions, cfg.SyncSubdirectories, isRepoFolder: false, scope: scope);
        var remote = ScanFolder(_configService.ResolvedRepositoryFolder, cfg.AllowedExtensions, cfg.SyncSubdirectories, isRepoFolder: true, scope: scope);
        var summary = CalculateDifferences(local, remote);
        summary.Changes.AddRange(new PackSyncService(_configService).Plan(push: true, scope: scope).Changes);
        return summary;
    }

    private async Task<IReadOnlyList<SyncRepository>> PreparePushAsync(SyncScope scope, string? token, Action<SyncProgressInfo>? progress)
    {
        var repositories = await new RepositorySyncService(_configService, _gitService).PrepareAsync(scope, download: false, token, progress, fetch: false);
        var statuses = await Task.WhenAll(repositories.Select(async repo => (Repo: repo, Status: await _gitService.GetStatusAsync(repo.Folder, repo.Url, repo.Branch, token, progress))));
        foreach (var (repo, status) in statuses)
        {
            if (!status.IsConnected || status.ErrorMessage != null) throw new IOException($"{repo.Label}: {status.ErrorMessage ?? "Could not verify remote status."}");
            if (scope == SyncScope.ResourcePackOrder)
            {
                // Never include previous asset commits in an order-only upload.
                if (status.AheadCount > 0) throw new IOException("Resource Packs has an unfinished upload. Finish the previous upload before using Push Order.");
                if (status.BehindCount > 0)
                {
                    // Refresh the cache only: keep the player's saved selection and installed packs intact.
                    await new RepositorySyncService(_configService, _gitService).PrepareAsync(scope, download: true, token, progress);
                    var refreshed = await _gitService.GetStatusAsync(repo.Folder, repo.Url, repo.Branch, token, progress);
                    if (!refreshed.IsConnected || refreshed.ErrorMessage != null || refreshed.BehindCount > 0 || refreshed.AheadCount > 0)
                        throw new IOException("Could not refresh the resource pack repository safely. Retry Push Order.");
                }
                continue;
            }
            if (status.BehindCount > 0) throw new IOException($"{repo.Label} has newer repository changes. Sync {repo.Label} before pushing this category; other categories do not need a sync.");
            if (scope != SyncScope.All && status.AheadCount > 0 && repo.Folder.Equals(_configService.ResolvedRepositoryFolder, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{repo.Label} has unpublished commits. Use Push Modpack to finish the previous upload before pushing only one category.");
        }
        return statuses.Select(item => item.Repo with { PendingCommits = item.Status.AheadCount }).ToArray();
    }

    public async Task<(bool Success, SyncSummary? Summary, string? Message)> GetPushChangesAsync(Action<SyncProgressInfo>? progressCallback = null, SyncScope scope = SyncScope.All)
    {
        try
        {
            var repositories = await PreparePushAsync(scope, _getToken(), progressCallback);
            var summary = BuildPushPlan(scope);
            summary.PendingRepositories.AddRange(repositories.Where(repo => repo.PendingCommits > 0).Select(repo => repo.Label));
            return (true, summary, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    /// <summary>
    /// Performs a fresh clean install of all repository mods into the local mods folder.
    /// Backs up mods, enabled packs, and pack settings under modsync_backups.
    /// Provides live per-file backup & install progress, transfer speed, and ETA.
    /// </summary>
    public async Task<(bool Success, string? BackupFolder, int RestoredCount, string? Error)> CleanReinstallAsync(
        Action<SyncProgressInfo>? progressCallback = null,
        bool forceIfMinecraftRunning = false, SyncScope scope = SyncScope.All)
    {
        if (scope is SyncScope.ResourcePacks or SyncScope.Shaders)
            return await CleanReinstallPacksAsync(scope, progressCallback, forceIfMinecraftRunning);
        if (scope is not (SyncScope.Mods or SyncScope.All))
            return (false, null, 0, "Select Mods, Resource Packs, Shaders, or the whole modpack to reinstall.");
        try
        {
            var config = _configService.Config;
            string modsFolder = _configService.ResolvedModsFolder;
            string repoFolder = _configService.ResolvedRepositoryFolder;
            string? token = _getToken();

            _logger.Info("Starting Clean Reinstall workflow...");
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Checking running processes...", "Verifying Minecraft state..."));

            // Check Minecraft running
            var (mcRunning, mcDetails) = _mcCheckService.CheckIfMinecraftRunning();
            if (mcRunning && !forceIfMinecraftRunning)
            {
                if (Environment.UserInteractive && !Console.IsInputRedirected)
                {
                    ConsoleUI.PrintWarning("Minecraft appears to be running. Close Minecraft before reinstalling mods.");
                    if (!ConsoleUI.Confirm("Continue anyway?", defaultYes: false))
                    {
                        return (false, null, 0, "Operation cancelled: Please close Minecraft.");
                    }
                }
                else
                {
                    return (false, null, 0, $"Minecraft is currently running ({mcDetails}). Please close Minecraft and try again.");
                }
            }

            await new RepositorySyncService(_configService, _gitService).PrepareAsync(scope, download: true, token, progressCallback);

            // Validate visual asset declarations before a clean reinstall changes any files.
            var packChanges = new PackSyncService(_configService).Plan(scope: scope);

            // Step 1: Backup existing mods if any exist (0% -> 40%)
            string backupDir = new PackBackupService(_configService).SnapshotForReinstall(scope);
            if (Directory.Exists(modsFolder))
            {
                var localMods = ScanFolder(modsFolder, config.AllowedExtensions, config.SyncSubdirectories, isRepoFolder: false);
                if (localMods.Count > 0)
                {
                    PathUtils.EnsureDirectoryExists(backupDir);

                    int i = 0;
                    foreach (var mod in localMods.Values)
                    {
                        if (_ignoreService.IsIgnored(mod.RelativePath, mod.FullPath))
                        {
                            _logger.Info($"Preserving local personal mod during clean reinstall: {mod.RelativePath}");
                            continue;
                        }

                        double pct = ((double)i / localMods.Count) * 40.0;
                        int remaining = localMods.Count - i;
                        string speedEta = $"{remaining} item{(remaining > 1 ? "s" : "")} left";

                        progressCallback?.Invoke(SyncProgressInfo.Determinate(
                            "Creating safety backup...",
                            pct,
                            $"Backing up: {mod.RelativePath} ({i + 1} of {localMods.Count})",
                            speedEta));

                        string dest = Path.Combine(backupDir, "mods", mod.RelativePath);
                        string destSubdir = Path.GetDirectoryName(dest) ?? backupDir;
                        PathUtils.EnsureDirectoryExists(destSubdir);
                        File.Move(mod.FullPath, dest, overwrite: true);
                        i++;
                    }
                    _logger.Info($"Backed up {localMods.Count} mods to {backupDir}");
                }
            }

            PathUtils.EnsureDirectoryExists(modsFolder);

            // Step 2: Copy all repo mods cleanly into ../mods with Speed & ETA (40% -> 100%)
            var repoFiles = ScanFolder(repoFolder, config.AllowedExtensions, config.SyncSubdirectories, isRepoFolder: true);
            long totalBytes = repoFiles.Values.Sum(f => f.SizeBytes);
            int totalFiles = repoFiles.Count;

            var stopwatch = Stopwatch.StartNew();
            long bytesCopied = 0;
            int copied = 0;

            foreach (var file in repoFiles.Values)
            {
                if (_ignoreService.IsIgnored(file.RelativePath, file.FullPath))
                {
                    _logger.Info($"Skipping excluded repository mod during clean reinstall: {file.RelativePath}");
                    continue;
                }

                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                double speed = elapsedSec > 0.2 ? bytesCopied / elapsedSec : 0;
                long remainingBytes = Math.Max(0, totalBytes - bytesCopied);
                double etaSec = speed > 1024 ? (double)remainingBytes / speed : 0;

                double pct = 40.0 + (totalBytes > 0
                    ? ((double)bytesCopied / totalBytes) * 60.0
                    : ((double)copied / Math.Max(1, totalFiles)) * 60.0);

                string speedEta = speed > 1024
                    ? $"{PathUtils.FormatSpeed(speed)} • {PathUtils.FormatEta(etaSec)}"
                    : $"{totalFiles - copied} item{(totalFiles - copied > 1 ? "s" : "")} left";

                progressCallback?.Invoke(SyncProgressInfo.Determinate(
                    "Installing fresh repository mods...",
                    pct,
                    $"{file.RelativePath} ({copied + 1} of {totalFiles})",
                    speedEta));

                string dest = Path.Combine(modsFolder, file.RelativePath);
                string destSubdir = Path.GetDirectoryName(dest) ?? modsFolder;
                PathUtils.EnsureDirectoryExists(destSubdir);
                File.Copy(file.FullPath, dest, overwrite: true);

                bytesCopied += file.SizeBytes;
                copied++;
            }

            progressCallback?.Invoke(SyncProgressInfo.Determinate(
                "Clean reinstall complete",
                100,
                $"Restored {copied} mods fresh from repository."));

            config.FirstSyncCompleted = true;
            _configService.Save();

            var packResult = await ApplyChangesCoreAsync(packChanges, repoFolder, modsFolder, progressCallback, backupPacks: false);
            if (!packResult.Success) return (false, backupDir, copied, packResult.Error);

            _logger.Info($"Clean reinstall complete: {copied} mods copied.");
            return (true, backupDir, copied, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Clean reinstall failed", ex);
            return (false, null, 0, $"Clean reinstall failed: {ex.Message}");
        }
    }

    private async Task<(bool Success, string? BackupFolder, int RestoredCount, string? Error)> CleanReinstallPacksAsync(
        SyncScope scope, Action<SyncProgressInfo>? progress, bool forceIfMinecraftRunning)
    {
        string? backup = null;
        try
        {
            if ((scope == SyncScope.ResourcePacks && !_configService.Config.SyncResourcePacks) ||
                (scope == SyncScope.Shaders && !_configService.Config.SyncShaderPacks))
                return (false, null, 0, "Enable this category in Settings first.");
            var (running, details) = _mcCheckService.CheckIfMinecraftRunning();
            if (running && !forceIfMinecraftRunning) return (false, null, 0, $"Minecraft is currently running ({details}). Please close Minecraft and try again.");
            progress?.Invoke(SyncProgressInfo.Indeterminate("Preparing clean reinstall…", "Refreshing the selected repository…"));
            await new RepositorySyncService(_configService, _gitService).PrepareAsync(scope, download: true, _getToken(), progress);
            var plan = new PackSyncService(_configService).Plan(scope: scope);
            // Recopy all shared files, including files that already match. Local-only personal files stay.
            foreach (var change in plan.Changes.Where(change => change.SourceItem != null && change.Type == ChangeType.Unchanged))
                change.Type = ChangeType.Updated;
            progress?.Invoke(SyncProgressInfo.Indeterminate("Creating safety backup…", "Saving the selected packs and their settings…"));
            backup = new PackBackupService(_configService).SnapshotForReinstall(scope);
            int packs = plan.Changes.Where(change => change.SourceItem != null).Select(change => change.RelativePath.Split('/')[1]).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var result = await ApplyChangesCoreAsync(plan, _configService.ResolvedRepositoryFolder, _configService.ResolvedModsFolder, progress, backupPacks: false);
            return (result.Success, backup, packs, result.Error);
        }
        catch (Exception ex) { return (false, backup, 0, $"Clean reinstall failed: {ex.Message}"); }
    }

    public async Task<(GitStatusInfo GitStatus, SyncSummary LocalModChanges, int LocalModCount, int RepoModCount)> CheckStatusAsync(Action<SyncProgressInfo>? progressCallback = null, SyncScope scope = SyncScope.All, bool refreshRepository = false)
    {
        var config = _configService.Config;
        string modsFolder = _configService.ResolvedModsFolder;
        string repoFolder = _configService.ResolvedRepositoryFolder;
        string? token = _getToken();

        progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Checking repository...", "Querying GitHub status..."));
        var repositories = await new RepositorySyncService(_configService, _gitService).PrepareAsync(scope, refreshRepository, token, progressCallback, fetch: false);
        var statuses = await Task.WhenAll(repositories.Select(async repo => (Repo: repo, Status: await _gitService.GetStatusAsync(repo.Folder, repo.Url, repo.Branch, token, progressCallback))));
        var gitStatus = statuses.FirstOrDefault().Status ?? new GitStatusInfo();
        if (statuses.Length > 1)
        {
            gitStatus = new GitStatusInfo {
                RepositoryUrl = string.Join("\n", statuses.Select(s => $"{s.Repo.Label}: {s.Repo.Url}")),
                Branch = string.Join(", ", statuses.Select(s => $"{s.Repo.Label}: {s.Repo.Branch}")),
                StatusMessage = string.Join(" • ", statuses.Select(s => $"{s.Repo.Label}: {s.Status.StatusMessage}")),
                IsCloned = statuses.All(s => s.Status.IsCloned), IsConnected = statuses.All(s => s.Status.IsConnected),
                ErrorMessage = statuses.Any(s => s.Status.ErrorMessage != null)
                    ? string.Join("\n", statuses.Where(s => s.Status.ErrorMessage != null).Select(s => s.Repo.Label + ": " + s.Status.ErrorMessage)) : null,
                AheadCount = statuses.Sum(s => s.Status.AheadCount), BehindCount = statuses.Sum(s => s.Status.BehindCount),
                LocalCommitHash = gitStatus.LocalCommitHash, LocalCommitDate = gitStatus.LocalCommitDate,
                RemoteCommitHash = gitStatus.RemoteCommitHash, RemoteCommitDate = gitStatus.RemoteCommitDate
            };
        }

        progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Scanning mods folder...", "Verifying local mod files..."));
        var localFiles = ScanFolder(modsFolder, config.AllowedExtensions, config.SyncSubdirectories, isRepoFolder: false, scope: scope);
        var repoFiles = ScanFolder(repoFolder, config.AllowedExtensions, config.SyncSubdirectories, isRepoFolder: true, scope: scope);

        var modChanges = CalculateDifferences(sourceFiles: repoFiles, targetFiles: localFiles);
        modChanges.Changes.AddRange(new PackSyncService(_configService).Plan(scope: scope).Changes);
        int repoCount = repoFiles.Count(f => !_ignoreService.IsIgnored(f.Key, f.Value.FullPath));

        progressCallback?.Invoke(SyncProgressInfo.Determinate("Status ready", 100, $"{localFiles.Count} local mods inspected."));
        return (gitStatus, modChanges, localFiles.Count, repoCount);
    }

    /// <summary>
    /// Gets the count of local mods installed in the mods folder, and the count of expected
    /// mods configured in the repository (either from local cloned repo or remote GitHub tree).
    /// </summary>
    public async Task<(int LocalCount, int? ExpectedCount)> GetModCountsAsync(bool fetchRemoteIfMissing = true)
    {
        var config = _configService.Config;
        string modsFolder = _configService.ResolvedModsFolder;
        string repoFolder = _configService.ResolvedRepositoryFolder;
        var allowedExts = config.AllowedExtensions;
        bool syncSubdirs = config.SyncSubdirectories;

        // 1. Local count
        int localCount = 0;
        if (Directory.Exists(modsFolder))
        {
            var searchOpt = syncSubdirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var extSet = new HashSet<string>(allowedExts, StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(modsFolder, "*.*", searchOpt))
            {
                if (extSet.Contains(Path.GetExtension(f)))
                {
                    localCount++;
                }
            }
        }

        // 2. Expected repository count
        int? expectedCount = null;

        // Case A: Cloned repo exists locally
        if (Directory.Exists(repoFolder) && Directory.Exists(Path.Combine(repoFolder, ".git")))
        {
            try
            {
                var repoFiles = ScanFolder(repoFolder, allowedExts, syncSubdirs, isRepoFolder: true, hashContents: false);
                expectedCount = repoFiles.Count(f => !_ignoreService.IsIgnored(f.Key, f.Value.FullPath));
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not scan local repository folder for mod count: {ex.Message}");
            }
        }

        // Case B: Repo not cloned yet locally -> Query GitHub Tree API
        if (fetchRemoteIfMissing && !expectedCount.HasValue && !string.IsNullOrWhiteSpace(config.Repository))
        {
            try
            {
                expectedCount = await FetchRemoteModCountFromGitHubAsync(config.Repository, config.Branch, allowedExts);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not fetch remote mod count via GitHub API: {ex.Message}");
            }
        }

        return (localCount, expectedCount);
    }

    private async Task<int?> FetchRemoteModCountFromGitHubAsync(string repoUrl, string branch, List<string> allowedExtensions)
    {
        var (owner, repo) = PathUtils.ParseGitHubOwnerAndRepo(repoUrl);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) return null;

        string branchName = string.IsNullOrWhiteSpace(branch) ? "main" : branch;
        string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{branchName}?recursive=1";

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("User-Agent", "ModSync-App");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        string? token = _getToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await HttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tree", out var treeProp) || treeProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var extSet = new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase);
        bool hasModsFolder = treeProp.EnumerateArray().Any(item =>
            item.TryGetProperty("path", out var p) && p.GetString() == "mods" &&
            item.TryGetProperty("type", out var t) && t.GetString() == "tree");
        int count = 0;
        foreach (var item in treeProp.EnumerateArray())
        {
            string type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (!string.Equals(type, "blob", StringComparison.OrdinalIgnoreCase)) continue;

            string path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            if (hasModsFolder)
            {
                if (!path.StartsWith("mods/", StringComparison.Ordinal)) continue;
                path = path[5..];
            }
            if (!_configService.Config.SyncSubdirectories && path.Contains('/')) continue;
            string ext = Path.GetExtension(path);
            if (extSet.Contains(ext) && !_ignoreService.IsIgnored(path))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Scans a directory for managed files matching allowedExtensions, computing SHA-256 hashes.
    /// Never touches unrelated files or .git internals.
    /// </summary>
    private Dictionary<string, ModFileItem> ScanFolder(
        string folderPath,
        List<string> allowedExtensions,
        bool searchSubdirs,
        bool isRepoFolder,
        Action<int, int, string>? onProgress = null,
        SyncScope scope = SyncScope.All,
        bool hashContents = true)
    {
        var result = new Dictionary<string, ModFileItem>(StringComparer.OrdinalIgnoreCase);
        if (!scope.HasFlag(SyncScope.Mods)) return result;

        if (isRepoFolder) folderPath = PackSyncService.RepositoryModsFolder(folderPath);

        if (!Directory.Exists(folderPath))
            return result;

        var searchOption = searchSubdirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            var allFiles = PackSyncService.SafeFiles(folderPath, searchSubdirs).ToArray();
            var extensionSet = new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase);

            var candidateFiles = new List<string>();
            foreach (var file in allFiles)
            {
                if (isRepoFolder && file.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
                    continue;

                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    continue;

                string ext = Path.GetExtension(file);
                if (!extensionSet.Contains(ext))
                    continue;

                candidateFiles.Add(file);
            }

            int index = 0;
            int total = candidateFiles.Count;

            foreach (var file in candidateFiles)
            {
                index++;
                string relative = Path.GetRelativePath(folderPath, file);
                onProgress?.Invoke(index, total, relative);

                var fi = new FileInfo(file);
                string hash = string.Empty;
                try
                {
                    if (hashContents) hash = HashUtils.ComputeSha256(file);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Could not verify '{file}'. No changes were applied.", ex);
                }

                result[relative] = new ModFileItem
                {
                    RelativePath = relative,
                    FullPath = file,
                    SizeBytes = fi.Length,
                    Sha256Hash = hash
                };
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to scan folder '{folderPath}'", ex);
            throw new IOException($"Could not scan '{folderPath}'. No changes were applied.", ex);
        }

        return result;
    }

    /// <summary>
    /// Compares two sets of files (source vs target) and builds a categorized SyncSummary.
    /// Excluded/ignored mods are marked as ChangeType.Ignored so they are never copied or deleted.
    /// </summary>
    private SyncSummary CalculateDifferences(
        Dictionary<string, ModFileItem> sourceFiles,
        Dictionary<string, ModFileItem> targetFiles)
    {
        var summary = new SyncSummary();

        // 1. Check all source files (either Added, Updated, Unchanged, or Ignored)
        foreach (var (relPath, sourceItem) in sourceFiles)
        {
            bool isIgnored = _ignoreService.IsIgnored(relPath, sourceItem.FullPath) || (targetFiles.TryGetValue(relPath, out var candidate) && _ignoreService.IsIgnored(relPath, candidate.FullPath));

            if (targetFiles.TryGetValue(relPath, out var targetItem))
            {
                // File exists in both - compare hashes
                if (string.Equals(sourceItem.Sha256Hash, targetItem.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                {
                    summary.Changes.Add(new ModChange
                    {
                        RelativePath = relPath,
                        Type = isIgnored ? ChangeType.Ignored : ChangeType.Unchanged,
                        SourceItem = sourceItem,
                        TargetItem = targetItem
                    });
                }
                else
                {
                    summary.Changes.Add(new ModChange
                    {
                        RelativePath = relPath,
                        Type = isIgnored ? ChangeType.Ignored : ChangeType.Updated,
                        SourceItem = sourceItem,
                        TargetItem = targetItem
                    });
                }
            }
            else
            {
                // In source but not in target -> Added (or Ignored)
                summary.Changes.Add(new ModChange
                {
                    RelativePath = relPath,
                    Type = isIgnored ? ChangeType.Ignored : ChangeType.Added,
                    SourceItem = sourceItem,
                    TargetItem = null
                });
            }
        }

        // 2. Check for Removed (in target but no longer in source)
        foreach (var (relPath, targetItem) in targetFiles)
        {
            if (!sourceFiles.ContainsKey(relPath))
            {
                bool isIgnored = _ignoreService.IsIgnored(relPath, targetItem.FullPath);
                summary.Changes.Add(new ModChange
                {
                    RelativePath = relPath,
                    Type = isIgnored ? ChangeType.Ignored : ChangeType.Removed,
                    SourceItem = null,
                    TargetItem = targetItem
                });
            }
        }

        return summary;
    }

    /// <summary>
    /// Safely applies file modifications with atomic copying (.tmp + rename),
    /// retry logic for locked files, and real-time progress callbacks with Speed & ETA.
    /// </summary>
    private Task<(bool Success, string? Error)> ApplyChangesAsync(
        SyncSummary summary,
        string sourceDir,
        string targetDir,
        Action<SyncProgressInfo>? progressCallback = null) => ApplyChangesCoreAsync(summary, sourceDir, targetDir, progressCallback);

    private async Task<(bool Success, string? Error)> ApplyChangesCoreAsync(
        SyncSummary summary, string sourceDir, string targetDir,
        Action<SyncProgressInfo>? progressCallback = null, bool backupPacks = true)
    {
        try
        {
            if (backupPacks && Path.GetFullPath(targetDir).Equals(Path.GetFullPath(_configService.ResolvedModsFolder), StringComparison.OrdinalIgnoreCase))
                new PackBackupService(_configService).BackupChanges(summary);

            var textChanges = summary.Changes.Where(c => c.NewContent != null).ToList();
            var itemsToCopy = summary.Changes.Where(c => c.NewContent == null && (c.Type == ChangeType.Added || c.Type == ChangeType.Updated)).ToList();
            var itemsToRemove = summary.Changes.Where(c => c.Type == ChangeType.Removed).ToList();

            int totalOps = itemsToCopy.Count + itemsToRemove.Count + textChanges.Count;
            long totalBytes = itemsToCopy.Sum(c => c.SourceItem?.SizeBytes ?? 0);

            var stopwatch = Stopwatch.StartNew();
            long bytesCopied = 0;
            int opsCompleted = 0;

            // 1. Process Added and Updated files first
            foreach (var change in itemsToCopy)
            {
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                double speed = elapsedSec > 0.2 ? bytesCopied / elapsedSec : 0;
                long remainingBytes = Math.Max(0, totalBytes - bytesCopied);
                double etaSec = speed > 1024 ? (double)remainingBytes / speed : 0;

                double pct = totalBytes > 0
                    ? ((double)bytesCopied / totalBytes) * 100.0
                    : ((double)opsCompleted / Math.Max(1, totalOps)) * 100.0;

                string speedEta = speed > 1024
                    ? $"{PathUtils.FormatSpeed(speed)} • {PathUtils.FormatEta(etaSec)}"
                    : $"{totalOps - opsCompleted} item{(totalOps - opsCompleted > 1 ? "s" : "")} left";

                string actionLabel = change.Type == ChangeType.Added ? "Adding" : "Updating";
                progressCallback?.Invoke(SyncProgressInfo.Determinate(
                    "Synchronizing files...",
                    pct,
                    $"{actionLabel}: {change.RelativePath} ({opsCompleted + 1} of {totalOps})",
                    speedEta));

                string sourcePath = change.SourceItem?.FullPath ?? Path.Combine(sourceDir, change.RelativePath);
                string targetRoot = targetDir == _configService.ResolvedRepositoryFolder
                    ? PackSyncService.RepositoryModsFolder(targetDir) : targetDir;
                string targetPath = change.DestinationPath ?? Path.Combine(targetRoot, change.RelativePath);
                string tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

                string targetSubdir = Path.GetDirectoryName(targetPath) ?? targetDir;
                PathUtils.EnsureDirectoryExists(targetSubdir);

                // Atomic copy: write to .tmp first, then move/overwrite
                bool copied = false;
                Exception? lastEx = null;

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        File.Copy(sourcePath, tempPath, overwrite: true);
                        if (change.SourceItem != null && HashUtils.ComputeSha256(tempPath) != change.SourceItem.Sha256Hash)
                            throw new InvalidDataException($"'{change.RelativePath}' changed during copying. Retry the sync.");

                        // Atomically replace target
                        if (File.Exists(targetPath))
                        {
                            File.Move(tempPath, targetPath, overwrite: true);
                        }
                        else
                        {
                            File.Move(tempPath, targetPath);
                        }

                        copied = true;
                        _logger.Info($"Copied: {change.RelativePath} ({change.Type})");
                        break;
                    }
                    catch (IOException ioEx)
                    {
                        lastEx = ioEx;
                        await Task.Delay(500); // Async retry delay
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        break;
                    }
                    finally
                    {
                        // Clean up temporary file if left behind
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    }
                }

                if (!copied)
                {
                    string err = $"Failed to copy '{change.RelativePath}'. The file may be in use by Minecraft or another program.";
                    _logger.Error(err, lastEx!);
                    return (false, err);
                }

                bytesCopied += change.SourceItem?.SizeBytes ?? 0;
                opsCompleted++;
            }

            // 2. Process Removed files
            foreach (var change in itemsToRemove)
            {
                opsCompleted++;
                double pct = ((double)opsCompleted / Math.Max(1, totalOps)) * 100.0;

                progressCallback?.Invoke(SyncProgressInfo.Determinate(
                    "Cleaning obsolete files...",
                    pct,
                    $"Removing: {change.RelativePath} ({opsCompleted} of {totalOps})",
                    $"{totalOps - opsCompleted} item{(totalOps - opsCompleted > 1 ? "s" : "")} left"));

                string targetRoot = targetDir == _configService.ResolvedRepositoryFolder
                    ? PackSyncService.RepositoryModsFolder(targetDir) : targetDir;
                string targetPath = change.DestinationPath ?? Path.Combine(targetRoot, change.RelativePath);
                if (File.Exists(targetPath))
                {
                    bool deleted = false;
                    Exception? lastEx = null;

                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            File.Delete(targetPath);
                            deleted = true;
                            _logger.Info($"Deleted obsolete mod: {change.RelativePath}");
                            break;
                        }
                        catch (IOException ioEx)
                        {
                            lastEx = ioEx;
                            await Task.Delay(500); // Async retry delay
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            break;
                        }
                    }

                    if (!deleted)
                    {
                        string err = $"Failed to remove old mod file '{change.RelativePath}'. The file may be locked.";
                        _logger.Error(err, lastEx!);
                        return (false, err);
                    }
                }
            }

            // Apply settings and ownership tracking only after every asset operation succeeds.
            foreach (var change in textChanges)
            {
                string path = change.DestinationPath!;
                if ((File.Exists(path) ? File.ReadAllText(path) : null) != change.OriginalContent)
                    throw new IOException($"'{change.RelativePath}' changed while syncing. Your newer settings were preserved; retry the sync.");
                PathUtils.EnsureDirectoryExists(Path.GetDirectoryName(path)!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, change.NewContent);
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }

            progressCallback?.Invoke(SyncProgressInfo.Determinate(
                "Sync complete",
                100,
                $"Applied {totalOps} change{(totalOps > 1 ? "s" : "")} successfully."));

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected error during file synchronization", ex);
            return (false, $"Synchronization failed: {ex.Message}");
        }
    }
}
