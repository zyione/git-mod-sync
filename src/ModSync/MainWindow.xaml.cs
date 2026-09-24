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

    private bool _isBusy;
    private string? _lastBackupFolder;
    private UpdateInfo? _latestUpdateInfo;

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

        Loaded += MainWindow_Loaded;
        Activated += async (_, _) =>
        {
            if (!_isBusy)
            {
                await RefreshLocalModCountAsync();
            }
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateStatusCard();
        await RefreshAuthStatusAsync();
        await RefreshLocalModCountAsync();
        UpdateIgnoredModsUI();

        if (_configService.Config.AutoCheckUpdates)
        {
            _ = CheckForUpdatesBackgroundAsync();
        }
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
            string modsDir = _configService.ResolvedModsFolder;
            var allowedExts = _configService.Config.AllowedExtensions;
            bool syncSubdirs = _configService.Config.SyncSubdirectories;
            var searchOpt = syncSubdirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            int count = await Task.Run(() =>
            {
                if (!Directory.Exists(modsDir)) return 0;
                var extSet = new HashSet<string>(allowedExts, StringComparer.OrdinalIgnoreCase);
                int found = 0;
                foreach (var f in Directory.GetFiles(modsDir, "*.*", searchOpt))
                {
                    if (extSet.Contains(Path.GetExtension(f))) found++;
                }
                return found;
            });

            Dispatcher.Invoke(() =>
            {
                ModCountBadgeText.Text = $"{count} mod{(count == 1 ? "" : "s")}";
            });
        }
        catch
        {
            Dispatcher.Invoke(() =>
            {
                ModCountBadgeText.Text = "-- mods";
            });
        }
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
                var (gitStatus, modChanges, _) = await Task.Run(() => _syncService.CheckStatusAsync(UpdateProgress));
                if (!modChanges.HasChanges && !gitStatus.HasUpdates)
                {
                    ShowFeedback("✓ Mods are up to date.", false);
                    SetBusy(false);
                    return;
                }

                if (modChanges.HasChanges || modChanges.IgnoredCount > 0)
                {
                    var parts = new List<string>();
                    if (modChanges.AddedCount > 0) parts.Add($"+{modChanges.AddedCount} added");
                    if (modChanges.UpdatedCount > 0) parts.Add($"~{modChanges.UpdatedCount} updated");
                    if (modChanges.RemovedCount > 0) parts.Add($"-{modChanges.RemovedCount} removed");
                    if (modChanges.IgnoredCount > 0) parts.Add($"🛡️ {modChanges.IgnoredCount} excluded");
                    SyncConfirmSummaryText.Text = $"Updates detected: {string.Join(", ", parts)}";

                    var sb = new System.Text.StringBuilder();
                    foreach (var item in modChanges.Added) sb.AppendLine($"+ {item.RelativePath}");
                    foreach (var item in modChanges.Updated) sb.AppendLine($"~ {item.RelativePath}");
                    foreach (var item in modChanges.Removed) sb.AppendLine($"- {item.RelativePath}");
                    foreach (var item in modChanges.Ignored) sb.AppendLine($"🛡️ {item.RelativePath} (excluded / kept)");
                    SyncDetailsText.Text = sb.ToString().TrimEnd();
                }
                else
                {
                    SyncConfirmSummaryText.Text = $"Updates detected: Remote repository has {gitStatus.BehindCount} new commit(s).";
                    SyncDetailsText.Text = "Metadata or repository configuration update.";
                }

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
                if (summary != null && summary.HasChanges)
                {
                    ShowFeedback($"✓ Sync Complete: +{summary.AddedCount} added, ~{summary.UpdatedCount} updated, -{summary.RemovedCount} removed", false);
                }
                else
                {
                    ShowFeedback("✓ Mods are up to date.", false);
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
            var (gitStatus, modChanges, localCount) = await Task.Run(() => _syncService.CheckStatusAsync(UpdateProgress));

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

            StatusDialogPathText.Text = $"Mods Folder: {_configService.ResolvedModsFolder} ({localCount} installed)";

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
        SettingsModsFolderPathText.Text = _configService.ResolvedModsFolder;
        SettingsAppVersionText.Text = $"ModSync v{_updateService.CurrentVersion}";
        UpdateIgnoredModsUI();
        ShowModal(SettingsSheet);
    }

    private void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _configService.Config;
        cfg.RequireConfirmationBeforeSync = ConfirmSyncToggle.IsChecked == true;
        cfg.RequireConfirmationBeforePush = ConfirmPushToggle.IsChecked == true;
        cfg.AutoCheckUpdates = AutoCheckUpdatesToggle.IsChecked == true;
        _configService.Save();
        _logger.Info($"Preferences saved: ConfirmBeforeSync={cfg.RequireConfirmationBeforeSync}, ConfirmBeforePush={cfg.RequireConfirmationBeforePush}, AutoCheckUpdates={cfg.AutoCheckUpdates}");
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

    private void OpenCleanReinstallConfirm_Click(object sender, RoutedEventArgs e)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        CleanReinstallBackupPreviewText.Text = $"mods_backup_{timestamp}/";
        ShowModal(CleanReinstallConfirmSheet);
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

    private async Task CheckForUpdatesBackgroundAsync()
    {
        try
        {
            var info = await _updateService.CheckForUpdatesAsync();
            if (info.IsUpdateAvailable)
            {
                _latestUpdateInfo = info;
                Dispatcher.Invoke(() =>
                {
                    UpdateBannerText.Text = $"ModSync v{info.LatestVersion} available!";
                    UpdateBanner.Visibility = Visibility.Visible;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Background update check failed: {ex.Message}");
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
}
