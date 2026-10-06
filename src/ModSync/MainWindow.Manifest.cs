using System.Net.Http;
using System.Text.Json;
using System.Windows;
using ModSync.Models;
using ModSync.Services;
using ModSync.Utils;

namespace ModSync;

public partial class MainWindow
{
    private static readonly HttpClient PackHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private PackManifestService ManifestService => new(_configService, _ignoreService, PackHttp, _authService.GetStoredToken);
    private ManifestReview? _manifestReview;
    private CancellationTokenSource? _packCancellation;
    private bool _packApplying;
    private void CancelPackOperation_Click(object sender, RoutedEventArgs e)
    {
        if (!_packApplying) { _packCancellation?.Cancel(); PackCancelButton.IsEnabled = false; }
    }

    private void RefreshPackVersion(string? available = null)
    {
        available ??= _manifestReview?.Manifest.Version;
        try
        {
            string? installed = ManifestService.State.Version;
            PackVersionText.Text = installed == null ? "Pack version not verified" : "Pack " + installed + " · last verified";
            if (available != null && available != installed) PackVersionText.Text += " → " + available + " available";
            PackVersionText.ToolTip = "Version of enabled shared content. Personal exclusions are preserved. Use Full verification to check every file.";
        }
        catch (Exception ex) { PackVersionText.Text = "Pack version needs attention"; PackVersionText.ToolTip = ex.Message; }
    }
    private FabricStatusInfo ManifestFabric(PackManifest manifest)
    {
        var status = _fabricService.DetectFabricStatus();
        status.TargetLoaderVersion = manifest.Fabric;
        status.IsConfigured = manifest.Fabric != null;
        status.IsUpToDate = manifest.Fabric == null || status.InstalledLoaderVersion == manifest.Fabric;
        if (status.MinecraftVersion != null && status.MinecraftVersion != manifest.Minecraft)
            throw new IOException($"Pack {manifest.Version} requires Minecraft {manifest.Minecraft}. Choose a matching instance before updating.");
        status.MinecraftVersion = manifest.Minecraft;
        status.VersionSource = "Published pack " + manifest.Version;
        return status;
    }
    private async void CheckStatus_Click(object sender, RoutedEventArgs e) => await BeginSyncAsync(SyncScope.All);
    private async void FullVerification_Click(object sender, RoutedEventArgs e) => await BeginSyncAsync(SyncScope.All, full: true);

    private async Task BeginSyncAsync(SyncScope scope, bool reviewChanges = true, bool full = false)
    {
        if (_isBusy || !EnsureInstanceSelected()) return;
        _activeScope = _syncScope = scope; _manifestReview = null;
        _retryOperation = () => BeginSyncAsync(scope, full: full);
        var operation = new CancellationTokenSource(); _packCancellation = operation; var cancellation = operation.Token; PackCancelButton.IsEnabled = true;
        SetCategoryState(scope, "Checking…"); SetBusy(true, "Checking published modpack…"); DismissFeedback();
        try
        {
            var service = ManifestService;
            var manifest = await service.FetchAsync(cancellation);
            _currentFabricStatus = ManifestFabric(manifest); ApplyFabricStatusToUI(_currentFabricStatus);
            var review = await Task.Run(() => service.Plan(manifest, scope, full || service.HasPendingUpdate || File.Exists(PendingUpdatePath), cancellation));
            cancellation.ThrowIfCancellationRequested();
            if (_launchDecision?.Task.IsCompleted == true) return;
            _manifestReview = review; _reviewedSync = review.Summary; _reviewedConfig = ConfigFingerprint(); _reviewedFabric = _currentFabricStatus;
            await RefreshLocalModCountAsync(false);
            SetCheckedStates(scope, review.Summary); RefreshPackVersion(manifest.Version);
            bool loader = scope.HasFlag(SyncScope.Mods) && _configService.Config.SyncFabricLoader && !_currentFabricStatus.IsUpToDate;
            if (!review.Summary.HasChanges && !loader)
            {
                if (scope == SyncScope.All && (!_configService.Config.SyncFabricLoader || _currentFabricStatus.IsUpToDate))
                {
                    // The initial baseline/full verification is required once; ordinary launches reuse valid metadata.
                    if (service.State.ManifestHash != PackManifestService.Digest(manifest) || service.HasPendingUpdate || full)
                        await Task.Run(() => service.MarkVerified(manifest));
                    RefreshPackVersion();
                }
                ShowSyncCompletion(review.Summary); SetBusy(false);
                if (_launchDecision != null) await HandleLaunchCheckAsync(review.Summary);
                return;
            }
            string previous = service.State.Version ?? "unverified";
            SyncConfirmTitleText.Text = $"Review pack {previous} → {manifest.Version}";
            SyncConfirmSummaryText.Text = $"{review.Summary.AddedCount} added · {review.Summary.UpdatedCount} updated · {review.Summary.RemovedCount} removed\n" +
                $"Download: {PathUtils.FormatFileSize(service.DownloadBytes(review))}. Files download only after approval.";
            SyncDetailsText.Text = string.Join("\n", review.Summary.Changes.Where(c => c.Type != ChangeType.Unchanged).Select(c => c.ToString())) +
                (loader ? $"\nFabric Loader: {_currentFabricStatus.InstalledLoaderVersion ?? "not installed"} → {manifest.Fabric}" : "");
            SetBusy(false); ShowModal(SyncConfirmSheet);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { ShowFeedback("Check cancelled. No game files changed.", false); CompleteLaunch(false); }
        catch (Exception ex)
        {
            SetCategoryState(scope, "Failed"); ShowFeedback("Could not check the published pack: " + ex.Message, true); SetBusy(false);
            if (_launchDecision != null) await LaunchIssueAsync(ex.Message);
        }
        finally { if (ReferenceEquals(_packCancellation, operation)) _packCancellation = null; operation.Dispose(); SetBusy(false); }
    }
    private async void ConfirmSync_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _manifestReview == null) return;
        CloseModal(); await PerformSyncAsync();
    }
    private async Task PerformSyncAsync(bool skipConfirmation = false)
    {
        if (_isBusy || _manifestReview == null) return;
        var review = _manifestReview; var service = ManifestService;
        if (review.Configuration != JsonSerializer.Serialize(_configService.Config)) { ShowFeedback("Settings changed. Check again before updating.", true); return; }
        bool loader = review.Scope.HasFlag(SyncScope.Mods) && _configService.Config.SyncFabricLoader && _reviewedFabric is { IsConfigured: true, IsUpToDate: false };
        if (loader && !EnsureLoaderCanUpdate()) return;
        var operation = new CancellationTokenSource(); _packCancellation = operation; PackCancelButton.IsEnabled = true;
        SetBusy(true, "Downloading approved files…"); SetCategoryState(review.Scope, "Syncing…");
        try
        {
            await service.DownloadAsync(review, UpdateProgress, _packCancellation.Token);
            _packCancellation.Token.ThrowIfCancellationRequested();
            var (running, details) = _mcCheckService.CheckIfMinecraftRunning();
            if (running) throw new IOException("Close Minecraft before applying updates. " + details);
            _packApplying = true;
            PackCancelButton.Visibility = Visibility.Collapsed;
            SetBusy(true, "Verifying and applying approved files…");
            await Task.Run(() => service.BeginApply(review));
            Directory.CreateDirectory(Path.GetDirectoryName(PendingUpdatePath)!); File.WriteAllText(PendingUpdatePath, "Pack update needs verification."); _updateFailed = true;
            var result = await Task.Run(() => _syncService.ApplyManifestFilesAsync(review.Summary, UpdateProgress));
            if (!result.Success) throw new IOException(result.Error);
            if (loader)
            {
                await Task.Run(() => new PackBackupService(_configService).SnapshotLoader(_fabricService.FindMmcPackPath()));
                var installed = await _fabricService.InstallFabricLoaderAsync(review.Manifest.Minecraft, review.Manifest.Fabric!, UpdateProgress);
                if (!installed.Success) throw new IOException(installed.Error);
            }
            ShowSyncCompletion(review.Summary);
            await VerifyManifestAndOfferCloseAsync(review.Manifest);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !_packApplying) { ShowFeedback("Download cancelled. Installed files were not changed.", false); CompleteLaunch(false); }
        catch (Exception ex) { SetCategoryState(review.Scope, "Failed"); ShowFeedback("Update stopped: " + ex.Message + "\nRetry the check. Completed downloads and backups are retained.", true); }
        finally { _packApplying = false; if (ReferenceEquals(_packCancellation, operation)) _packCancellation = null; operation.Dispose(); SetBusy(false); RefreshPackVersion(); await RefreshLocalModCountAsync(false); }
    }
    private async Task VerifyManifestAndOfferCloseAsync(PackManifest manifest)
    {
        _currentFabricStatus = ManifestFabric(manifest); ApplyFabricStatusToUI(_currentFabricStatus);
        if (_configService.Config.SyncFabricLoader && !_currentFabricStatus.IsUpToDate) throw new IOException("Fabric Loader still needs attention.");
        await Task.Run(() => ManifestService.MarkVerified(manifest));
        _updateFailed = false; if (File.Exists(PendingUpdatePath)) File.Delete(PendingUpdatePath);
        _configService.Config.PersonalModsAcknowledged[_configService.InstanceStorageKey] = LocalModNames();
        if (!_configService.Save()) throw new IOException("Could not save the exclusion reminder state.");
        RefreshPackVersion(); SetBusy(false);
        bool launching = _launchDecision != null;
        int choice = await ChooseAsync("Pack " + manifest.Version + " is synchronized", "Enabled content has been verified. Excluded personal mods were preserved. " +
            (launching ? "Continue to Minecraft and close ModSync?" : "Would you like to close ModSync?"),
            launching ? "Play & close ModSync" : "Close ModSync", launching ? "Play & keep ModSync open" : "Keep ModSync open");
        if (choice < 0) return;
        if (launching) { _closeAfterLaunch = choice == 0; CompleteLaunch(true); } else if (choice == 0) Close();
    }
    private async void PublishPack_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !EnsureInstanceSelected()) return;
        if (_launchDecision != null) { ShowFeedback("Cancel the pending launch before publishing a pack version.", false); return; }
        if (string.IsNullOrWhiteSpace(_authService.GetStoredToken())) { ShowLoginModal(); return; }
        int start = await ChooseAsync("Prepare a modpack version?", "This downloads authoring repositories to build a file list and checksums. Push your intended content changes first. Nothing is published until you review the version.", "Prepare version", "Cancel");
        if (start != 0) return;
        try
        {
            SetBusy(true, "Preparing modpack version…");
            var publisher = new PackPublisher(_configService, _gitService, ManifestService, _authService.GetStoredToken);
            var publication = await Task.Run(() => publisher.PrepareAsync(UpdateProgress));
            SetBusy(false);
            int choice = await ChooseAsync("Publish pack " + publication.Manifest.Version + "?",
                $"{publication.Manifest.Files.Count} files across {publication.Manifest.Repositories.Count} repository snapshots. This publishes the shared manifest so players can review this exact version. Versions stay in their current major series until you explicitly change it.", "Publish " + publication.Manifest.Version, "Cancel");
            if (choice != 0) return;
            SetBusy(true, "Publishing modpack version…");
            await Task.Run(() => publisher.PublishAsync(publication, UpdateProgress));
            ShowFeedback("Published pack " + publication.Manifest.Version + ". Players can now check for this version.", false); RefreshPackVersion(publication.Manifest.Version);
        }
        catch (Exception ex) { ShowFeedback("Could not publish pack: " + ex.Message, true); }
        finally { SetBusy(false); }
    }
}
