using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ModSync.Models;
using ModSync.Services;
using ModSync.Themes;
using ModSync.Utils;

namespace ModSync;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// Provides an Apple-inspired minimalist interface while delegating all
/// business logic directly to core Services.
/// </summary>
public partial class MainWindow : Window
{
    private readonly LoggingService _logger;
    private readonly ConfigService _configService;
    private readonly IGitService _gitService;
    private readonly AuthenticationService _authService;
    private readonly MinecraftCheckService _mcCheckService;
    private readonly ModIgnoreService _ignoreService;
    private readonly UpdateService _updateService;
    private readonly ModSyncService _syncService;
    private readonly FabricService _fabricService;

    private bool _isBusy;
    private string? _lastBackupFolder;
    private UpdateInfo? _latestUpdateInfo;
    private FabricStatusInfo? _currentFabricStatus;

    public MainWindow()
    {
        InitializeComponent();

        _logger = new LoggingService();
        _logger.Info("ModSync GUI started.");

        _configService = new ConfigService(_logger);
        _configService.Load();

        _gitService = new GitService(_logger);
        _authService = new AuthenticationService(_logger);
        _mcCheckService = new MinecraftCheckService(_logger);
        _ignoreService = new ModIgnoreService(_configService, _logger);
        _updateService = new UpdateService(_configService, _authService, _logger);
        _syncService = new ModSyncService(_configService, _gitService, _authService, _mcCheckService, _ignoreService, _logger);
        _fabricService = new FabricService(_configService, _logger, _authService);

        Loaded += MainWindow_Loaded;
        Activated += async (_, _) =>
        {
            if (!_isBusy)
            {
                await RefreshLocalModCountAsync();
                RefreshFabricStatusUI();
            }
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateStatusCard();

        // Force check for application updates on every startup
        _ = CheckForUpdatesOnStartupAsync();

        await RefreshAuthStatusAsync();
        await RefreshLocalModCountAsync();
        UpdateIgnoredModsUI();
        RefreshFabricStatusUI();
    }

    #region Status & Information

    private void UpdateStatusCard()
    {
        var cfg = _configService.Config;
        string repoName = GetShortRepoName(cfg.Repository);
        StatusSubText.Text = $"{repoName} • {cfg.Branch}";
        ModsFolderPathText.Text = _configService.ResolvedModsFolder;
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string modsDir = _configService.ResolvedModsFolder;
            PathUtils.EnsureDirectoryExists(modsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = modsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Could not open mods folder in File Explorer", ex);
            ShowFeedback($"Could not open folder: {ex.Message}", true);
        }
    }

    private async Task RefreshLocalModCountAsync()
    {
        try
        {
            var (localCount, expectedCount) = await Task.Run(() => _syncService.GetModCountsAsync());
            UpdateModCountBadge(localCount, expectedCount);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not refresh mod counts: {ex.Message}");
            Dispatcher.Invoke(() =>
            {
                ModCountBadgeText.Text = "-- mods";
                ModCountStatusIcon.Visibility = Visibility.Collapsed;
            });
        }
    }

    private void UpdateModCountBadge(int localCount, int? expectedCount)
    {
        Dispatcher.Invoke(() =>
        {
            var cfg = _configService.Config;
            string repoName = GetShortRepoName(cfg.Repository);

            if (expectedCount.HasValue)
            {
                int expected = expectedCount.Value;
                ModCountBadgeText.Text = $"{localCount} / {expected} mods";

                if (localCount == expected)
                {
                    ModCountStatusIcon.Visibility = Visibility.Visible;
                    ModCountBadgeBorder.ToolTip = $"All mods synchronized ({localCount} of {expected} installed)";
                    StatusHeadingText.Text = "All Mods Synchronized";
                    StatusDot.Fill = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                }
                else if (localCount < expected)
                {
                    ModCountStatusIcon.Visibility = Visibility.Collapsed;
                    int missing = expected - localCount;
                    ModCountBadgeBorder.ToolTip = $"{localCount} installed, {expected} expected in repository ({missing} missing - click Sync Mods)";
                    StatusHeadingText.Text = $"{missing} Mod Update{(missing == 1 ? "" : "s")} Available";
                    StatusDot.Fill = (System.Windows.Media.Brush)FindResource("AccentBrush");
                }
                else
                {
                    ModCountStatusIcon.Visibility = Visibility.Collapsed;
                    int extra = localCount - expected;
                    ModCountBadgeBorder.ToolTip = $"{localCount} installed, {expected} expected in repository ({extra} extra local mod{(extra == 1 ? "" : "s")})";
                    StatusHeadingText.Text = $"{extra} Custom Mod{(extra == 1 ? "" : "s")} Present";
                    StatusDot.Fill = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                }
            }
            else
            {
                ModCountStatusIcon.Visibility = Visibility.Collapsed;
                ModCountBadgeText.Text = $"{localCount} mod{(localCount == 1 ? "" : "s")}";
                ModCountBadgeBorder.ToolTip = $"{localCount} local mods installed";
                StatusHeadingText.Text = "Connected to GitHub";
                StatusDot.Fill = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            }

            StatusSubText.Text = $"{repoName} • {cfg.Branch}";
            ModsFolderPathText.Text = _configService.ResolvedModsFolder;
        });
    }

    private async Task RefreshAuthStatusAsync()
    {
        var (isValid, username, message) = await _authService.CheckAuthStatusAsync();
        if (isValid && !string.IsNullOrEmpty(username))
        {
            AuthButton.Content = $"@{username}";
            LogoutButton.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrEmpty(username) && message != null && message.Contains("Offline", StringComparison.OrdinalIgnoreCase))
        {
            AuthButton.Content = $"@{username} (Offline)";
            LogoutButton.Visibility = Visibility.Visible;
        }
        else
        {
            AuthButton.Content = "GitHub Login";
            LogoutButton.Visibility = Visibility.Collapsed;
        }
    }

    private static string GetShortRepoName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "No repository";
        try
        {
            string clean = url.Trim().TrimEnd('/');
            if (clean.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
            int lastSlash = clean.LastIndexOf('/');
            if (lastSlash > 0)
            {
                int secondLastSlash = clean.LastIndexOf('/', lastSlash - 1);
                if (secondLastSlash >= 0)
                {
                    return clean[(secondLastSlash + 1)..];
                }
                return clean[(lastSlash + 1)..];
            }
            return clean;
        }
        catch
        {
            return url;
        }
    }

    #endregion

    #region Sync Action

    private async void SyncMods_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        if (_configService.Config.RequireConfirmationBeforeSync)
        {
            SetBusy(true, "Checking updates from GitHub...");
            DismissFeedback();

            try
            {
                var (gitStatus, modChanges, _, _) = await Task.Run(() => _syncService.CheckStatusAsync(UpdateProgress));

                // Check if Fabric Loader also needs update
                bool fabricNeedsUpdate = false;
                if (_configService.Config.SyncFabricLoader)
                {
                    _currentFabricStatus = await _fabricService.DetectFabricStatusAsync();
                    fabricNeedsUpdate = _currentFabricStatus != null &&
                                        _currentFabricStatus.IsConfigured &&
                                        !_currentFabricStatus.IsUpToDate;
                }

                if (!modChanges.HasChanges && !gitStatus.HasUpdates && !fabricNeedsUpdate)
                {
                    ShowFeedback("✓ Mods and Fabric Loader are up to date.", false);
                    SetBusy(false);
                    return;
                }

                var parts = new List<string>();
                if (modChanges.AddedCount > 0) parts.Add($"+{modChanges.AddedCount} added");
                if (modChanges.UpdatedCount > 0) parts.Add($"~{modChanges.UpdatedCount} updated");
                if (modChanges.RemovedCount > 0) parts.Add($"-{modChanges.RemovedCount} removed");
                if (modChanges.IgnoredCount > 0) parts.Add($"🛡️ {modChanges.IgnoredCount} excluded");
                if (fabricNeedsUpdate && _currentFabricStatus != null)
                {
                    parts.Add($"⚙️ Fabric Loader {_currentFabricStatus.TargetLoaderVersion}");
                }

                if (parts.Count > 0)
                {
                    SyncConfirmSummaryText.Text = $"Updates detected: {string.Join(", ", parts)}";
                }
                else
                {
                    SyncConfirmSummaryText.Text = $"Updates detected: Remote repository has {gitStatus.BehindCount} new commit(s).";
                }

                var sb = new System.Text.StringBuilder();
                foreach (var item in modChanges.Added) sb.AppendLine($"+ {item.RelativePath}");
                foreach (var item in modChanges.Updated) sb.AppendLine($"~ {item.RelativePath}");
                foreach (var item in modChanges.Removed) sb.AppendLine($"- {item.RelativePath}");
                foreach (var item in modChanges.Ignored) sb.AppendLine($"🛡️ {item.RelativePath} (excluded / kept)");

                if (fabricNeedsUpdate && _currentFabricStatus != null)
                {
                    string fromVer = _currentFabricStatus.InstalledLoaderVersion ?? "Not Installed";
                    sb.AppendLine($"⚙️ Fabric Loader: {fromVer} → {_currentFabricStatus.TargetLoaderVersion} (will update)");
                }

                if (sb.Length == 0)
                {
                    sb.AppendLine("Metadata or repository configuration update.");
                }

                SyncDetailsText.Text = sb.ToString().TrimEnd();

                SetBusy(false);
                ShowModal(SyncConfirmSheet);
                return;
            }
            catch (Exception ex)
            {
                _logger.Error("Error checking sync diffs", ex);
                // Fall through to regular sync if diff precheck failed
                SetBusy(false);
            }
        }

        await PerformSyncAsync();
    }

    private async void ConfirmSync_Click(object sender, RoutedEventArgs e)
    {
        CloseModal();
        await PerformSyncAsync(skipConfirmation: true);
    }

    private async Task PerformSyncAsync(bool skipConfirmation = false)
    {
        SetBusy(true, "Syncing mods...");
        DismissFeedback();

        try
        {
            var (success, summary, message) = await Task.Run(() =>
                _syncService.SyncModsAsync(
                    UpdateProgress,
                    forceIfMinecraftRunning: false,
                    skipConfirmation: skipConfirmation
                )
            );

            if (!success && message != null && message.Contains("Minecraft is currently running", StringComparison.OrdinalIgnoreCase))
            {
                var result = MessageBox.Show(
                    $"{message}\n\nModifying mods while Minecraft is running may corrupt files or cause crashes.\n\nDo you want to continue anyway?",
                    "Minecraft Running Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    SetBusy(true, "Forcing mod sync...");
                    (success, summary, message) = await Task.Run(() =>
                        _syncService.SyncModsAsync(
                            UpdateProgress,
                            forceIfMinecraftRunning: true,
                            skipConfirmation: true
                        )
                    );
                }
                else
                {
                    ShowFeedback("Sync cancelled: Close Minecraft and try again.", true);
                    return;
                }
            }

            if (success)
            {
                bool fabricUpdated = false;
                string? fabricTarget = null;

                if (_configService.Config.SyncFabricLoader)
                {
                    try
                    {
                        // Refresh status from freshly synced repo
                        _currentFabricStatus = await _fabricService.DetectFabricStatusAsync();
                        if (_currentFabricStatus != null && _currentFabricStatus.IsConfigured && !_currentFabricStatus.IsUpToDate)
                        {
                            fabricTarget = _currentFabricStatus.TargetLoaderVersion;
                            string mcVer = !string.IsNullOrWhiteSpace(_currentFabricStatus.MinecraftVersion)
                                ? _currentFabricStatus.MinecraftVersion
                                : "1.21.1";

                            UpdateProgress(SyncProgressInfo.Indeterminate("Syncing Fabric Loader...", $"Installing Fabric Loader {fabricTarget}..."));

                            var (fSuccess, fError) = await Task.Run(async () =>
                                await _fabricService.InstallFabricLoaderAsync(mcVer, fabricTarget!, progress => UpdateProgress(progress))
                            );

                            if (fSuccess)
                            {
                                fabricUpdated = true;
                                _logger.Info($"Fabric Loader {fabricTarget} installed during mod sync.");
                            }
                            else
                            {
                                _logger.Warning($"Fabric Loader update during sync failed: {fError}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Error updating Fabric Loader during mod sync", ex);
                    }
                }

                if (summary != null && summary.HasChanges)
                {
                    string fb = $"✓ Sync Complete: +{summary.AddedCount} added, ~{summary.UpdatedCount} updated, -{summary.RemovedCount} removed";
                    if (fabricUpdated) fb += $" • Fabric Loader {fabricTarget} updated";
                    ShowFeedback(fb, false);
                }
                else if (fabricUpdated)
                {
                    ShowFeedback($"✓ Sync Complete: Fabric Loader updated to {fabricTarget}.", false);
                }
                else
                {
                    ShowFeedback("✓ Mods and Fabric Loader are up to date.", false);
                }
                SetStatusDot(true);
            }
            else
            {
                ShowFeedback(message ?? "Synchronization failed.", true);
                SetStatusDot(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("GUI Sync exception", ex);
            ShowFeedback($"Error: {ex.Message}", true);
            SetStatusDot(false);
        }
        finally
        {
            await RefreshLocalModCountAsync();
            RefreshFabricStatusUI();
            SetBusy(false);
        }
    }

    #endregion

    #region Push Action

    private async void PushMods_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        // Check authentication first
        var (isValid, _, authMsg) = await _authService.CheckAuthStatusAsync();
        if (!isValid)
        {
            if (authMsg != null && authMsg.Contains("Offline", StringComparison.OrdinalIgnoreCase))
            {
                ShowFeedback("Push unavailable: Cannot connect to GitHub. Please check your internet connection.", true);
                return;
            }

            ShowLoginModal();
            return;
        }

        await RefreshLocalModCountAsync();
        SetBusy(true, "Checking local modifications...");
        DismissFeedback();

        try
        {
            var (success, modChanges, error) = await Task.Run(() => _syncService.GetPushChangesAsync(UpdateProgress));
            if (!success || modChanges == null)
            {
                ShowFeedback(error ?? "Failed to inspect modifications.", true);
                SetBusy(false);
                return;
            }

            if (!modChanges.HasChanges)
            {
                ShowFeedback("No mod changes detected. Nothing to push.", false);
                SetBusy(false);
                return;
            }

            // Show confirmation sheet modal
            var pushParts = new List<string>();
            if (modChanges.AddedCount > 0) pushParts.Add($"+{modChanges.AddedCount} added");
            if (modChanges.UpdatedCount > 0) pushParts.Add($"~{modChanges.UpdatedCount} updated");
            if (modChanges.RemovedCount > 0) pushParts.Add($"-{modChanges.RemovedCount} removed");
            if (modChanges.IgnoredCount > 0) pushParts.Add($"🛡️ {modChanges.IgnoredCount} excluded");
            PushConfirmSummaryText.Text = $"Changes to upload: {string.Join(", ", pushParts)}";

            var sb = new System.Text.StringBuilder();
            foreach (var item in modChanges.Added) sb.AppendLine($"+ {item.RelativePath}");
            foreach (var item in modChanges.Updated) sb.AppendLine($"~ {item.RelativePath}");
            foreach (var item in modChanges.Removed) sb.AppendLine($"- {item.RelativePath}");
            foreach (var item in modChanges.Ignored) sb.AppendLine($"🛡️ {item.RelativePath} (excluded / kept local)");
            PushDetailsText.Text = sb.ToString().TrimEnd();

            SetBusy(false);
            ShowModal(PushConfirmSheet);
        }
        catch (Exception ex)
        {
            _logger.Error("Error checking push diffs", ex);
            ShowFeedback($"Error: {ex.Message}", true);
            SetBusy(false);
        }
    }

    private async void ConfirmPush_Click(object sender, RoutedEventArgs e)
    {
        CloseModal();
        SetBusy(true, "Pushing mod updates to GitHub...");

        try
        {
            var (success, summary, message) = await Task.Run(() =>
                _syncService.PushModsAsync(UpdateProgress)
            );

            if (success)
            {
                ShowFeedback("✓ Successfully pushed mod updates to GitHub!", false);
            }
            else
            {
                ShowFeedback(message ?? "Push failed.", true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("GUI Push exception", ex);
            ShowFeedback($"Push error: {ex.Message}", true);
        }
        finally
        {
            await RefreshLocalModCountAsync();
            SetBusy(false);
        }
    }

    #endregion

    #region Check Status Action

    private async void CheckStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true, "Checking status from GitHub...");

        try
        {
            var (gitStatus, modChanges, localCount, repoCount) = await Task.Run(() => _syncService.CheckStatusAsync(UpdateProgress));
            UpdateModCountBadge(localCount, repoCount);

            StatusDialogRepoText.Text = $"Repository:  {gitStatus.RepositoryUrl}";
            StatusDialogBranchText.Text = $"Branch:      {gitStatus.Branch} ({gitStatus.StatusMessage})";

            if (!string.IsNullOrEmpty(gitStatus.LocalCommitHash))
            {
                if (gitStatus.BehindCount > 0 && !string.IsNullOrEmpty(gitStatus.RemoteCommitHash))
                {
                    StatusDialogCommitText.Text = $"Local:       {gitStatus.LocalCommitHash} ({gitStatus.LocalCommitDate:yyyy-MM-dd HH:mm})\nRemote:      {gitStatus.RemoteCommitHash} ({gitStatus.RemoteCommitDate:yyyy-MM-dd HH:mm}) [Behind by {gitStatus.BehindCount}]";
                }
                else
                {
                    StatusDialogCommitText.Text = $"Commit:      {gitStatus.LocalCommitHash} ({gitStatus.LocalCommitDate:yyyy-MM-dd HH:mm})";
                }
            }
            else
            {
                StatusDialogCommitText.Text = "Commit:      (not cloned yet)";
            }

            string syncComp = localCount == repoCount
                ? "Synchronized"
                : localCount < repoCount ? $"{repoCount - localCount} missing" : $"{localCount - repoCount} extra";
            StatusDialogPathText.Text = $"Mods Folder: {_configService.ResolvedModsFolder}\nInstalled:   {localCount} of {repoCount} mods ({syncComp})";

            SetBusy(false);
            ShowModal(StatusSheet);
        }
        catch (Exception ex)
        {
            _logger.Error("GUI Status check failed", ex);
            ShowFeedback($"Status check failed: {ex.Message}", true);
            SetBusy(false);
        }
    }

    #endregion

    #region GitHub Login Modal

    private void Auth_Click(object sender, RoutedEventArgs e)
    {
        ShowLoginModal();
    }

    private void ShowLoginModal()
    {
        TokenInputBox.Password = string.Empty;
        ShowModal(LoginSheet);
    }

    private async void SubmitLogin_Click(object sender, RoutedEventArgs e)
    {
        string token = TokenInputBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowFeedback("Please enter a token.", true);
            return;
        }

        SubmitLoginButton.IsEnabled = false;

        try
        {
            var (success, user, error) = await _authService.LoginWithTokenAsync(token);
            if (success)
            {
                CloseModal();
                await RefreshAuthStatusAsync();
                ShowFeedback($"✓ Logged in as @{user}", false);
            }
            else
            {
                MessageBox.Show(error ?? "Login failed.", "GitHub Login", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            SubmitLoginButton.IsEnabled = true;
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _authService.Logout();
        CloseModal();
        await RefreshAuthStatusAsync();
        ShowFeedback("Signed out. Credentials cleared.", false);
    }

    #endregion

    #region Switch Repository Modal

    private void SwitchRepo_Click(object sender, RoutedEventArgs e)
    {
        RepoUrlInputBox.Text = _configService.Config.Repository;
        ShowModal(SwitchRepoSheet);
    }

    private void ResetDefaultRepo_Click(object sender, RoutedEventArgs e)
    {
        RepoUrlInputBox.Text = AppConfig.DefaultRepositoryUrl;
    }

    private async void SubmitSwitchRepo_Click(object sender, RoutedEventArgs e)
    {
        string newUrl = RepoUrlInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newUrl))
        {
            return;
        }

        CloseModal();
        SetBusy(true, "Switching repository...");

        try
        {
            bool switched = _configService.SwitchRepository(newUrl);
            if (switched)
            {
                await _gitService.VerifyOrResetRemoteAsync(_configService.ResolvedRepositoryFolder, newUrl);
                UpdateStatusCard();
                ShowFeedback($"✓ Switched to {GetShortRepoName(newUrl)}. Click 'Sync Mods' to update.", false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Switch repo error", ex);
            ShowFeedback($"Failed to switch repository: {ex.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    #endregion

    #region Settings & Clean Reinstall

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _configService.Config;
        ConfirmSyncToggle.IsChecked = cfg.RequireConfirmationBeforeSync;
        ConfirmPushToggle.IsChecked = cfg.RequireConfirmationBeforePush;
        AutoCheckUpdatesToggle.IsChecked = cfg.AutoCheckUpdates;
        SyncFabricLoaderToggle.IsChecked = cfg.SyncFabricLoader;
        SettingsModsFolderPathText.Text = _configService.ResolvedModsFolder;
        SettingsAppVersionText.Text = $"ModSync v{_updateService.CurrentVersion}";

        string? fabricVer = _currentFabricStatus?.TargetLoaderVersion ?? cfg.FabricLoaderVersion;
        string? source = _currentFabricStatus?.VersionSource;
        if (!string.IsNullOrWhiteSpace(fabricVer))
        {
            SettingsFabricStatusText.Text = !string.IsNullOrWhiteSpace(source) ? source : $"Enforced: v{fabricVer}";
            SettingsFabricVersionBadge.Text = $"v{fabricVer}";
            SettingsFabricVersionBadge.Foreground = (Brush)FindResource("AccentBrush");
        }
        else
        {
            SettingsFabricStatusText.Text = "Not configured (Disabled)";
            SettingsFabricVersionBadge.Text = "Disabled";
            SettingsFabricVersionBadge.Foreground = (Brush)FindResource("SecondaryTextBrush");
        }

        UpdateIgnoredModsUI();
        ShowModal(SettingsSheet);
    }

    private void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _configService.Config;
        cfg.RequireConfirmationBeforeSync = ConfirmSyncToggle.IsChecked == true;
        cfg.RequireConfirmationBeforePush = ConfirmPushToggle.IsChecked == true;
        cfg.AutoCheckUpdates = AutoCheckUpdatesToggle.IsChecked == true;
        cfg.SyncFabricLoader = SyncFabricLoaderToggle.IsChecked == true;
        _configService.Save();
        _logger.Info($"Preferences saved: ConfirmBeforeSync={cfg.RequireConfirmationBeforeSync}, ConfirmBeforePush={cfg.RequireConfirmationBeforePush}, AutoCheckUpdates={cfg.AutoCheckUpdates}, SyncFabricLoader={cfg.SyncFabricLoader}");
        RefreshFabricStatusUI();
    }

    private void ChangeModsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Minecraft mods Folder",
            InitialDirectory = _configService.ResolvedModsFolder
        };

        if (dialog.ShowDialog() == true)
        {
            string chosenPath = dialog.FolderName;
            if (!string.IsNullOrWhiteSpace(chosenPath) && Directory.Exists(chosenPath))
            {
                _configService.SetModsFolder(chosenPath);
                SettingsModsFolderPathText.Text = _configService.ResolvedModsFolder;
                UpdateStatusCard();
                _ = RefreshLocalModCountAsync();
                ShowFeedback($"✓ Mods folder set to: {_configService.ResolvedModsFolder}", false);
            }
        }
    }

    private void ResetModsFolder_Click(object sender, RoutedEventArgs e)
    {
        _configService.SetModsFolder("./mods");
        SettingsModsFolderPathText.Text = _configService.ResolvedModsFolder;
        UpdateStatusCard();
        _ = RefreshLocalModCountAsync();
        ShowFeedback("✓ Mods folder reset to default (./mods)", false);
    }

    private bool _reinstallOpenedFromSettings = false;

    private void OpenCleanReinstallConfirm_Click(object sender, RoutedEventArgs e)
    {
        _reinstallOpenedFromSettings = true;
        ReinstallBackButton.Visibility = Visibility.Visible;
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        CleanReinstallBackupPreviewText.Text = $"mods_backup_{timestamp}/";
        ShowModal(CleanReinstallConfirmSheet);
    }

    private void OpenCleanReinstallFromMain_Click(object sender, RoutedEventArgs e)
    {
        _reinstallOpenedFromSettings = false;
        ReinstallBackButton.Visibility = Visibility.Collapsed;
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        CleanReinstallBackupPreviewText.Text = $"mods_backup_{timestamp}/";
        ShowModal(CleanReinstallConfirmSheet);
    }

    private void BackFromReinstall_Click(object sender, RoutedEventArgs e)
    {
        if (_reinstallOpenedFromSettings)
            ShowModal(SettingsSheet);
        else
            CloseModal();
    }

    private void BackToSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowModal(SettingsSheet);
    }

    private async void ConfirmCleanReinstall_Click(object sender, RoutedEventArgs e)
    {
        CloseModal();
        DismissFeedback();
        SetBusy(true, "Backing up old mods and performing clean reinstall...");

        try
        {
            var (success, backupDir, count, error) = await Task.Run(() =>
                _syncService.CleanReinstallAsync(
                    UpdateProgress,
                    forceIfMinecraftRunning: false
                )
            );

            if (!success && error != null && error.Contains("Minecraft is currently running", StringComparison.OrdinalIgnoreCase))
            {
                var result = MessageBox.Show(
                    $"{error}\n\nModifying mods while Minecraft is running may corrupt files or cause crashes.\n\nDo you want to continue anyway?",
                    "Minecraft Running Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    SetBusy(true, "Forcing clean reinstall...");
                    (success, backupDir, count, error) = await Task.Run(() =>
                        _syncService.CleanReinstallAsync(
                            UpdateProgress,
                            forceIfMinecraftRunning: true
                        )
                    );
                }
                else
                {
                    ShowFeedback("Clean reinstall cancelled: Close Minecraft and try again.", true);
                    return;
                }
            }

            if (success)
            {
                SetStatusDot(true);
                _lastBackupFolder = backupDir;

                if (!string.IsNullOrEmpty(backupDir))
                {
                    string backupFolderName = Path.GetFileName(backupDir);
                    ShowFeedback($"✓ Clean Reinstall Complete! {count} mods restored. Backup: {backupFolderName}", false, showBackupAction: true);
                }
                else
                {
                    ShowFeedback($"✓ Clean Reinstall Complete! {count} mods installed fresh.", false);
                }
            }
            else
            {
                SetStatusDot(false);
                ShowFeedback(error ?? "Clean reinstall failed.", true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("GUI Clean Reinstall exception", ex);
            ShowFeedback($"Clean reinstall error: {ex.Message}", true);
            SetStatusDot(false);
        }
        finally
        {
            await RefreshLocalModCountAsync();
            UpdateStatusCard();
            SetBusy(false);
        }
    }

    private void FeedbackAction_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastBackupFolder) && Directory.Exists(_lastBackupFolder))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastBackupFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.Error("Could not open backup directory", ex);
                ShowFeedback($"Could not open backup directory: {ex.Message}", true);
            }
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string logsDir = Path.Combine(PathUtils.GetAppDirectory(), "logs");
            PathUtils.EnsureDirectoryExists(logsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = logsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Could not open logs folder", ex);
            ShowFeedback($"Could not open logs folder: {ex.Message}", true);
        }
    }

    private void OpenWebRepo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string repo = _configService.Config.Repository;
            if (string.IsNullOrWhiteSpace(repo)) return;
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                repo = repo[..^4];
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = repo,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Could not open repository URL", ex);
            ShowFeedback($"Could not open browser: {ex.Message}", true);
        }
    }

    #endregion

    #region Helpers & Window Controls

    private void UpdateProgress(SyncProgressInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            if (ProgressArea.Visibility != Visibility.Visible)
            {
                ProgressArea.Visibility = Visibility.Visible;
            }

            ProgressStatusText.Text = !string.IsNullOrWhiteSpace(info.Status) ? info.Status : "Working...";

            if (info.Percentage.HasValue)
            {
                TaskProgressBar.IsIndeterminate = false;
                TaskProgressBar.Value = Math.Clamp(info.Percentage.Value, 0.0, 100.0);
                ProgressPercentageText.Text = $"{(int)Math.Round(info.Percentage.Value)}%";
                ProgressPercentageText.Visibility = Visibility.Visible;
            }
            else
            {
                TaskProgressBar.IsIndeterminate = true;
                ProgressPercentageText.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(info.Details))
            {
                ProgressDetailsText.Text = info.Details;
                ProgressDetailsText.Visibility = Visibility.Visible;
            }
            else
            {
                ProgressDetailsText.Text = string.Empty;
                ProgressDetailsText.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(info.SpeedOrEta))
            {
                ProgressSpeedEtaText.Text = info.SpeedOrEta;
                ProgressSpeedEtaText.Visibility = Visibility.Visible;
            }
            else
            {
                ProgressSpeedEtaText.Text = string.Empty;
                ProgressSpeedEtaText.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        SyncModsButton.IsEnabled = !busy;
        PushModsButton.IsEnabled = !busy;

        if (busy)
        {
            ProgressArea.Visibility = Visibility.Visible;
            ProgressStatusText.Text = message ?? "Working...";
            TaskProgressBar.IsIndeterminate = true;
            ProgressPercentageText.Visibility = Visibility.Collapsed;
            ProgressDetailsText.Visibility = Visibility.Collapsed;
            ProgressSpeedEtaText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ProgressArea.Visibility = Visibility.Collapsed;
        }
    }

    private void SetStatusDot(bool ok)
    {
        StatusDot.Fill = ok
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("ErrorBrush");
    }

    private void ShowFeedback(string message, bool isError, bool showBackupAction = false)
    {
        FeedbackMessageText.Text = message;
        FeedbackMessageText.Foreground = isError
            ? (Brush)FindResource("ErrorBrush")
            : (Brush)FindResource("PrimaryTextBrush");

        FeedbackActionButton.Visibility = showBackupAction ? Visibility.Visible : Visibility.Collapsed;
        FeedbackCard.Visibility = Visibility.Visible;
    }

    private void DismissFeedback()
    {
        FeedbackCard.Visibility = Visibility.Collapsed;
        FeedbackActionButton.Visibility = Visibility.Collapsed;
    }

    private void DismissFeedback_Click(object sender, RoutedEventArgs e)
    {
        DismissFeedback();
    }

    private void ShowModal(FrameworkElement sheet)
    {
        PushConfirmSheet.Visibility = Visibility.Collapsed;
        LoginSheet.Visibility = Visibility.Collapsed;
        SwitchRepoSheet.Visibility = Visibility.Collapsed;
        StatusSheet.Visibility = Visibility.Collapsed;
        SettingsSheet.Visibility = Visibility.Collapsed;
        CleanReinstallConfirmSheet.Visibility = Visibility.Collapsed;
        SyncConfirmSheet.Visibility = Visibility.Collapsed;
        UpdateSheet.Visibility = Visibility.Collapsed;
        IgnoredModsSheet.Visibility = Visibility.Collapsed;
        FabricUpdateConfirmSheet.Visibility = Visibility.Collapsed;

        sheet.Visibility = Visibility.Visible;
        ModalBackdrop.Visibility = Visibility.Visible;
    }

    private void CloseModal()
    {
        PushConfirmSheet.Visibility = Visibility.Collapsed;
        LoginSheet.Visibility = Visibility.Collapsed;
        SwitchRepoSheet.Visibility = Visibility.Collapsed;
        StatusSheet.Visibility = Visibility.Collapsed;
        SettingsSheet.Visibility = Visibility.Collapsed;
        CleanReinstallConfirmSheet.Visibility = Visibility.Collapsed;
        SyncConfirmSheet.Visibility = Visibility.Collapsed;
        UpdateSheet.Visibility = Visibility.Collapsed;
        IgnoredModsSheet.Visibility = Visibility.Collapsed;
        FabricUpdateConfirmSheet.Visibility = Visibility.Collapsed;
        ModalBackdrop.Visibility = Visibility.Collapsed;
    }

    private void CloseModal_Click(object sender, RoutedEventArgs e)
    {
        CloseModal();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.ToggleTheme();
    }

    #endregion

    #region App Updates & Mod Exclusions

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            _logger.Info("Performing startup force check for updates...");
            var info = await _updateService.CheckForUpdatesAsync();
            if (info.IsUpdateAvailable)
            {
                _latestUpdateInfo = info;
                Dispatcher.Invoke(() =>
                {
                    UpdateBannerText.Text = $"ModSync v{info.LatestVersion} available!";
                    UpdateBanner.Visibility = Visibility.Visible;

                    // If no other modal is currently active, surface the update sheet so the user can update immediately
                    if (ModalBackdrop.Visibility != Visibility.Visible)
                    {
                        ShowUpdateSheet(info);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Startup update check failed: {ex.Message}");
        }
    }

    private void OpenUpdateModal_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdateInfo != null)
        {
            ShowUpdateSheet(_latestUpdateInfo);
        }
    }

    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void ShowUpdateSheet(UpdateInfo info)
    {
        UpdateSheetVersionText.Text = $"ModSync v{info.LatestVersion} is available (Current: v{info.CurrentVersion})";
        UpdateSheetReleaseTitle.Text = !string.IsNullOrWhiteSpace(info.ReleaseTitle) ? info.ReleaseTitle : $"ModSync v{info.LatestVersion}";
        UpdateSheetSizeText.Text = info.FormattedSize;
        UpdateSheetNotesText.Text = !string.IsNullOrWhiteSpace(info.ReleaseNotes) ? info.ReleaseNotes.Trim() : "No release notes provided.";
        ShowModal(UpdateSheet);
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckAppUpdatesButton.IsEnabled = false;
        DismissFeedback();

        try
        {
            var info = await _updateService.CheckForUpdatesAsync();
            if (info.IsUpdateAvailable)
            {
                _latestUpdateInfo = info;
                CloseModal();
                ShowUpdateSheet(info);
            }
            else if (!string.IsNullOrEmpty(info.ErrorMessage))
            {
                ShowFeedback($"Update check failed: {info.ErrorMessage}", true);
            }
            else
            {
                ShowFeedback($"✓ ModSync is up to date (v{info.CurrentVersion}).", false);
            }
        }
        finally
        {
            CheckAppUpdatesButton.IsEnabled = true;
        }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdateInfo == null || string.IsNullOrWhiteSpace(_latestUpdateInfo.DownloadUrl))
        {
            ShowFeedback("No direct update download asset (.exe) available for this release.", true);
            return;
        }

        CloseModal();
        SetBusy(true, "Downloading ModSync update...");

        try
        {
            var (success, error) = await _updateService.DownloadAndApplyUpdateAsync(_latestUpdateInfo.DownloadUrl, UpdateProgress);
            if (!success)
            {
                SetBusy(false);
                ShowFeedback(error ?? "Update failed.", true);
            }
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _logger.Error("Update execution failed", ex);
            ShowFeedback($"Update failed: {ex.Message}", true);
        }
    }

    private void OpenReleaseWeb_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdateInfo != null && !string.IsNullOrWhiteSpace(_latestUpdateInfo.ReleaseHtmlUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _latestUpdateInfo.ReleaseHtmlUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.Error("Could not open release URL", ex);
            }
        }
    }

    private void UpdateIgnoredModsUI()
    {
        var patterns = _ignoreService.GetEffectivePatterns();
        SettingsIgnoredModsText.Text = $"{patterns.Count} active exclusion rule{(patterns.Count == 1 ? "" : "s")}";
    }

    private void OpenIgnoredModsModal_Click(object sender, RoutedEventArgs e)
    {
        RefreshIgnoredModsList();
        PopulateDetectedMods();
        ShowModal(IgnoredModsSheet);
    }

    private void RefreshIgnoredModsList()
    {
        IgnoredRulesContainer.Children.Clear();
        var patterns = _ignoreService.GetEffectivePatterns();

        if (patterns.Count == 0)
        {
            NoIgnoredRulesText.Visibility = Visibility.Visible;
            IgnoredRulesContainer.Children.Add(NoIgnoredRulesText);
            return;
        }

        NoIgnoredRulesText.Visibility = Visibility.Collapsed;

        foreach (var pattern in patterns)
        {
            var cardBorder = new Border
            {
                Background = (Brush)FindResource("CardBackgroundBrush"),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 8, 6),
                Margin = new Thickness(0, 2, 0, 2)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconText = new TextBlock
            {
                Text = "🛡️",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            leftStack.Children.Add(iconText);

            var nameText = new TextBlock
            {
                Text = pattern,
                FontFamily = (FontFamily)FindResource("SystemFont"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("PrimaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            leftStack.Children.Add(nameText);

            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var badge = new Border
                {
                    Background = (Brush)FindResource("SecondaryButtonBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                {
                    Text = "Pattern",
                    FontSize = 9.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = (Brush)FindResource("SecondaryTextBrush")
                };
                leftStack.Children.Add(badge);
            }

            var deleteButton = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("GhostButtonStyle"),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
                Foreground = (Brush)FindResource("ErrorBrush"),
                Tag = pattern,
                ToolTip = "Remove this exclusion rule"
            };
            deleteButton.Click += (s, args) =>
            {
                if (s is Button btn && btn.Tag is string pat)
                {
                    _ignoreService.RemovePattern(pat);
                    RefreshIgnoredModsList();
                    PopulateDetectedMods();
                    UpdateIgnoredModsUI();
                }
            };

            Grid.SetColumn(leftStack, 0);
            Grid.SetColumn(deleteButton, 1);

            row.Children.Add(leftStack);
            row.Children.Add(deleteButton);

            cardBorder.Child = row;
            IgnoredRulesContainer.Children.Add(cardBorder);
        }
    }

    private void PopulateDetectedMods()
    {
        DetectedModsComboBox.Items.Clear();
        var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan local mods
        try
        {
            string modsDir = _configService.ResolvedModsFolder;
            if (Directory.Exists(modsDir))
            {
                foreach (var file in Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
                {
                    detected.Add(Path.GetFileName(file));
                }
            }
        }
        catch { }

        // Scan repo mods
        try
        {
            string repoDir = _configService.ResolvedRepositoryFolder;
            if (Directory.Exists(repoDir))
            {
                foreach (var file in Directory.GetFiles(repoDir, "*.jar", SearchOption.TopDirectoryOnly))
                {
                    detected.Add(Path.GetFileName(file));
                }
            }
        }
        catch { }

        var sorted = detected.OrderBy(d => d).ToList();
        int addedCount = 0;
        foreach (var mod in sorted)
        {
            if (!_ignoreService.IsIgnored(mod))
            {
                DetectedModsComboBox.Items.Add(mod);
                addedCount++;
            }
        }

        if (addedCount > 0)
        {
            DetectedModsComboBox.SelectedIndex = 0;
            DetectedModsComboBox.IsEnabled = true;
        }
        else
        {
            DetectedModsComboBox.Items.Add(detected.Count > 0 ? "All detected mods excluded" : "No .jar mods found");
            DetectedModsComboBox.SelectedIndex = 0;
            DetectedModsComboBox.IsEnabled = false;
        }
    }

    private void AddIgnorePattern_Click(object sender, RoutedEventArgs e)
    {
        string pattern = NewIgnorePatternInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(pattern)) return;

        _ignoreService.AddPattern(pattern);
        NewIgnorePatternInput.Text = string.Empty;
        RefreshIgnoredModsList();
        PopulateDetectedMods();
        UpdateIgnoredModsUI();
    }

    private void NewIgnorePatternInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddIgnorePattern_Click(sender, e);
        }
    }

    private void ExcludeSelectedDetectedMod_Click(object sender, RoutedEventArgs e)
    {
        if (DetectedModsComboBox.IsEnabled &&
            DetectedModsComboBox.SelectedItem is string selectedMod &&
            !string.IsNullOrWhiteSpace(selectedMod))
        {
            _ignoreService.AddPattern(selectedMod);
            RefreshIgnoredModsList();
            PopulateDetectedMods();
            UpdateIgnoredModsUI();
        }
    }

    private void CloseIgnoredMods_Click(object sender, RoutedEventArgs e)
    {
        ShowModal(SettingsSheet);
    }

    #endregion

    #region Fabric Loader Version Sync

    private void RefreshFabricStatusUI()
    {
        _ = RefreshFabricStatusUIAsync();
    }

    private async Task RefreshFabricStatusUIAsync()
    {
        try
        {
            // Initial fast local check
            _currentFabricStatus = _fabricService.DetectFabricStatus();
            ApplyFabricStatusToUI(_currentFabricStatus);

            // Asynchronous check (fetches fabric-version.txt from repository if remote/not yet cloned)
            var remoteStatus = await _fabricService.DetectFabricStatusAsync();
            if (remoteStatus.IsConfigured || _currentFabricStatus?.IsConfigured == true)
            {
                _currentFabricStatus = remoteStatus;
                ApplyFabricStatusToUI(_currentFabricStatus);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error refreshing Fabric status in UI", ex);
        }
    }

    private void ApplyFabricStatusToUI(FabricStatusInfo? status)
    {
        if (!_configService.Config.SyncFabricLoader)
        {
            FabricCard.Visibility = Visibility.Collapsed;
            return;
        }

        FabricCard.Visibility = Visibility.Visible;

        if (status == null || !status.IsConfigured)
        {
            FabricGameVersionText.Text = !string.IsNullOrWhiteSpace(status?.MinecraftVersion)
                ? $" • Minecraft {status.MinecraftVersion}"
                : " • Minecraft";
            FabricInstalledVersionText.Text = status?.InstalledLoaderVersion ?? "Checking...";
            FabricTargetVersionText.Text = "Checking repo...";
            FabricStatusDot.Fill = (Brush)FindResource("SecondaryTextBrush");
            FabricUpToDateBadge.Visibility = Visibility.Collapsed;
            FabricUpdateButton.Visibility = Visibility.Collapsed;
            return;
        }
        string target = status.TargetLoaderVersion ?? "Unknown";
        string installed = status.InstalledLoaderVersion ?? "Not Installed";
        string mcVer = !string.IsNullOrWhiteSpace(status.MinecraftVersion)
            ? $" • Minecraft {status.MinecraftVersion}"
            : " • Minecraft";

        FabricGameVersionText.Text = mcVer;
        FabricInstalledVersionText.Text = installed;
        FabricTargetVersionText.Text = target;

        if (!string.IsNullOrWhiteSpace(status.VersionSource))
        {
            FabricTargetVersionText.ToolTip = $"Version source: {status.VersionSource}";
        }

        if (!status.IsMinecraftFound)
        {
            FabricStatusDot.Fill = (Brush)FindResource("SecondaryTextBrush");
            FabricUpToDateBadge.Visibility = Visibility.Collapsed;
            FabricUpdateButton.Visibility = Visibility.Visible;
            FabricUpdateButton.Content = "Install";
            FabricInstalledVersionText.Foreground = (Brush)FindResource("SecondaryTextBrush");
        }
        else if (status.IsUpToDate)
        {
            FabricStatusDot.Fill = (Brush)FindResource("SuccessBrush");
            FabricUpToDateBadge.Visibility = Visibility.Visible;
            FabricUpdateButton.Visibility = Visibility.Collapsed;
            FabricInstalledVersionText.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            FabricStatusDot.Fill = (Brush)FindResource("WarningBrush");
            FabricUpToDateBadge.Visibility = Visibility.Collapsed;
            FabricUpdateButton.Visibility = Visibility.Visible;
            FabricUpdateButton.Content = status.InstalledLoaderVersion == null ? "Install Fabric" : "Update Fabric";
            FabricInstalledVersionText.Foreground = (Brush)FindResource("WarningBrush");
        }
    }

    private void FabricUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        if (_currentFabricStatus == null)
        {
            _currentFabricStatus = _fabricService.DetectFabricStatus();
        }

        string installed = _currentFabricStatus.InstalledLoaderVersion ?? "Not installed";
        string target = _currentFabricStatus.TargetLoaderVersion ?? _configService.Config.FabricLoaderVersion ?? "0.16.9";
        string mcVer = !string.IsNullOrWhiteSpace(_currentFabricStatus.MinecraftVersion) ? _currentFabricStatus.MinecraftVersion : "1.21.1";

        FabricModalCurrentVerText.Text = installed;
        FabricModalTargetVerText.Text = target;
        FabricModalMcVerText.Text = mcVer;
        FabricModalInstancePathText.Text = _fabricService.GetMinecraftDirectory();

        ShowModal(FabricUpdateConfirmSheet);
    }

    private async void ConfirmFabricUpdate_Click(object sender, RoutedEventArgs e)
    {
        CloseModal();
        if (_isBusy) return;

        if (_currentFabricStatus == null)
        {
            _currentFabricStatus = _fabricService.DetectFabricStatus();
        }

        string target = _currentFabricStatus.TargetLoaderVersion ?? _configService.Config.FabricLoaderVersion ?? "0.16.9";
        string mcVer = !string.IsNullOrWhiteSpace(_currentFabricStatus.MinecraftVersion) ? _currentFabricStatus.MinecraftVersion : "1.21.1";

        SetBusy(true, "Installing Fabric Loader...");

        try
        {
            var (success, error) = await Task.Run(async () =>
            {
                return await _fabricService.InstallFabricLoaderAsync(mcVer, target, progress =>
                {
                    UpdateProgress(progress);
                });
            });

            if (success)
            {
                ShowFeedback($"Fabric Loader {target} installed successfully!", false);
                RefreshFabricStatusUI();
            }
            else
            {
                ShowFeedback($"Fabric update failed: {error}", true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to update Fabric Loader", ex);
            ShowFeedback($"Fabric update failed: {ex.Message}", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    #endregion
}
