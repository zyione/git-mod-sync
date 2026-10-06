using System.Windows;
using ModSync.Models;
using ModSync.Services;
using ModSync.Utils;

namespace ModSync;

public partial class MainWindow
{
    private TaskCompletionSource<bool>? _launchDecision;
    private TaskCompletionSource<int>? _choice;
    private bool _reviewingPersonalMods;
    private bool _updateFailed;
    private SyncSummary? _reviewedSync;
    private string? _reviewedConfig;
    private FabricStatusInfo? _reviewedFabric;
    private readonly TaskCompletionSource _dashboardReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closeAfterLaunch;
    private string PendingUpdatePath => Path.Combine(Path.GetDirectoryName(_configService.ConfigFilePath)!,
        "pending-updates", PathUtils.InstanceKey(_configService.MinecraftFolder) + ".txt");

    public void LaunchReplySent(bool play) { if (play && _closeAfterLaunch) { _closeAfterLaunch = false; Close(); } }

    private bool EnsureLoaderCanUpdate()
    {
        var launchers = System.Diagnostics.Process.GetProcessesByName("UltimMC");
        bool launcherOpen = launchers.Length > 0;
        foreach (var process in launchers) process.Dispose();
        if (_launchDecision != null || launcherOpen)
        {
            CompleteLaunch(false);
            ShowFeedback("Fabric Loader needs an update. Close UltimMC, review and apply the update in ModSync, then reopen UltimMC. No game files were changed.", true);
            return false;
        }
        var (running, details) = _mcCheckService.CheckIfMinecraftRunning();
        if (!running) return true;
        ShowFeedback("Close Minecraft before updating Fabric Loader. " + details, true);
        return false;
    }

    private string ConfigFingerprint() => System.Text.Json.JsonSerializer.Serialize(_configService.Config);
    private List<string> LocalModNames() => Directory.Exists(_configService.ResolvedModsFolder)
        ? Directory.EnumerateFiles(_configService.ResolvedModsFolder, "*.jar",
            _configService.Config.SyncSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(p => !_ignoreService.IsIgnored(Path.GetRelativePath(_configService.ResolvedModsFolder, p), p))
            .Select(p => Path.GetRelativePath(_configService.ResolvedModsFolder, p)).ToList() : new();

    public async Task<bool> RequestLaunchAsync(string instance, CancellationToken cancellation = default)
    {
        // Never silently switch an existing dashboard or apply one instance's settings to another.
        if (_launchDecision != null || _isBusy) return false;
        if (!_configService.Config.InstanceSelectionCompleted ||
            PathUtils.InstanceKey(instance) != PathUtils.InstanceKey(_configService.MinecraftFolder))
        {
            ShowFeedback("Launch cancelled: choose this UltimMC game folder in ModSync, then press Play in UltimMC again.", true);
            Activate();
            return false;
        }
        await _dashboardReady.Task.WaitAsync(cancellation);
        if (_launchDecision != null || _isBusy) return false;
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _launchDecision = decision;
        using var registration = cancellation.Register(() =>
        {
            decision.TrySetResult(false);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_launchDecision, decision)) return;
                _choice?.TrySetResult(-1);
                if (!_isBusy) CloseModal();
            }));
        });
        _updateFailed = File.Exists(PendingUpdatePath);
        LaunchBanner.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        Activate();
        try
        {
            CloseModal();
            await ContinueStartupOrLaunchAsync();
            return await decision.Task;
        }
        catch (Exception ex) { ShowFeedback("Launch check failed: " + ex.Message, true); return false; }
        finally { _launchDecision = null; LaunchBanner.Visibility = Visibility.Collapsed; }
    }

    private async Task ContinueStartupOrLaunchAsync()
    {
        string key = PathUtils.InstanceKey(_configService.MinecraftFolder);
        var mods = LocalModNames();
        if (LaunchReadiness.NeedsPersonalReminder(_configService.Config, key, mods))
        {
            int choice = await ChooseAsync("Have you excluded your personal mods?",
                "Excluded mods stay on your computer and won’t be uploaded. If you haven’t added personal mods, choose Skip. We’ll ask again when new local mods appear.",
                "Review exclusions", "Already excluded", "Skip — no personal mods");
            if (choice < 0) return;
            if (choice == 0)
            {
                _reviewingPersonalMods = true;
                OpenIgnoredModsModal_Click(this, new RoutedEventArgs());
                return;
            }
            _configService.Config.PersonalModsAcknowledged[key] = mods;
            if (!_configService.Save()) { ShowFeedback("Could not remember your choice. Check folder access and try again.", true); return; }
        }
        if (_launchDecision != null) await BeginSyncAsync(SyncScope.All);
    }

    private Task<int> ChooseAsync(string title, string body, string first, string second, string? third = null)
    {
        _choice?.TrySetResult(-1);
        _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ChoiceTitle.Text = title;
        ChoiceBody.Text = body;
        ChoiceFirst.Content = first;
        ChoiceSecond.Content = second;
        ChoiceThird.Content = third;
        ChoiceThird.Visibility = third == null ? Visibility.Collapsed : Visibility.Visible;
        ShowModal(ChoiceSheet);
        return _choice.Task;
    }

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        int selected = ReferenceEquals(sender, ChoiceFirst) ? 0 : ReferenceEquals(sender, ChoiceSecond) ? 1 : 2;
        var pending = _choice;
        _choice = null;
        CloseModal();
        pending?.TrySetResult(selected);
    }

    private async Task HandleLaunchCheckAsync(SyncSummary summary)
    {
        if (LaunchReadiness.IsReady(summary, _configService.Config.SyncFabricLoader, _currentFabricStatus))
        {
            _updateFailed = false;
            if (File.Exists(PendingUpdatePath)) File.Delete(PendingUpdatePath);
            CompleteLaunch(true); // Healthy launches need no extra confirmation.
        }
        else await LaunchIssueAsync("Content checked; Fabric Loader needs attention. Review the loader settings, or explicitly choose Play anyway.");
    }

    private async Task LaunchIssueAsync(string message)
    {
        int result = await ChooseAsync("Minecraft is waiting", message,
            "Check again", _updateFailed ? "Stay in ModSync" : "Play anyway", "Cancel launch");
        if (result == 0) await BeginSyncAsync(SyncScope.All);
        else if (result == 1 && !_updateFailed) CompleteLaunch(true);
        else if (result == 2 || result < 0) CompleteLaunch(false);
    }

    private void CompleteLaunch(bool play)
    {
        if (_launchDecision == null || !_launchDecision.TrySetResult(play)) return;
        LaunchBanner.Visibility = Visibility.Collapsed;
        if (play) ShowFeedback("Continuing to Minecraft. ModSync can stay open.", false);
    }

    private async void LaunchCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) await ContinueStartupOrLaunchAsync();
    }

    private async void PlayAnyway_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _launchDecision == null) return;
        CloseModal();
        if (_updateFailed)
        {
            ShowFeedback("An update did not finish. Check and repair the files before continuing to Minecraft.", true);
            return;
        }
        int choice = await ChooseAsync("Play without updating?", "Your installation may differ from the shared modpack. You may be unable to join the server.", "Play anyway", "Go back");
        if (choice == 0) CompleteLaunch(true);
    }

    private void CancelLaunch_Click(object sender, RoutedEventArgs e) { if (!_isBusy) { CloseModal(); CompleteLaunch(false); } }

    private async Task VerifyAndOfferCloseAsync()
    {
        var (status, remaining, _, _) = await Task.Run(() => _syncService.CheckStatusAsync(UpdateProgress, SyncScope.All, refreshRepository: false));
        if (!status.IsConnected || status.ErrorMessage != null)
            throw new IOException("Updated files could not be fully verified. Check again when the repositories are reachable.");
        if (status.BehindCount > 0 || status.AheadCount > 0)
        {
            ShowFeedback("The reviewed update finished, but repository versions have changed. Check again to review the remaining changes.", true);
            return;
        }
        _currentFabricStatus = _fabricService.DetectFabricStatus();
        SetCheckedStates(SyncScope.All, remaining);
        if (!LaunchReadiness.IsReady(remaining, _configService.Config.SyncFabricLoader, _currentFabricStatus))
        {
            ShowFeedback("The selected update finished, but the full modpack is not verified as synchronized. Check the remaining categories and Fabric Loader.", true);
            return;
        }
        _updateFailed = false;
        if (File.Exists(PendingUpdatePath)) File.Delete(PendingUpdatePath);
        // Files just installed from the reviewed repository are not new personal additions.
        _configService.Config.PersonalModsAcknowledged[PathUtils.InstanceKey(_configService.MinecraftFolder)] = LocalModNames();
        _configService.Save();
        SetBusy(false);
        bool launching = _launchDecision != null;
        int choice = await ChooseAsync("Everything is synchronized",
            "Enabled content matches the checked repository versions. Excluded mods were preserved. " +
            (launching ? "Continue to Minecraft and close ModSync?" : "Would you like to close ModSync?"),
            launching ? "Play & close ModSync" : "Close ModSync",
            launching ? "Play & keep ModSync open" : "Keep ModSync open");
        if (choice < 0) return;
        if (launching)
        {
            _closeAfterLaunch = choice == 0;
            CompleteLaunch(true);
        }
        else if (choice == 0) Close();
    }
}
