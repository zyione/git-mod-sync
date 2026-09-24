using System.Diagnostics;
using System.Windows;
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
    private readonly ModSyncService _syncService;

    private bool _isBusy;
    private string? _lastBackupFolder;

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
        _syncService = new ModSyncService(_configService, _gitService, _authService, _mcCheckService, _logger);

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateStatusCard();
        await RefreshAuthStatusAsync();
        await RefreshLocalModCountAsync();
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
            int count = await Task.Run(() =>
            {
                if (Directory.Exists(modsDir))
                {
                    return Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly).Length;
                }
                return 0;
            });

            ModCountBadgeText.Text = $"{count} mod{(count == 1 ? "" : "s")}";
        }
        catch
        {
            ModCountBadgeText.Text = "-- mods";
        }
    }

    private async Task RefreshAuthStatusAsync()
    {
        var (isValid, username, _) = await _authService.CheckAuthStatusAsync();
        if (isValid && !string.IsNullOrEmpty(username))
        {
            AuthButton.Content = $"@{username}";
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
                var (_, modChanges, _) = await Task.Run(() => _syncService.CheckStatusAsync(UpdateProgress));
                if (!modChanges.HasChanges)
                {
                    ShowFeedback("✓ Mods are up to date.", false);
                    SetBusy(false);
                    return;
                }

                SyncConfirmSummaryText.Text = $"Updates detected: +{modChanges.AddedCount} added, ~{modChanges.UpdatedCount} updated, -{modChanges.RemovedCount} removed";

                var sb = new System.Text.StringBuilder();
                foreach (var item in modChanges.Added) sb.AppendLine($"+ {item.RelativePath}");
                foreach (var item in modChanges.Updated) sb.AppendLine($"~ {item.RelativePath}");
                foreach (var item in modChanges.Removed) sb.AppendLine($"- {item.RelativePath}");
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
        var (isValid, _, _) = await _authService.CheckAuthStatusAsync();
        if (!isValid)
        {
            ShowLoginModal();
            return;
        }

        SetBusy(true, "Checking local modifications...");
        DismissFeedback();

        try
        {
            var (_, modChanges, _) = await Task.Run(() => _syncService.CheckStatusAsync(UpdateProgress));

            if (!modChanges.HasChanges)
            {
                ShowFeedback("No mod changes detected. Nothing to push.", false);
                SetBusy(false);
                return;
            }

            // Show confirmation sheet modal
            PushConfirmSummaryText.Text = $"Changes detected: +{modChanges.AddedCount} added, ~{modChanges.UpdatedCount} updated, -{modChanges.RemovedCount} removed";

            var sb = new System.Text.StringBuilder();
            foreach (var item in modChanges.Added) sb.AppendLine($"+ {item.RelativePath}");
            foreach (var item in modChanges.Updated) sb.AppendLine($"~ {item.RelativePath}");
            foreach (var item in modChanges.Removed) sb.AppendLine($"- {item.RelativePath}");
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
                StatusDialogCommitText.Text = $"Commit:      {gitStatus.LocalCommitHash} ({gitStatus.LocalCommitDate:yyyy-MM-dd HH:mm})";
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
        SettingsModsFolderPathText.Text = _configService.ResolvedModsFolder;
        ShowModal(SettingsSheet);
    }

    private void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _configService.Config;
        cfg.RequireConfirmationBeforeSync = ConfirmSyncToggle.IsChecked == true;
        cfg.RequireConfirmationBeforePush = ConfirmPushToggle.IsChecked == true;
        _configService.Save();
        _logger.Info($"Preferences saved: ConfirmBeforeSync={cfg.RequireConfirmationBeforeSync}, ConfirmBeforePush={cfg.RequireConfirmationBeforePush}");
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

        sheet.Visibility = Visibility.Visible;
        ModalBackdrop.Visibility = Visibility.Visible;
    }

    private void CloseModal()
    {
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
}
