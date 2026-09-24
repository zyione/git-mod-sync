using ModSync.Models;
using ModSync.Services;
using ModSync.Utils;

namespace ModSync;

internal class Program
{
    private static async Task Main(string[] args)
    {
        try { Console.Title = $"ModSync {ConsoleUI.AppVersion} - Minecraft Mod Manager"; } catch { }

        var logger = new LoggingService();
        logger.Info($"ModSync {ConsoleUI.AppVersion} started.");

        var configService = new ConfigService(logger);
        var (configLoaded, newlyCreated, configError) = configService.Load();

        var gitService = new GitService(logger);
        var authService = new AuthenticationService(logger);
        var mcCheckService = new MinecraftCheckService(logger);
        var syncService = new ModSyncService(configService, gitService, authService, mcCheckService, logger);

        if (newlyCreated)
        {
            ConsoleUI.PrintHeader("WELCOME TO MODSYNC");
            ConsoleUI.PrintWarning(configError ?? "config.json has been created.");
            Console.WriteLine();
            Console.WriteLine($"Configuration file created at:\n{configService.ConfigFilePath}");
            Console.WriteLine();
            Console.WriteLine("You can configure your GitHub repository now in Settings,");
            Console.WriteLine("or open config.json in any text editor.");
            ConsoleUI.Pause();
        }

        bool running = true;
        while (running)
        {
            try
            {
                var cfg = configService.Config;
                ConsoleUI.PrintBanner(cfg.Repository, cfg.Branch, configService.ResolvedModsFolder);
                ConsoleUI.PrintMenu();

                string? rawInput = Console.ReadLine();
                if (rawInput == null)
                {
                    // EOF reached (non-interactive or piped execution)
                    running = false;
                    break;
                }

                string choice = rawInput.Trim();
                switch (choice)
                {
                    case "1":
                        await HandleSyncModsAsync(syncService, configService);
                        break;
                    case "2":
                        await HandlePushModsAsync(syncService, authService);
                        break;
                    case "3":
                        await HandleCheckStatusAsync(syncService, configService, authService);
                        break;
                    case "4":
                        await HandleGitHubLoginAsync(authService);
                        break;
                    case "5":
                        await HandleSwitchRepositoryAsync(configService, gitService);
                        break;
                    case "6":
                        HandleSettings(configService);
                        break;
                    case "0":
                        running = false;
                        break;
                    default:
                        ConsoleUI.PrintWarning("Invalid option. Please choose 1, 2, 3, 4, 5, or 0.");
                        Thread.Sleep(800);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.Error("Unhandled exception in main loop", ex);
                ConsoleUI.PrintError($"An unexpected error occurred: {ex.Message}");
                if (Console.IsInputRedirected)
                {
                    running = false;
                    break;
                }
                ConsoleUI.Pause();
            }
        }

        logger.Info("ModSync exited cleanly.");
    }

    private static async Task HandleSyncModsAsync(ModSyncService syncService, ConfigService configService)
    {
        ConsoleUI.SafeClear();
        ConsoleUI.PrintHeader("SYNC MODS");
        Console.WriteLine("Connecting to GitHub and checking for mod updates...");
        Console.WriteLine();

        var (success, summary, message) = await syncService.SyncModsAsync(status =>
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"-> {status}");
            Console.ResetColor();
        });

        Console.WriteLine();
        if (success)
        {
            if (summary != null && summary.HasChanges)
            {
                ConsoleUI.PrintSuccess("========================================");
                ConsoleUI.PrintSuccess("             Sync Complete!");
                ConsoleUI.PrintSuccess("========================================");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Added:   {summary.AddedCount}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Updated: {summary.UpdatedCount}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Removed: {summary.RemovedCount}");
                Console.ResetColor();
                Console.WriteLine();
                ConsoleUI.PrintSuccess("Your mods are now up to date!");
            }
            else
            {
                ConsoleUI.PrintSuccess(message ?? "Your mods are already up to date.");
            }
        }
        else
        {
            ConsoleUI.PrintError(message ?? "Synchronization failed.");
        }

        ConsoleUI.Pause();
    }

    private static async Task HandlePushModsAsync(ModSyncService syncService, AuthenticationService authService)
    {
        ConsoleUI.SafeClear();
        ConsoleUI.PrintHeader("PUSH MODS");

        // Verify authentication
        var (isAuth, username, authMsg) = await authService.CheckAuthStatusAsync();
        if (!isAuth)
        {
            ConsoleUI.PrintWarning("GitHub login is required to push mod updates.");
            Console.WriteLine();
            if (ConsoleUI.Confirm("Would you like to log in now?", defaultYes: true))
            {
                await HandleGitHubLoginAsync(authService);
                (isAuth, username, _) = await authService.CheckAuthStatusAsync();
                if (!isAuth)
                {
                    ConsoleUI.PrintWarning("Push cancelled: Authentication not completed.");
                    ConsoleUI.Pause();
                    return;
                }
            }
            else
            {
                ConsoleUI.Pause();
                return;
            }
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Authenticated as: {username}");
        Console.ResetColor();
        Console.WriteLine();

        var (success, summary, message) = await syncService.PushModsAsync(status =>
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"-> {status}");
            Console.ResetColor();
        });

        Console.WriteLine();
        if (success)
        {
            if (summary != null && summary.HasChanges)
            {
                ConsoleUI.PrintSuccess("========================================");
                ConsoleUI.PrintSuccess("            Push Successful!");
                ConsoleUI.PrintSuccess("========================================");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Added:   {summary.AddedCount}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Updated: {summary.UpdatedCount}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Removed: {summary.RemovedCount}");
                Console.ResetColor();
                Console.WriteLine();
                ConsoleUI.PrintSuccess("Your mod changes have been pushed to GitHub!");
            }
            else
            {
                ConsoleUI.PrintInfo(message ?? "No changes to push.");
            }
        }
        else
        {
            ConsoleUI.PrintError(message ?? "Push failed.");
        }

        ConsoleUI.Pause();
    }

    private static async Task HandleCheckStatusAsync(
        ModSyncService syncService,
        ConfigService configService,
        AuthenticationService authService)
    {
        ConsoleUI.SafeClear();
        ConsoleUI.PrintHeader("MODSYNC STATUS");
        Console.WriteLine("Gathering status from GitHub and local files...");
        Console.WriteLine();

        var (gitStatus, modChanges, localModCount) = await syncService.CheckStatusAsync(status =>
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"-> {status}");
            Console.ResetColor();
        });

        var (isAuth, username, _) = await authService.CheckAuthStatusAsync();

        ConsoleUI.SafeClear();
        ConsoleUI.PrintHeader("MODSYNC STATUS");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("GitHub Repository:");
        Console.ResetColor();
        Console.WriteLine($"  {gitStatus.RepositoryUrl}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Branch:");
        Console.ResetColor();
        Console.WriteLine($"  {gitStatus.Branch}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Mods Folder:");
        Console.ResetColor();
        Console.WriteLine($"  {configService.ResolvedModsFolder}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Authentication (Admins):");
        Console.ResetColor();
        if (isAuth)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  Logged in as {username} (Push enabled)");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Not logged in (Player mode: Sync enabled, Push requires login)");
        }
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Remote Status:");
        Console.ResetColor();
        if (gitStatus.HasUpdates)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  {gitStatus.StatusMessage}");
        }
        else if (gitStatus.IsConnected)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  {gitStatus.StatusMessage}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  {gitStatus.StatusMessage}");
            if (!string.IsNullOrEmpty(gitStatus.ErrorMessage))
                Console.WriteLine($"  Detail: {gitStatus.ErrorMessage}");
        }
        Console.ResetColor();

        if (!string.IsNullOrEmpty(gitStatus.LocalCommitHash))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Local Commit:  {gitStatus.LocalCommitHash} ({gitStatus.LocalCommitDate:yyyy-MM-dd HH:mm})");
            if (!string.IsNullOrEmpty(gitStatus.LocalCommitMessage))
                Console.WriteLine($"                 \"{gitStatus.LocalCommitMessage}\"");
            if (!string.IsNullOrEmpty(gitStatus.RemoteCommitHash))
                Console.WriteLine($"  Remote Commit: {gitStatus.RemoteCommitHash} ({gitStatus.RemoteCommitDate:yyyy-MM-dd HH:mm})");
            Console.ResetColor();
        }
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Local Mods Folder:");
        Console.ResetColor();
        Console.WriteLine($"  {localModCount} mod file(s) found in {configService.Config.ModsFolder}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Differences with Repository:");
        Console.ResetColor();
        if (modChanges.HasChanges)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  {modChanges.TotalActionableChanges} pending change(s) between local mods and repo:");
            Console.ResetColor();

            if (modChanges.AddedCount > 0)
                Console.WriteLine($"  + {modChanges.AddedCount} added (available to download or push)");
            if (modChanges.RemovedCount > 0)
                Console.WriteLine($"  - {modChanges.RemovedCount} removed");
            if (modChanges.UpdatedCount > 0)
                Console.WriteLine($"  ~ {modChanges.UpdatedCount} updated");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  All local mods match the internal repository.");
            Console.ResetColor();
        }

        ConsoleUI.Pause();
    }

    private static async Task HandleGitHubLoginAsync(AuthenticationService authService)
    {
        bool inAuthMenu = true;
        while (inAuthMenu)
        {
            ConsoleUI.SafeClear();
            ConsoleUI.PrintHeader("GITHUB AUTHENTICATION");

            var (isValid, username, message) = await authService.CheckAuthStatusAsync();
            if (isValid)
            {
                ConsoleUI.PrintSuccess($"Status: Logged in as '{username}'");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("You have push access configured securely in Windows Credential Manager.");
                Console.ResetColor();
            }
            else
            {
                ConsoleUI.PrintWarning($"Status: {message}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("Note: Players who only sync mods do NOT need to log in.");
                Console.WriteLine("Authentication is only required for admins who push mods to GitHub.");
                Console.ResetColor();
            }

            Console.WriteLine();
            Console.WriteLine("[1] Sign in with GitHub Personal Access Token (PAT)");
            if (isValid)
            {
                Console.WriteLine("[2] Sign out / Clear saved credentials");
            }
            Console.WriteLine("[0] Back to main menu");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Select: ");
            Console.ResetColor();

            string? rawChoice = Console.ReadLine();
            if (rawChoice == null)
            {
                inAuthMenu = false;
                break;
            }

            string choice = rawChoice.Trim();
            switch (choice)
            {
                case "1":
                    await PerformTokenLoginAsync(authService);
                    break;
                case "2" when isValid:
                    authService.Logout();
                    ConsoleUI.PrintSuccess("Signed out successfully. Credentials cleared from Windows Credential Manager.");
                    ConsoleUI.Pause();
                    break;
                case "0":
                    inAuthMenu = false;
                    break;
                default:
                    ConsoleUI.PrintWarning("Invalid choice.");
                    Thread.Sleep(600);
                    break;
            }
        }
    }

    private static async Task PerformTokenLoginAsync(AuthenticationService authService)
    {
        ConsoleUI.SafeClear();
        ConsoleUI.PrintHeader("GITHUB LOGIN VIA TOKEN");

        Console.WriteLine("To push mods, you need a GitHub Personal Access Token (PAT):");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("1. Open: https://github.com/settings/tokens");
        Console.WriteLine("2. Generate a token with the 'repo' scope (write permission).");
        Console.WriteLine("3. Paste the token below.");
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Security Guarantee: Your token is stored securely in Windows Credential");
        Console.WriteLine("Manager using Windows DPAPI encryption. It will NEVER be written to");
        Console.WriteLine("config.json or any plaintext file.");
        Console.ResetColor();
        Console.WriteLine();

        string token = ConsoleUI.ReadPassword("Enter GitHub Token: ").Trim();
        if (string.IsNullOrEmpty(token))
        {
            ConsoleUI.PrintWarning("Login cancelled (empty token).");
            ConsoleUI.Pause();
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Verifying token with GitHub...");

        var (success, username, error) = await authService.LoginWithTokenAsync(token);
        if (success)
        {
            ConsoleUI.PrintSuccess($"Success! Logged in as: {username}");
            ConsoleUI.PrintSuccess("Your credentials have been securely stored in Windows Credential Manager.");
        }
        else
        {
            ConsoleUI.PrintError($"Login failed: {error}");
        }

        ConsoleUI.Pause();
    }

    private static async Task HandleSwitchRepositoryAsync(ConfigService configService, IGitService gitService)
    {
        bool inSwitchMenu = true;
        while (inSwitchMenu)
        {
            ConsoleUI.SafeClear();
            ConsoleUI.PrintHeader("SWITCH GIT REPOSITORY");

            var cfg = configService.Config;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Active Repository:");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  {cfg.Repository}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Branch: {cfg.Branch}");
            Console.ResetColor();
            Console.WriteLine();

            Console.WriteLine($"[1] Reset to Default Repository ({AppConfig.DefaultRepositoryUrl})");
            Console.WriteLine("[2] Enter New Repository URL");
            if (cfg.SavedRepositories != null && cfg.SavedRepositories.Count > 1)
            {
                Console.WriteLine($"[3] Choose from Saved Repositories ({cfg.SavedRepositories.Count} available)");
            }
            Console.WriteLine("[0] Back to main menu");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Select: ");
            Console.ResetColor();

            string? rawChoice = Console.ReadLine();
            if (rawChoice == null)
            {
                inSwitchMenu = false;
                break;
            }

            string choice = rawChoice.Trim();
            switch (choice)
            {
                case "1":
                    await ApplyRepoSwitchAsync(configService, gitService, AppConfig.DefaultRepositoryUrl, "main");
                    inSwitchMenu = false;
                    break;
                case "2":
                    Console.WriteLine();
                    Console.Write("Enter new GitHub repository URL (e.g. https://github.com/user/repo.git): ");
                    string? newUrl = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrWhiteSpace(newUrl))
                    {
                        Console.Write("Enter branch name (press ENTER for 'main'): ");
                        string? branch = Console.ReadLine()?.Trim();
                        if (string.IsNullOrWhiteSpace(branch)) branch = "main";

                        await ApplyRepoSwitchAsync(configService, gitService, newUrl, branch);
                        inSwitchMenu = false;
                    }
                    else
                    {
                        ConsoleUI.PrintWarning("Repository URL cannot be empty.");
                        Thread.Sleep(800);
                    }
                    break;
                case "3" when cfg.SavedRepositories != null && cfg.SavedRepositories.Count > 1:
                    await HandleSelectSavedRepoAsync(configService, gitService);
                    inSwitchMenu = false;
                    break;
                case "0":
                    inSwitchMenu = false;
                    break;
                default:
                    ConsoleUI.PrintWarning("Invalid option.");
                    Thread.Sleep(600);
                    break;
            }
        }
    }

    private static async Task HandleSelectSavedRepoAsync(ConfigService configService, IGitService gitService)
    {
        ConsoleUI.SafeClear();
        ConsoleUI.PrintHeader("SAVED REPOSITORIES");

        var repos = configService.Config.SavedRepositories;
        for (int i = 0; i < repos.Count; i++)
        {
            bool isCurrent = string.Equals(repos[i], configService.Config.Repository, StringComparison.OrdinalIgnoreCase);
            string marker = isCurrent ? " (Active)" : "";
            Console.WriteLine($"[{i + 1}] {repos[i]}{marker}");
        }
        Console.WriteLine("[0] Cancel");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Select repository: ");
        Console.ResetColor();

        string? input = Console.ReadLine()?.Trim();
        if (int.TryParse(input, out int index) && index >= 1 && index <= repos.Count)
        {
            string selectedUrl = repos[index - 1];
            await ApplyRepoSwitchAsync(configService, gitService, selectedUrl, null);
        }
    }

    private static async Task ApplyRepoSwitchAsync(ConfigService configService, IGitService gitService, string newUrl, string? branch)
    {
        Console.WriteLine();
        Console.WriteLine("Switching repository configuration...");

        bool saved = configService.SwitchRepository(newUrl, branch);
        if (saved)
        {
            // Reset internal repository clone so next sync clones the new repository cleanly
            await gitService.VerifyOrResetRemoteAsync(configService.ResolvedRepositoryFolder, newUrl);

            ConsoleUI.PrintSuccess("========================================");
            ConsoleUI.PrintSuccess("    Repository Switched Successfully!");
            ConsoleUI.PrintSuccess("========================================");
            Console.WriteLine();
            Console.WriteLine($"Active Repository: {newUrl}");
            Console.WriteLine($"Branch:            {configService.Config.Branch}");
            Console.WriteLine();
            ConsoleUI.PrintInfo("Select [1] Sync Mods on the main menu to download mods from this repository.");
        }
        else
        {
            ConsoleUI.PrintError("Failed to save new repository configuration.");
        }

        ConsoleUI.Pause();
    }

    private static void HandleSettings(ConfigService configService)
    {
        bool inSettings = true;
        while (inSettings)
        {
            ConsoleUI.SafeClear();
            ConsoleUI.PrintHeader("SETTINGS & CONFIGURATION");

            var cfg = configService.Config;
            Console.WriteLine($"Config file: {configService.ConfigFilePath}");
            Console.WriteLine();

            Console.WriteLine($"[1] Repository URL:            {cfg.Repository}");
            Console.WriteLine($"[2] Branch:                    {cfg.Branch}");
            Console.WriteLine($"[3] Mods Folder:               {cfg.ModsFolder} -> ({configService.ResolvedModsFolder})");
            Console.WriteLine($"[4] Confirm before Sync:       {(cfg.RequireConfirmationBeforeSync ? "Yes" : "No")}");
            Console.WriteLine($"[5] Confirm before Push:       {(cfg.RequireConfirmationBeforePush ? "Yes" : "No")}");
            Console.WriteLine($"[6] Max File Size Warning:     {cfg.WarnFileSizeMb} MB");
            Console.WriteLine($"[7] Hard File Size Limit:      {cfg.MaxFileSizeMb} MB");
            Console.WriteLine("[0] Back to main menu");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Select setting to edit: ");
            Console.ResetColor();

            string? rawChoice = Console.ReadLine();
            if (rawChoice == null)
            {
                inSettings = false;
                break;
            }

            string choice = rawChoice.Trim();
            switch (choice)
            {
                case "1":
                    Console.Write($"Enter new repository URL (current: {cfg.Repository}): ");
                    string? newRepo = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrWhiteSpace(newRepo))
                    {
                        cfg.Repository = newRepo;
                        configService.Save();
                        ConsoleUI.PrintSuccess("Repository URL updated.");
                    }
                    break;
                case "2":
                    Console.Write($"Enter branch name (current: {cfg.Branch}): ");
                    string? newBranch = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrWhiteSpace(newBranch))
                    {
                        cfg.Branch = newBranch;
                        configService.Save();
                        ConsoleUI.PrintSuccess("Branch updated.");
                    }
                    break;
                case "3":
                    Console.Write($"Enter mods folder path (current: {cfg.ModsFolder}): ");
                    string? newMods = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrWhiteSpace(newMods))
                    {
                        cfg.ModsFolder = newMods;
                        configService.Save();
                        ConsoleUI.PrintSuccess("Mods folder path updated.");
                    }
                    break;
                case "4":
                    cfg.RequireConfirmationBeforeSync = !cfg.RequireConfirmationBeforeSync;
                    configService.Save();
                    ConsoleUI.PrintSuccess($"Confirmation before sync set to: {(cfg.RequireConfirmationBeforeSync ? "Yes" : "No")}");
                    break;
                case "5":
                    cfg.RequireConfirmationBeforePush = !cfg.RequireConfirmationBeforePush;
                    configService.Save();
                    ConsoleUI.PrintSuccess($"Confirmation before push set to: {(cfg.RequireConfirmationBeforePush ? "Yes" : "No")}");
                    break;
                case "0":
                    inSettings = false;
                    break;
                default:
                    ConsoleUI.PrintWarning("Invalid choice.");
                    Thread.Sleep(600);
                    break;
            }
        }
    }
}
