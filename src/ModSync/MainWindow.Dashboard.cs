using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ModSync.Models;

namespace ModSync;

public partial class MainWindow
{
    private DateTime? _lastChecked;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private IInputElement? _focusBeforeModal;

    private void OpenActions_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { ContextMenu: { } menu } button) return;
        PrepareActionsMenu(menu);
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void PrepareActionsMenu(ContextMenu menu)
    {
        // Popups have their own resource scope; refresh it when opening after a theme change.
        foreach (var key in new[] { "CardBackgroundBrush", "CardBorderBrush", "PrimaryTextBrush", "SecondaryButtonBrush" })
            menu.Resources[key] = FindResource(key);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ModalBackdrop.Visibility == Visibility.Visible && !_isBusy)
        {
            CloseModal();
            e.Handled = true;
        }
    }

    private void ReviewMods_Click(object sender, RoutedEventArgs e) => ReviewCategory(SyncScope.Mods);
    private void ReviewResources_Click(object sender, RoutedEventArgs e) => ReviewCategory(SyncScope.ResourcePacks);
    private void ReviewShaders_Click(object sender, RoutedEventArgs e) => ReviewCategory(SyncScope.Shaders);

    private async void ReviewCategory(SyncScope scope)
    {
        if (_isBusy) return;
        bool enabled = scope == SyncScope.Mods || (scope == SyncScope.ResourcePacks
            ? _configService.Config.SyncResourcePacks : _configService.Config.SyncShaderPacks);
        if (!enabled) { Settings_Click(this, new RoutedEventArgs()); return; }
        await BeginSyncAsync(scope);
    }

    private void RenderDashboardActions()
    {
        var enabled = new List<SyncScope> { SyncScope.Mods };
        if (_configService.Config.SyncResourcePacks) enabled.Add(SyncScope.ResourcePacks);
        if (_configService.Config.SyncShaderPacks) enabled.Add(SyncScope.Shaders);
        var states = enabled.Select(s => _categoryStates.GetValueOrDefault(s, "Not checked")).ToArray();
        bool changed = states.Any(s => s.Contains("change", StringComparison.OrdinalIgnoreCase) || s.Contains("review", StringComparison.OrdinalIgnoreCase));
        bool failed = states.Any(s => s == "Failed");
        bool current = states.All(s => s.StartsWith("Up to date"));
        bool fabricNeedsUpdate = _configService.Config.SyncFabricLoader && _currentFabricStatus is { IsConfigured: true, IsUpToDate: false };
        bool fabricUnknown = _configService.Config.SyncFabricLoader && _currentFabricStatus is not { IsConfigured: true };
        OverallStatusText.Text = _isBusy ? "Working on your modpack…" : failed ? "Some checks need attention" : changed ? "Changes need review" : fabricNeedsUpdate ? "Fabric Loader needs attention" : current ? (fabricUnknown ? "Content up to date · loader not checked" : "Everything is up to date") : "Check your modpack";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, failed ? "ErrorBrush" : changed || fabricNeedsUpdate ? "WarningBrush" : current && !fabricUnknown ? "SuccessBrush" : "SecondaryTextBrush");
        LastCheckedText.Text = _lastChecked.HasValue ? $"Last content check at {_lastChecked.Value:t} · check again after playing" : "Check for differences with your repositories";
        DashboardCheckButton.Content = _lastChecked.HasValue ? "Check Again" : "Check Now";
        DashboardCheckButton.IsEnabled = !_isBusy;
        SyncModsButton.Visibility = changed || fabricNeedsUpdate ? Visibility.Visible : Visibility.Collapsed;
        SyncModsButton.IsEnabled = !_isBusy;
        void Action(Button button, SyncScope scope, bool active)
        {
            string state = _categoryStates.GetValueOrDefault(scope, "Not checked");
            button.Content = !active ? "Enable in Settings" : state.Contains("change", StringComparison.OrdinalIgnoreCase) || state.Contains("review", StringComparison.OrdinalIgnoreCase) ? "Review changes" : state == "Failed" ? "Try Again" : "Check";
            button.IsEnabled = !_isBusy;
            button.Visibility = active && state.StartsWith("Up to date") ? Visibility.Collapsed : Visibility.Visible;
        }
        Action(SyncOnlyModsButton, SyncScope.Mods, true);
        Action(SyncResourcesButton, SyncScope.ResourcePacks, _configService.Config.SyncResourcePacks);
        Action(SyncShadersButton, SyncScope.Shaders, _configService.Config.SyncShaderPacks);
        PushResourcesButton.IsEnabled = SyncOrderButton.IsEnabled = PushOrderButton.IsEnabled = ReinstallResourcesButton.IsEnabled = !_isBusy && _configService.Config.SyncResourcePacks;
        PushShadersButton.IsEnabled = ReinstallShadersButton.IsEnabled = !_isBusy && _configService.Config.SyncShaderPacks;
    }
}
