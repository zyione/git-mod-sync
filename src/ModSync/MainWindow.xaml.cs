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
/// Provides a state-driven desktop interface while delegating all
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
    private SyncScope _syncScope = SyncScope.All;
    private SyncScope _pushScope = SyncScope.All;
    private SyncScope _reinstallScope = SyncScope.All;
    private CancellationTokenSource? _updateCancellation;
    private string? _lastBackupFolder;
    private UpdateInfo? _latestUpdateInfo;
    private FabricStatusInfo? _currentFabricStatus;

    public MainWindow()
    {
        InitializeComponent();

        _logger = new LoggingService();
        _logger.Info("ModSync GUI started.");

        _configService = new ConfigService(_logger);
        var loadedConfig = _configService.Load();
        if (!loadedConfig.Success && !loadedConfig.IsNewlyCreated)
            throw new IOException(loadedConfig.ErrorMessage);

        _gitService = new GitService(_logger);
        _authService = new AuthenticationService(_logger);
        _mcCheckService = new MinecraftCheckService(_logger);
        _ignoreService = new ModIgnoreService(_configService, _logger);
        _updateService = new UpdateService(_configService, _authService, _logger);
        _syncService = new ModSyncService(_configService, _gitService, _authService, _mcCheckService, _ignoreService, _logger);
        _fabricService = new FabricService(_configService, _logger, _authService);

        _feedbackTimer.Tick += (_, _) => { _feedbackTimer.Stop(); DismissFeedback(); };
        Closing += (_, e) => { if (_isBusy) { e.Cancel = true; ShowFeedback("Wait for the current operation to finish before closing.", false); } };
        Closed += (_, _) => { _feedbackTimer.Stop(); _choice?.TrySetResult(-1); _launchDecision?.TrySetResult(false); };
        Loaded += MainWindow_Loaded;
        Activated += async (_, _) =>
        {
            if (!_isBusy && _configService.Config.InstanceSelectionCompleted)
            {
                await RefreshLocalModCountAsync(fetchRemote: false);
                RefreshFabricStatusUI();
                RefreshLaunchSetup();
            }
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateStatusCard();
        RenderCategoryStates();
        if (!EnsureInstanceSelected()) return;

        await StartDashboardAsync();
    }

    private async Task StartDashboardAsync()
    {
        if (_dashboardStarted) return;
        _dashboardStarted = true;
        // Check for application updates when enabled
        if (_configService.Config.AutoCheckUpdates) _ = CheckForUpdatesOnStartupAsync();

        // Authentication is checked when opening account/settings actions; it must not delay Play.
        await RefreshLocalModCountAsync(fetchRemote: false);
        UpdateIgnoredModsUI();
        RefreshFabricStatusUI();
        ReadUpdateResult();
        _dashboardReady.TrySetResult();
        await ContinueStartupOrLaunchAsync();
    }

    #region Status & Information

    private void UpdateStatusCard()
    {
        var cfg = _configService.Config;
        ResourceRepoText.Text = string.IsNullOrWhiteSpace(cfg.ResourcePackRepository) ? GetShortRepoName(cfg.Repository) : GetShortRepoName(cfg.ResourcePackRepository);
        ShaderRepoText.Text = string.IsNullOrWhiteSpace(cfg.ShaderPackRepository) ? GetShortRepoName(cfg.Repository) : GetShortRepoName(cfg.ShaderPackRepository);
        ResourceRepoText.ToolTip = cfg.ResourcePackRepository;
        ShaderRepoText.ToolTip = cfg.ShaderPackRepository;
        string repoName = GetShortRepoName(cfg.Repository);
        StatusSubText.Text = $"{repoName} • {cfg.Branch}";
        ModsFolderPathText.Text = _configService.MinecraftFolder;
        ModsFolderPathText.ToolTip = _configService.MinecraftFolder;
        string folder = _configService.MinecraftFolder.TrimEnd(Path.DirectorySeparatorChar);
        string name = Path.GetFileName(folder);
        if (name is ".minecraft" or "minecraft") name = Path.GetFileName(Path.GetDirectoryName(folder)) ?? name;
        InstanceNameText.Text = cfg.InstanceSelectionCompleted ? name : "Choose an instance";
        InstanceNameText.ToolTip = folder;
        StatusSubText.ToolTip = cfg.Repository;
        RefreshPackVersion();
        RefreshLaunchSetup();
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

    private async Task RefreshLocalModCountAsync(bool fetchRemote = true)
    {
        string instance = _configService.MinecraftFolder;
        try
        {
            if (_manifestReview is { } review)
            {
                var manifest = review.Manifest;
                var counts = await Task.Run(async () =>
                {
                    var (local, _) = await _syncService.GetModCountsAsync(false);
                    int resources = PackSyncService.ScanPacks(_configService.ResolvedResourcePacksFolder, true, false).Keys.Select(p => p.Split('/')[0]).Distinct().Count();
                    int shaders = PackSyncService.ScanPacks(_configService.ResolvedShaderPacksFolder, false, false).Count;
                    return (local, resources, shaders);
                });
                if (instance != _configService.MinecraftFolder || !ReferenceEquals(review, _manifestReview)) return;
                UpdateModCountBadge(counts.local, manifest.Files.Count(f => f.Category == "mods"));
                ModCountBadgeText.Text = $"{counts.local} local · {manifest.Files.Count(f => f.Category == "mods")} published";
                PackStatusText.Text = $"{counts.resources} local · {manifest.Files.Where(f => f.Category == "resourcepacks").Select(f => f.Path.Split('/')[0]).Distinct().Count()} published";
                ShaderStatusText.Text = $"{counts.shaders} local · {manifest.Files.Count(f => f.Category == "shaderpacks")} published";
                if (manifest.ActiveShader != null) ShaderStatusText.Text += "\nPublished shader: " + manifest.ActiveShader;
                RenderCategoryStates(); return;
            }
            var (localCount, expectedCount) = await Task.Run(() => _syncService.GetModCountsAsync(fetchRemote));
            if (!instance.Equals(_configService.MinecraftFolder, StringComparison.OrdinalIgnoreCase)) return;
            UpdateModCountBadge(localCount, expectedCount);
            var (resources, shaders) = await Task.Run(() => new PackSyncService(_configService).SectionStatus());
            if (!instance.Equals(_configService.MinecraftFolder, StringComparison.OrdinalIgnoreCase)) return;
            PackStatusText.Text = resources;
            ShaderStatusText.Text = shaders;
            RenderCategoryStates();
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
            ModCountBadgeText.Text = expectedCount.HasValue
                ? $"{localCount} local · {expectedCount.Value} repository"
                : $"{localCount} local · repository not checked";
            ModCountBadgeText.ToolTip = "File counts are inventory only. Check to compare file contents.";
            ModCountStatusIcon.Visibility = Visibility.Collapsed;
            UpdateStatusCard();
            RenderCategoryStates();
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

    private async void SyncMods_Click(object sender, RoutedEventArgs e) => await BeginSyncAsync(SyncScope.All);
    private async void SyncOnlyMods_Click(object sender, RoutedEventArgs e) => await BeginSyncAsync(SyncScope.Mods);
    private async void SyncResources_Click(object sender, RoutedEventArgs e) => await BeginSyncAsync(SyncScope.ResourcePacks);
    private async void SyncOrder_Click(object sender, RoutedEventArgs e) => await BeginSyncAsync(SyncScope.ResourcePackOrder);
    private async void SyncShaders_Click(object sender, RoutedEventArgs e) => await BeginSyncAsync(SyncScope.Shaders);

    #endregion

    #region Push Action

    private async void PushMods_Click(object sender, RoutedEventArgs e) => await PreviewPushAsync(SyncScope.All);
    private async void PushOnlyMods_Click(object sender, RoutedEventArgs e) => await PreviewPushAsync(SyncScope.Mods);
    private async void PushResources_Click(object sender, RoutedEventArgs e) => await PreviewPushAsync(SyncScope.ResourcePacks);
    private async void PushOrder_Click(object sender, RoutedEventArgs e) => await PreviewPushAsync(SyncScope.ResourcePackOrder);
    private async void PushShaders_Click(object sender, RoutedEventArgs e) => await PreviewPushAsync(SyncScope.Shaders);

    private async Task PreviewPushAsync(SyncScope scope)
    {
        if (_isBusy || !EnsureInstanceSelected()) return;
        _activeScope = scope;
        _retryOperation = () => PreviewPushAsync(scope);

        if (((scope == SyncScope.ResourcePacks || scope == SyncScope.ResourcePackOrder) && !_configService.Config.SyncResourcePacks) ||
            (scope == SyncScope.Shaders && !_configService.Config.SyncShaderPacks))
        {
            ShowFeedback("Enable this sync option in Settings first.", true);
            return;
        }
        _pushScope = scope;
        PushConfirmTitleText.Text = scope == SyncScope.Mods ? "Push Mods?" : scope == SyncScope.ResourcePackOrder ? "Push Resource Pack Order?" : scope == SyncScope.ResourcePacks ? "Push Resource Packs?" : scope == SyncScope.Shaders ? "Push Shaders?" : "Push Modpack Changes?";

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

        if (scope.HasFlag(SyncScope.Mods)) await RefreshLocalModCountAsync();
        SetBusy(true, "Checking local modifications...");
        DismissFeedback();

        try
        {
            var (success, modChanges, error) = await Task.Run(() => _syncService.GetPushChangesAsync(UpdateProgress, _pushScope));
            if (!success || modChanges == null)
            {
                ShowFeedback(error ?? "Failed to inspect modifications.", true);
                SetBusy(false);
                return;
            }

            if (!modChanges.HasChanges)
            {
                ShowFeedback("No changes detected. Nothing to push.", false);
                SetBusy(false);
                return;
            }

            // Show confirmation sheet modal
            var pushParts = new List<string>();
            if (modChanges.AddedCount > 0) pushParts.Add($"+{modChanges.AddedCount} added");
            if (modChanges.UpdatedCount > 0) pushParts.Add($"~{modChanges.UpdatedCount} updated");
            if (modChanges.RemovedCount > 0) pushParts.Add($"-{modChanges.RemovedCount} removed");
            if (modChanges.IgnoredCount > 0) pushParts.Add($"{modChanges.IgnoredCount} excluded");
            if (modChanges.PendingRepositories.Count > 0) pushParts.Add("pending upload to retry");
            PushConfirmSummaryText.Text = $"Changes to upload: {string.Join(", ", pushParts)}";

            var sb = new System.Text.StringBuilder();
            foreach (var item in modChanges.Added) sb.AppendLine($"+ {item.RelativePath}");
            foreach (var item in modChanges.Updated) sb.AppendLine($"~ {item.RelativePath}");
            foreach (var item in modChanges.Removed) sb.AppendLine($"- {item.RelativePath}");
            foreach (var item in modChanges.Ignored) sb.AppendLine($"{item.RelativePath} (excluded / kept local)");
            foreach (var repo in modChanges.PendingRepositories) sb.AppendLine($"Retry pending upload: {repo}");
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
        if (_isBusy || !EnsureInstanceSelected()) return;
        CloseModal();
        SetBusy(true, "Pushing selected updates to GitHub...");

        try
        {
            var (success, summary, message) = await Task.Run(() =>
                _syncService.PushModsAsync(UpdateProgress, _pushScope)
            );

            if (success)
            {
                SetCategoryState(_pushScope, "Published · sync to verify");
                ShowFeedback("✓ Successfully pushed selected updates to GitHub!", false);
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
            await RefreshLocalModCountAsync(fetchRemote: _pushScope.HasFlag(SyncScope.Mods));
            SetBusy(false);
        }
    }

    #endregion

    #region Check Status Action

    #endregion

    #region GitHub Login Modal

    private void Auth_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
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
        if (_isBusy) return;
        RepoUrlInputBox.Text = _configService.Config.Repository;
        ModsBranchInput.Text = _configService.Config.Branch;
        ResourceRepoUrlInput.Text = _configService.Config.ResourcePackRepository;
        ResourceBranchInput.Text = _configService.Config.ResourcePackBranch;
        ShaderRepoUrlInput.Text = _configService.Config.ShaderPackRepository;
        ShaderBranchInput.Text = _configService.Config.ShaderPackBranch;
        ShowModal(SwitchRepoSheet);
    }

    private void ResetDefaultRepo_Click(object sender, RoutedEventArgs e)
    {
        RepoUrlInputBox.Text = AppConfig.DefaultRepositoryUrl;
        ResourceRepoUrlInput.Text = AppConfig.DefaultResourcePackRepositoryUrl;
        ShaderRepoUrlInput.Text = AppConfig.DefaultShaderPackRepositoryUrl;
        ModsBranchInput.Text = ResourceBranchInput.Text = ShaderBranchInput.Text = "main";
    }

    private void SubmitSwitchRepo_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        static bool ValidUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Trim('/').Split('/').Length == 2;
        var urls = new[] { RepoUrlInputBox.Text.Trim(), ResourceRepoUrlInput.Text.Trim(), ShaderRepoUrlInput.Text.Trim() };
        var branches = new[] { ModsBranchInput.Text.Trim(), ResourceBranchInput.Text.Trim(), ShaderBranchInput.Text.Trim() };
        if (!ValidUrl(urls[0]) || urls.Skip(1).Any(url => url.Length > 0 && !ValidUrl(url)) || branches.Any(branch => !RepositorySyncService.ValidBranch(branch)))
        {
            MessageBox.Show("Enter valid GitHub repository URLs and branch names.", "Repositories", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cfg = _configService.Config;
        var previous = (cfg.Repository, cfg.Branch, cfg.ResourcePackRepository, cfg.ResourcePackBranch, cfg.ShaderPackRepository, cfg.ShaderPackBranch);
        (cfg.Repository, cfg.Branch, cfg.ResourcePackRepository, cfg.ResourcePackBranch, cfg.ShaderPackRepository, cfg.ShaderPackBranch) = (urls[0], branches[0], urls[1], branches[1], urls[2], branches[2]);
        if (!_configService.Save())
        {
            (cfg.Repository, cfg.Branch, cfg.ResourcePackRepository, cfg.ResourcePackBranch, cfg.ShaderPackRepository, cfg.ShaderPackBranch) = previous;
            ShowFeedback("Could not save the repositories.", true);
            return;
        }
        CloseModal();
        UpdateStatusCard();
        _lastChecked = null;
        _currentFabricStatus = null;
        _manifestReview = null;
        _categoryStates.Clear(); RenderCategoryStates();
        RefreshFabricStatusUI();
        ShowFeedback("Repositories saved. Check your modpack to review changes.", false);
    }

    #endregion

    #region Settings & Clean Reinstall

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var cfg = _configService.Config;
        ConfirmPushToggle.IsChecked = cfg.RequireConfirmationBeforePush;
        AutoCheckUpdatesToggle.IsChecked = cfg.AutoCheckUpdates;
        SyncFabricLoaderToggle.IsChecked = cfg.SyncFabricLoader;
        SyncResourcePacksToggle.IsChecked = cfg.SyncResourcePacks;
        SyncShaderPacksToggle.IsChecked = cfg.SyncShaderPacks;
        EnforceActiveShaderToggle.IsChecked = cfg.EnforceActiveShader;
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
        _ = RefreshAuthStatusAsync();
    }

    private void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var cfg = _configService.Config;
        cfg.RequireConfirmationBeforePush = ConfirmPushToggle.IsChecked == true;
        cfg.AutoCheckUpdates = AutoCheckUpdatesToggle.IsChecked == true;
        cfg.SyncFabricLoader = SyncFabricLoaderToggle.IsChecked == true;
        cfg.SyncResourcePacks = SyncResourcePacksToggle.IsChecked == true;
        cfg.SyncShaderPacks = SyncShaderPacksToggle.IsChecked == true;
        cfg.EnforceActiveShader = EnforceActiveShaderToggle.IsChecked == true;
        _configService.Save();
        RenderCategoryStates();
        _logger.Info($"Preferences saved: ConfirmBeforeSync={cfg.RequireConfirmationBeforeSync}, ConfirmBeforePush={cfg.RequireConfirmationBeforePush}, AutoCheckUpdates={cfg.AutoCheckUpdates}, SyncFabricLoader={cfg.SyncFabricLoader}");
        RefreshFabricStatusUI();
    }

    private void ChangeModsFolder_Click(object sender, RoutedEventArgs e) => ChooseInstance_Click(sender, e);
    private void ResetModsFolder_Click(object sender, RoutedEventArgs e) => ChooseInstance_Click(sender, e);

    private bool _reinstallOpenedFromSettings = false;

    private void OpenCleanReinstallConfirm_Click(object sender, RoutedEventArgs e) => ShowReinstallConfirmation(SyncScope.Mods, fromSettings: true);
    private void OpenCleanReinstallFromMain_Click(object sender, RoutedEventArgs e) => ShowReinstallConfirmation(SyncScope.All);
    private void ReinstallMods_Click(object sender, RoutedEventArgs e) => ShowReinstallConfirmation(SyncScope.Mods);
    private void ReinstallResources_Click(object sender, RoutedEventArgs e) => ShowReinstallConfirmation(SyncScope.ResourcePacks);
    private void ReinstallShaders_Click(object sender, RoutedEventArgs e) => ShowReinstallConfirmation(SyncScope.Shaders);

    private void ShowReinstallConfirmation(SyncScope scope, bool fromSettings = false)
    {
        if (_isBusy) return;
        if ((scope == SyncScope.ResourcePacks && !_configService.Config.SyncResourcePacks) || (scope == SyncScope.Shaders && !_configService.Config.SyncShaderPacks))
        { ShowFeedback("Enable this category in Settings first.", true); return; }
        if (!EnsureInstanceSelected()) return;
        _activeScope = scope;
        _retryOperation = () => { ShowReinstallConfirmation(scope); return Task.CompletedTask; };
        _reinstallScope = scope;
        _reinstallOpenedFromSettings = fromSettings;
        ReinstallBackButton.Visibility = fromSettings ? Visibility.Visible : Visibility.Collapsed;
        string category = scope == SyncScope.ResourcePacks ? "Resource Packs" : scope == SyncScope.Shaders ? "Shaders" : scope == SyncScope.Mods ? "Mods" : "Modpack";
        bool packs = scope is SyncScope.ResourcePacks or SyncScope.Shaders;
        ReinstallTitleText.Text = $"Clean Reinstall {category}?";
        ReinstallDescriptionText.Text = packs ? "Save a safety backup, then reinstall every shared pack from this category’s repository." : scope == SyncScope.Mods ? "Back up and reinstall mods without changing resource packs or shaders." : "Back up and reinstall repository mods, then safely sync enabled packs.";
        ReinstallStepOneText.Text = packs ? "1. Back up this pack folder and its selection settings." : scope == SyncScope.Mods ? "1. Move non-excluded mods into a safety backup." : "1. Back up mods, enabled packs, and pack settings.";
        ReinstallStepTwoText.Text = packs ? "2. Replace all shared packs with fresh, verified copies." : "2. Install fresh repository mods; excluded mods stay.";
        ReinstallStepThreeText.Text = packs ? "3. Remove obsolete managed files; personal packs stay." : scope == SyncScope.Mods ? "3. Leave resource packs, shaders, and settings untouched." : "3. Sync shared packs; personal packs stay.";
        ReinstallPreservationText.Text = packs ? "Other categories and worlds stay untouched. Enabled selection rules are applied after the files succeed." : scope == SyncScope.Mods ? "Resource packs, shaders, and worlds stay untouched." : "Worlds stay untouched. Pack order and active shader follow enabled rules.";
        CleanReinstallBackupPreviewText.Text = _configService.BackupFolder;
        ShowModal(CleanReinstallConfirmSheet);
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        var folder = _configService.BackupFolder;
        if (!Directory.Exists(folder)) { ShowFeedback("No backups yet. Backups are created before pack changes or a clean reinstall.", false); return; }
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        catch (Exception ex) { ShowFeedback($"Could not open backups: {ex.Message}", true); }
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
        if (_isBusy || !EnsureInstanceSelected()) return;
        CloseModal();
        DismissFeedback();
        SetBusy(true, "Backing up and reinstalling the selected category…");

        try
        {
            var (success, backupDir, count, error) = await Task.Run(() =>
                _syncService.CleanReinstallAsync(
                    UpdateProgress,
                    forceIfMinecraftRunning: false, scope: _reinstallScope
                )
            );

            if (!success && error != null && error.Contains("Minecraft is currently running", StringComparison.OrdinalIgnoreCase))
            {
                var result = MessageBox.Show(
                    $"{error}\n\nChanging files while Minecraft is running may corrupt files or cause crashes.\n\nDo you want to continue anyway?",
                    "Minecraft Running Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    SetBusy(true, "Forcing clean reinstall...");
                    (success, backupDir, count, error) = await Task.Run(() =>
                        _syncService.CleanReinstallAsync(
                            UpdateProgress,
                            forceIfMinecraftRunning: true, scope: _reinstallScope
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
                SetCategoryState(_reinstallScope, "Up to date · last check");
                _lastBackupFolder = backupDir;

                if (!string.IsNullOrEmpty(backupDir))
                {
                    string kind = _reinstallScope is SyncScope.ResourcePacks or SyncScope.Shaders ? "packs" : "mods";
                    ShowFeedback($"✓ Clean reinstall complete. {count} {kind} restored; backup saved.", false, showBackupAction: true);
                }
                else
                {
                    ShowFeedback($"✓ Clean Reinstall Complete! {count} mods installed fresh.", false);
                }
            }
            else
            {
                SetStatusDot(false);
                _lastBackupFolder = backupDir;
                ShowFeedback(error ?? "Clean reinstall failed.", true, showBackupAction: backupDir != null);
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
            await RefreshLocalModCountAsync(fetchRemote: _reinstallScope.HasFlag(SyncScope.Mods));
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
            string logsDir = Path.Combine(PathUtils.GetDataDirectory(), "logs");
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
        PackCancelButton.Visibility = busy && _packCancellation != null && !_packApplying ? Visibility.Visible : Visibility.Collapsed;
        _isBusy = busy;
        SyncModsButton.IsEnabled = !busy;
        SyncOnlyModsButton.IsEnabled = !busy;
        PushOnlyModsButton.IsEnabled = !busy;
        ReinstallModsButton.IsEnabled = !busy;
        SyncResourcesButton.IsEnabled = !busy;
        SyncOrderButton.IsEnabled = !busy;
        PushOrderButton.IsEnabled = !busy;
        ReinstallResourcesButton.IsEnabled = !busy;
        ReinstallShadersButton.IsEnabled = !busy;
        SyncShadersButton.IsEnabled = !busy;
        PushModsButton.IsEnabled = !busy;
        PushResourcesButton.IsEnabled = !busy;
        PushShadersButton.IsEnabled = !busy;
        FabricUpdateButton.IsEnabled = !busy;
        ChooseInstanceButton.IsEnabled = !busy;
        FrontCheckUpdatesButton.IsEnabled = !busy && !_checkingUpdates;
        RecoveryButton.IsEnabled = !busy;
        RenderDashboardActions();

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

    private void SetStatusDot(bool ok) => RenderCategoryStates();

    private void ShowFeedback(string message, bool isError, bool showBackupAction = false)
    {
        _feedbackTimer.Stop();
        if (!isError && !showBackupAction) _feedbackTimer.Start();
        if (isError && _isBusy && _activeScope.HasValue) SetCategoryState(_activeScope.Value, "Failed");
        _recovery = isError ? ErrorRecoveryService.Suggest(message, _retryOperation != null) : null;
        RecoveryButton.Visibility = isError && !showBackupAction ? Visibility.Visible : Visibility.Collapsed;
        if (_recovery != null) { RecoveryButton.Content = _recovery.Label; message += "\n" + _recovery.Hint; }
        FeedbackMessageText.Text = message;
        FeedbackMessageText.Foreground = isError
            ? (Brush)FindResource("ErrorBrush")
            : (Brush)FindResource("PrimaryTextBrush");

        FeedbackActionButton.Visibility = showBackupAction ? Visibility.Visible : Visibility.Collapsed;
        FeedbackCard.Visibility = Visibility.Visible;
    }

    private void ShowSyncCompletion(SyncSummary summary, string? fabricVersion = null, bool fabricFailed = false)
    {
        ShowFeedback(SyncCompletionService.Describe(summary, _syncScope,
            _configService.Config.SyncResourcePacks, _configService.Config.SyncShaderPacks,
            File.Exists(Path.Combine(_configService.ResolvedResourcePackRepositoryFolder, "resourcepack-order.txt")), fabricVersion, fabricFailed), false);
        // Keep the result and restart guidance visible until dismissed or another action starts.
        _feedbackTimer.Stop();
    }
    private void DismissFeedback()
    {
        _feedbackTimer.Stop();
        FeedbackCard.Visibility = Visibility.Collapsed;
        RecoveryButton.Visibility = Visibility.Collapsed;
        FeedbackActionButton.Visibility = Visibility.Collapsed;
    }

    private void DismissFeedback_Click(object sender, RoutedEventArgs e)
    {
        DismissFeedback();
    }

    private void ShowModal(FrameworkElement sheet)
    {
        if (ModalBackdrop.Visibility != Visibility.Visible) _focusBeforeModal = Keyboard.FocusedElement;
        MainDashboard.IsEnabled = false;
        ChoiceSheet.Visibility = Visibility.Collapsed;
        InstanceSheet.Visibility = Visibility.Collapsed;
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
        // Reopening a sheet must remeasure its card and scroll presenter as well as its content.
        for (DependencyObject? parent = sheet; parent is FrameworkElement element; parent = VisualTreeHelper.GetParent(parent))
            element.InvalidateMeasure();
        Dispatcher.BeginInvoke(new Action(() => sheet.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
    }

    private void CloseModal()
    {
        _choice?.TrySetResult(-1);
        _choice = null;
        ChoiceSheet.Visibility = Visibility.Collapsed;
        if (InstanceSheet.Visibility == Visibility.Visible && (!_configService.Config.InstanceSelectionCompleted || !System.IO.Directory.Exists(_configService.MinecraftFolder))) return;
        InstanceSheet.Visibility = Visibility.Collapsed;
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
        MainDashboard.IsEnabled = true;
        if (_focusBeforeModal != null) Keyboard.Focus(_focusBeforeModal);
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
        RenderCategoryStates();
        ApplyFabricStatusToUI(_currentFabricStatus);
    }

    #endregion

    #region App Updates & Mod Exclusions

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            _logger.Info("Performing startup force check for updates...");
            var info = await _updateService.CheckForUpdatesAsync(force: false);
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
        if (_isBusy || _checkingUpdates) return;
        _activeScope = null;
        _retryOperation = () => { CheckForUpdates_Click(this, new RoutedEventArgs()); return Task.CompletedTask; };
        _checkingUpdates = true;
        FrontCheckUpdatesButton.IsEnabled = false;
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
            _checkingUpdates = false;
            FrontCheckUpdatesButton.IsEnabled = !_isBusy;
            CheckAppUpdatesButton.IsEnabled = true;
        }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _activeScope = null;
        _retryOperation = () => { if (_latestUpdateInfo != null) ShowUpdateSheet(_latestUpdateInfo); return Task.CompletedTask; };
        if (_isBusy) return;
        if (_latestUpdateInfo == null || string.IsNullOrWhiteSpace(_latestUpdateInfo.DownloadUrl))
        {
            ShowFeedback("No direct update download asset (.exe) available for this release.", true);
            return;
        }

        CloseModal();
        SetBusy(true, "Downloading ModSync update...");
        _updateCancellation = new CancellationTokenSource();
        PauseUpdateButton.Visibility = Visibility.Visible;

        try
        {
            var (success, error) = await _updateService.DownloadAndApplyUpdateAsync(_latestUpdateInfo, UpdateProgress, _updateCancellation.Token);
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
        finally
        {
            _updateCancellation.Dispose();
            _updateCancellation = null;
            PauseUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private void PauseUpdate_Click(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();

    private void ReadUpdateResult()
    {
        string path = (Environment.ProcessPath ?? Path.Combine(PathUtils.GetAppDirectory(), "ModSync.exe")) + ".update-result.json";
        if (!File.Exists(path)) return;
        try
        {
            using var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            ShowFeedback(result.RootElement.GetProperty("Message").GetString() ?? "Update finished.",
                !result.RootElement.GetProperty("Success").GetBoolean());
            File.Delete(path);
            if (result.RootElement.GetProperty("Success").GetBoolean()) _updateService.PruneDownloadCache();
        }
        catch (Exception ex) { _logger.Warning($"Could not read update result: {ex.Message}"); }
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
        _selectedExclusionMods.Clear();
        ExclusionSearchInput.Text = string.Empty;
        ResetExclusionDropZone();
        ExclusionDropFeedback.Visibility = Visibility.Collapsed;
        if (App.IsRunningAsAdministrator())
        {
            ExclusionDropFeedback.Text = "Running as administrator: Windows blocks drops from normal File Explorer windows. Use Choose Files to exclude several mods at once.";
            ExclusionDropFeedback.Visibility = Visibility.Visible;
        }
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

            var leftStack = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            leftStack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            leftStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            leftStack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconText = new TextBlock
            {
                Text = "—",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            leftStack.Children.Add(iconText);

            var nameText = new TextBlock
            {
                Text = pattern.StartsWith("fabric-id:", StringComparison.Ordinal) ? pattern[10..] + " · All versions (Fabric)" : pattern,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = pattern,
                FontFamily = (FontFamily)FindResource("SystemFont"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("PrimaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameText, 1);
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
                Grid.SetColumn(badge, 2);
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
                    if (!_ignoreService.RemovePattern(pat))
                    {
                        ExclusionDropFeedback.Text = "Couldn't remove this rule. Check folder access; rules from .modignore must be removed in that file.";
                        ExclusionDropFeedback.Visibility = Visibility.Visible;
                        return;
                    }
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

    private async void CloseIgnoredMods_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewingPersonalMods)
        {
            _reviewingPersonalMods = false;
            CloseModal();
            await ContinueStartupOrLaunchAsync();
            return;
        }
        ShowModal(SettingsSheet);
    }

    #endregion

    #region Fabric Loader Version Sync

    private void RefreshFabricStatusUI()
    {
        _ = RefreshFabricStatusUIAsync();
    }

    private Task RefreshFabricStatusUIAsync()
    {
        string instance = _configService.MinecraftFolder;
        try
        {
            // Initial fast local check
            _currentFabricStatus = _manifestReview != null ? ManifestFabric(_manifestReview.Manifest) : _fabricService.DetectFabricStatus();
            ApplyFabricStatusToUI(_currentFabricStatus);

            // Repository checking supplies the required version; focus/startup stays local.
        }
        catch (Exception ex)
        {
            _logger.Error("Error refreshing Fabric status in UI", ex);
        }
        return Task.CompletedTask;
    }

    private void ApplyFabricStatusToUI(FabricStatusInfo? status)
    {
        RenderDashboardActions();
        InstanceVersionText.Text = !string.IsNullOrWhiteSpace(status?.MinecraftVersion)
            ? $"Minecraft {status.MinecraftVersion}" + (status.InstalledLoaderVersion != null ? $" · Fabric {status.InstalledLoaderVersion}" : "")
            : "Minecraft instance";
        FabricVersionComparison.Visibility = status is { IsUpToDate: true, IsConfigured: true } ? Visibility.Collapsed : Visibility.Visible;
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
            FabricTargetVersionText.Text = "Not checked";
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

        FabricGameVersionText.Text = status.IsUpToDate ? $" · Fabric {installed}" : mcVer;
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

    private async void FabricUpdateButton_Click(object sender, RoutedEventArgs e) => await BeginSyncAsync(SyncScope.All);
    private async void ConfirmFabricUpdate_Click(object sender, RoutedEventArgs e) { CloseModal(); await BeginSyncAsync(SyncScope.All); }

    #endregion
}
