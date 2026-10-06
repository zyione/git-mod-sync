using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModSync.Models;
using ModSync.Services;
using ModSync.Utils;

namespace ModSync;

public partial class MainWindow
{
    private readonly Dictionary<SyncScope, string> _categoryStates = new();
    private Func<Task>? _retryOperation;
    private RecoverySuggestion? _recovery;
    private bool _dashboardStarted;
    private bool _checkingUpdates;
    private SyncScope? _activeScope;

    private bool EnsureInstanceSelected()
    {
        if (_configService.Config.InstanceSelectionCompleted && Directory.Exists(_configService.MinecraftFolder)) return true;
        ShowInstancePicker();
        return false;
    }

    private void ChooseInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_launchDecision != null) { ShowFeedback("Cancel the pending Minecraft launch before changing instances.", false); return; }
        if (!_isBusy) ShowInstancePicker();
    }

    private void ShowInstancePicker()
    {
        if (_isBusy) return;
        var choices = InstanceDiscoveryService.Discover(PathUtils.GetAppDirectory(), _configService.MinecraftFolder).ToList();
        InstanceList.ItemsSource = choices;
        InstanceList.SelectedItem = choices.FirstOrDefault(c => c.Folder.Equals(_configService.MinecraftFolder, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(c => c.Name.Replace(" ", "").Contains("BigChadGuys", StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        InstanceSelectedPathText.Text = choices.Count == 0 ? $"Current folder: {PathUtils.GetAppDirectory()}" : (InstanceList.SelectedItem as MinecraftInstance)?.Folder ?? "Choose an instance";
        InstanceIntroText.Text = choices.Count == 0 ? "This folder does not look like a Minecraft instance. Browse to the game's folder containing options.txt or saves. Custom launcher locations can be selected manually." : "Confirm the suggested folder or choose another installed instance. This choice is saved across app updates.";
        InstanceErrorText.Text = "";
        ConfirmInstanceButton.IsEnabled = InstanceList.SelectedItem != null;
        InstanceCancelButton.Visibility = _configService.Config.InstanceSelectionCompleted && Directory.Exists(_configService.MinecraftFolder) ? Visibility.Visible : Visibility.Collapsed;
        ShowModal(InstanceSheet);
    }

    private void InstanceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmInstanceButton == null) return;
        ConfirmInstanceButton.IsEnabled = InstanceList.SelectedItem is MinecraftInstance;
        if (InstanceList.SelectedItem is MinecraftInstance selected)
        {
            InstanceSelectedPathText.Text = selected.Folder;
            InstanceIntroText.Text = "Use this instance for mods, resource packs, and shaders. Matching settings from an older installation are reused when found. Backups stay in ModSync; your choice is remembered.";
        }
    }

    private void BrowseInstance_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose Minecraft instance (the folder containing mods, options.txt, or saves)", InitialDirectory = Directory.Exists(_configService.MinecraftFolder) ? _configService.MinecraftFolder : PathUtils.GetAppDirectory() };
        if (dialog.ShowDialog() != true) return;
        string folder = dialog.FolderName;
        if (Path.GetFileName(folder).Equals("mods", StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder)!;
        if (!InstanceDiscoveryService.IsInstance(folder)) { InstanceErrorText.Text = "Choose the Minecraft game folder, not the launcher folder. Launch this instance once if it has not created its files yet."; return; }
        var choices = ((IEnumerable<MinecraftInstance>)InstanceList.ItemsSource).ToList();
        var selected = choices.FirstOrDefault(c => c.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) ?? new MinecraftInstance(Path.GetFileName(folder), folder);
        if (!choices.Contains(selected)) choices.Add(selected);
        InstanceList.ItemsSource = choices; InstanceList.SelectedItem = selected; InstanceErrorText.Text = "";
    }

    private async void ConfirmInstance_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceList.SelectedItem is not MinecraftInstance selected) return;
        if (!_configService.SelectInstance(selected.Folder)) { InstanceErrorText.Text = "Could not save this instance. Check that the folder still exists and config.json is writable."; return; }
        CloseModal(); ResetInstanceView();
        bool alreadyStarted = _dashboardStarted;
        if (!alreadyStarted) await StartDashboardAsync();
        await RefreshLocalModCountAsync(fetchRemote: false);
        RenderCategoryStates();
        if (alreadyStarted) await ContinueStartupOrLaunchAsync();
    }

    private void ResetInstanceView()
    {
        _lastChecked = null; _categoryStates.Clear(); _currentFabricStatus = null; _retryOperation = null; _activeScope = null;
        _reviewedSync = null; _reviewedFabric = null; _reviewedConfig = null;
        _manifestReview = null;
        SettingsModsFolderPathText.Text = _configService.ResolvedModsFolder;
        UpdateIgnoredModsUI();
        _lastBackupFolder = null; DismissFeedback(); UpdateStatusCard(); RefreshFabricStatusUI();
    }

    private void SetCategoryState(SyncScope scope, string state)
    {
        if (scope == SyncScope.ResourcePackOrder) scope = SyncScope.ResourcePacks;
        foreach (var category in new[] { SyncScope.Mods, SyncScope.ResourcePacks, SyncScope.Shaders })
            if (scope.HasFlag(category)) _categoryStates[category] = state;
        RenderCategoryStates();
    }

    private void MarkSyncComplete(SyncScope scope)
    {
        SetCategoryState(scope, scope == SyncScope.ResourcePackOrder ? "Order current · files not checked" : "Up to date · last check");
    }

    private void SetCheckedStates(SyncScope scope, SyncSummary summary)
    {
        _lastChecked = DateTime.Now;
        if (scope == SyncScope.ResourcePackOrder)
        {
            _categoryStates[SyncScope.ResourcePacks] = summary.HasChanges ? "Order changes available" : "Order current · files not checked";
            RenderCategoryStates(); return;
        }
        foreach (var category in new[] { SyncScope.Mods, SyncScope.ResourcePacks, SyncScope.Shaders })
        {
            if (!scope.HasFlag(category)) continue;
            int changes = summary.Changes.Count(c => !c.IsInternal && (c.Type is ChangeType.Added or ChangeType.Updated or ChangeType.Removed) &&
                (category == SyncScope.ResourcePacks ? c.RelativePath.StartsWith("resourcepacks/") || c.RelativePath.Contains("resource pack order") :
                 category == SyncScope.Shaders ? c.RelativePath.StartsWith("shaderpacks/") || c.RelativePath.Contains("active shader") :
                 !c.RelativePath.StartsWith("resourcepacks/") && !c.RelativePath.StartsWith("shaderpacks/") && c.NewContent == null));
            _categoryStates[category] = changes > 0 ? $"{changes} change{(changes == 1 ? "" : "s")} to review"
                : summary.PendingRepositories.Count > 0 ? "Review repository updates"
                : summary.Changes.Any(c => c.IsInternal && c.Type != ChangeType.Unchanged) ? "Review metadata updates"
                : "Up to date · last check";
        }
        RenderCategoryStates();
    }

    private void RenderCategoryStates()
    {
        void Apply(TextBlock text, SyncScope scope, bool enabled, string prefix = "")
        {
            string state = enabled ? _categoryStates.GetValueOrDefault(scope, "Not checked") : "Disabled";
            text.Text = prefix + state;
            text.SetResourceReference(TextBlock.ForegroundProperty, state == "Failed" ? "ErrorBrush" : state.StartsWith("Up to date") ? "SuccessBrush" : state.Contains("review", StringComparison.OrdinalIgnoreCase) || state.Contains("changes", StringComparison.OrdinalIgnoreCase) ? "WarningBrush" : "SecondaryTextBrush");
        }
        Apply(StatusHeadingText, SyncScope.Mods, true);
        Apply(ResourceStateText, SyncScope.ResourcePacks, _configService.Config.SyncResourcePacks);
        Apply(ShaderStateText, SyncScope.Shaders, _configService.Config.SyncShaderPacks);
        RenderDashboardActions();
        ModCountStatusIcon.Visibility = Visibility.Collapsed; // File counts do not prove matching contents.
    }

    private async void Recovery_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _recovery == null) return;
        switch (_recovery.Action)
        {
            case RecoveryAction.SignIn: ShowLoginModal(); break;
            case RecoveryAction.ChooseInstance: ShowInstancePicker(); break;
            case RecoveryAction.OpenBackups: OpenBackups_Click(sender, e); break;
            case RecoveryAction.ViewLogs: OpenLogs_Click(sender, e); break;
            case RecoveryAction.SyncSection: await BeginSyncAsync(_pushScope); break;
            case RecoveryAction.Retry: if (_retryOperation != null) await _retryOperation(); else OpenLogs_Click(sender, e); break;
        }
    }
}
