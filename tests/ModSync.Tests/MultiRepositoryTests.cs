using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class MultiRepositoryTests
{
    private string _root = null!;
    private ConfigService _config = null!;
    private LoggingService _logger = null!;
    private FakeGit _git = null!;
    private string ModsRepo => _config.ResolvedRepositoryFolder;
    private string PacksRepo => _config.ResolvedResourcePackRepositoryFolder;
    private string ShadersRepo => _config.ResolvedShaderPackRepositoryFolder;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ModSync_MultiRepo_" + Guid.NewGuid().ToString("N"));
        _logger = new LoggingService(); _config = new ConfigService(_logger); _git = new FakeGit();
        _config.Config.RepositoryFolder = Path.Combine(_root, "cache", "mods");
        _config.Config.ModsFolder = Path.Combine(_root, "instance", "mods");
        _config.Config.RequireConfirmationBeforePush = false;
        _config.Config.RequireConfirmationBeforeSync = false;
        foreach (var folder in new[] { ModsRepo, PacksRepo, ShadersRepo }) Directory.CreateDirectory(Path.Combine(folder, ".git"));
        Directory.CreateDirectory(_config.MinecraftFolder);
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static void Write(string file, string value) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value); }
    private ModSyncService Engine() => new(_config, _git, new AuthenticationService(_logger), new MinecraftCheckService(_logger), new ModIgnoreService(_config, _logger), _logger, () => "test-token");

    [TestMethod]
    public async Task SyncAllRefreshesThreeRepositoriesConcurrentlyThenAppliesOnePlan()
    {
        Write(Path.Combine(ModsRepo, "mods", "Mod.jar"), "mod");
        Write(Path.Combine(PacksRepo, "Pack.zip"), "pack");
        Write(Path.Combine(PacksRepo, "resourcepack-order.txt"), "Pack.zip");
        Write(Path.Combine(ShadersRepo, "Shader.zip"), "shader");
        Write(Path.Combine(ShadersRepo, "active-shader.txt"), "Shader.zip");
        _git.ConcurrentDownloads = 3;
        var engine = Engine();
        var result = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(3, _git.MaxDownloads);
        Assert.AreEqual("mod", File.ReadAllText(Path.Combine(_config.ResolvedModsFolder, "Mod.jar")));
        Assert.AreEqual("pack", File.ReadAllText(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip")));
        Assert.AreEqual("shader", File.ReadAllText(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip")));
        Assert.IsTrue(File.ReadAllText(Path.Combine(_config.MinecraftFolder, "options.txt")).Contains("file/Pack.zip"));
        Assert.IsTrue(File.ReadAllText(Path.Combine(_config.MinecraftFolder, "config", "iris.properties")).Contains("Shader.zip"));
        var second = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true);
        Assert.IsTrue(second.Success, second.Message);
        Assert.IsFalse(second.Summary!.HasChanges);
    }

    [TestMethod]
    public async Task RefreshFailurePreventsAllMinecraftWritesAndDeletionTrackingChanges()
    {
        Write(Path.Combine(ModsRepo, "mods", "New.jar"), "new");
        Write(Path.Combine(_config.ResolvedModsFolder, "Old.jar"), "old");
        Write(Path.Combine(PacksRepo, "New.zip"), "new");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Old.zip"), "old");
        var manifest = Path.Combine(_config.MinecraftFolder, ".modsync-managed-packs.json");
        Write(manifest, "{\"resourcepacks\":[\"Old.zip\"]}");
        _git.FailedRefresh = ShadersRepo;
        var result = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message!, "Shaders");
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(_config.ResolvedModsFolder, "Old.jar")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(_config.ResolvedResourcePacksFolder, "Old.zip")));
        Assert.IsFalse(File.Exists(Path.Combine(_config.ResolvedModsFolder, "New.jar")));
        Assert.AreEqual("{\"resourcepacks\":[\"Old.zip\"]}", File.ReadAllText(manifest));
    }

    [TestMethod]
    public async Task PackPushAndOrderIgnoreNewModsAndShaderRepositoryChanges()
    {
        _git.Behind[ModsRepo] = 10; _git.Behind[ShadersRepo] = 5;
        Write(Path.Combine(ModsRepo, "mods", "Mod.jar"), "remote");
        Write(Path.Combine(_config.ResolvedModsFolder, "Mod.jar"), "local");
        Write(Path.Combine(PacksRepo, "Pack.zip"), "old");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "new");
        Write(Path.Combine(_config.MinecraftFolder, "options.txt"), "resourcePacks:[\"vanilla\",\"file/Pack.zip\"]\n");
        _config.Config.PublishResourcePackOrder = true;
        var preview = await Engine().GetPushChangesAsync(scope: SyncScope.ResourcePacks);
        Assert.IsTrue(preview.Success, preview.Message);
        Assert.AreEqual(2, preview.Summary!.TotalActionableChanges);
        var result = await Engine().PushModsAsync(scope: SyncScope.ResourcePacks);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(_git.Touched.All(folder => folder == PacksRepo));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(PacksRepo, "Pack.zip")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(PacksRepo, "resourcepack-order.txt")), "Pack.zip");
        CollectionAssert.AreEquivalent(new[] { "Pack.zip", "resourcepack-order.txt" }, _git.CommitPaths[PacksRepo]);
        Assert.AreEqual("remote", File.ReadAllText(Path.Combine(ModsRepo, "mods", "Mod.jar")));
        Assert.AreEqual("local", File.ReadAllText(Path.Combine(_config.ResolvedModsFolder, "Mod.jar")));
    }

    [TestMethod]
    public async Task ShaderPushAndSyncNeverContactModsOrResourcePacks()
    {
        _git.Behind[ModsRepo] = 10; _git.Behind[PacksRepo] = 3;
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip"), "shader");
        Assert.IsTrue((await Engine().PushModsAsync(scope: SyncScope.Shaders)).Success);
        Assert.IsTrue(_git.Touched.All(folder => folder == ShadersRepo));
        _git.Touched.Clear();
        var result = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.Shaders);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(_git.Touched.All(folder => folder == ShadersRepo));
        Assert.IsFalse(Directory.Exists(_config.ResolvedModsFolder));
    }

    [TestMethod]
    public async Task NewerChangesInSelectedRepositoryBlockPushBeforeAnyCheckoutWrites()
    {
        _git.Behind[PacksRepo] = 1;
        Write(Path.Combine(PacksRepo, "Pack.zip"), "old");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "new");
        var result = await Engine().PushModsAsync(scope: SyncScope.ResourcePacks);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message!, "Sync Resource Packs");
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(PacksRepo, "Pack.zip")));
        Assert.AreEqual(0, _git.Pushes.Count);
    }

    [TestMethod]
    public async Task FailedCategoryUploadCanRetryPendingCommitWithoutSyncingMods()
    {
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "pack");
        _git.FailedPush = PacksRepo;
        Assert.IsFalse((await Engine().PushModsAsync(scope: SyncScope.ResourcePacks)).Success);
        _git.Behind[ModsRepo] = 20; _git.FailedPush = null;
        var preview = await Engine().GetPushChangesAsync(scope: SyncScope.ResourcePacks);
        Assert.IsTrue(preview.Success, preview.Message);
        Assert.IsTrue(preview.Summary!.HasChanges);
        Assert.AreEqual(0, preview.Summary.TotalActionableChanges);
        CollectionAssert.AreEqual(new[] { "Resource Packs" }, preview.Summary.PendingRepositories);
        var retry = await Engine().PushModsAsync(scope: SyncScope.ResourcePacks);
        Assert.IsTrue(retry.Success, retry.Message);
        Assert.IsTrue(_git.Touched.All(folder => folder == PacksRepo));
        Assert.AreEqual(0, _git.Ahead[PacksRepo]);
    }

    [TestMethod]
    public async Task PushAllPreflightsAllRepositoriesAndReportsPartialUploadFailure()
    {
        Write(Path.Combine(_config.ResolvedModsFolder, "Mod.jar"), "mod");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "pack");
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip"), "shader");
        _git.FailedPush = PacksRepo;
        var result = await Engine().PushModsAsync();
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message!, "Already uploaded: Mods");
        CollectionAssert.AreEqual(new[] { ModsRepo, PacksRepo }, _git.Pushes.ToArray());
        Assert.IsFalse(File.Exists(Path.Combine(ShadersRepo, "Shader.zip")), "Repositories after the failed upload remain untouched.");
    }

    [TestMethod]
    public async Task CategoryPreviewRefreshAndCleanReinstallUseDedicatedSources()
    {
        Write(Path.Combine(PacksRepo, "Pack.zip"), "new");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "old");
        var preview = await Engine().CheckStatusAsync(scope: SyncScope.ResourcePacks, refreshRepository: true);
        Assert.IsTrue(_git.Touched.All(folder => folder == PacksRepo));
        Assert.AreEqual(1, preview.LocalModChanges.UpdatedCount);
        _git.Touched.Clear();
        var reinstall = await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true);
        Assert.IsTrue(reinstall.Success, reinstall.Error);
        Assert.AreEqual(3, _git.Touched.Distinct().Count());
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(reinstall.BackupFolder!, "resourcepacks", "Pack.zip")));
    }

    [TestMethod]
    public async Task DisabledCategoriesAreNotContactedAndSharedRepoModeRefreshesOnce()
    {
        _config.Config.SyncResourcePacks = false; _config.Config.SyncShaderPacks = false;
        await new RepositorySyncService(_config, _git).PrepareAsync(SyncScope.All, true, "test");
        Assert.IsTrue(_git.Touched.All(folder => folder == ModsRepo));
        _config.Config.SyncResourcePacks = true; _config.Config.SyncShaderPacks = true;
        _config.Config.ResourcePackRepository = ""; _config.Config.ShaderPackRepository = "";
        _git.Touched.Clear(); _git.Refreshes = 0;
        await new RepositorySyncService(_config, _git).PrepareAsync(SyncScope.All, true, "test");
        Assert.AreEqual(1, _git.Refreshes);
        Assert.AreEqual(1, new RepositorySyncService(_config, _git).Selected(SyncScope.All).Count);
    }

    [TestMethod]
    public void OldConfigDefaultsToDedicatedRepositoriesAndNestedPackLayoutStillWorks()
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>("{\"repository\":\"https://github.com/zyione/4stoogies-mod-list.git\"}")!;
        Assert.AreEqual(AppConfig.DefaultResourcePackRepositoryUrl, cfg.ResourcePackRepository);
        Assert.AreEqual(AppConfig.DefaultShaderPackRepositoryUrl, cfg.ShaderPackRepository);
        Write(Path.Combine(PacksRepo, "resourcepacks", "Pack.zip"), "pack");
        Write(Path.Combine(ShadersRepo, "shaderpacks", "Shader.zip"), "shader");
        var plan = new PackSyncService(_config).Plan();
        Assert.AreEqual(2, plan.AddedCount);
    }


    [TestMethod]
    public async Task ResourceReinstallRefreshesEvenMatchingFilesAndBacksUpOnlySelectedCategory()
    {
        var local = Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip");
        Write(local, "same"); Write(Path.Combine(PacksRepo, "Pack.zip"), "same");
        File.SetLastWriteTimeUtc(local, new DateTime(2000, 1, 1));
        Write(Path.Combine(PacksRepo, "Extracted", "pack.mcmeta"), "{}");
        Write(Path.Combine(PacksRepo, "resourcepack-order.txt"), "Pack.zip\nExtracted\n");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip"), "personal");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Old.zip"), "old");
        Write(Path.Combine(_config.MinecraftFolder, ".modsync-managed-packs.json"), "{\"resourcepacks\":[\"Pack.zip\",\"Old.zip\"]}");
        Write(Path.Combine(_config.MinecraftFolder, "options.txt"), "music:0.5\nresourcePacks:[\"vanilla\"]\n");
        var shader = Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip"); Write(shader, "shader");
        using var locked = new FileStream(shader, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true, scope: SyncScope.ResourcePacks);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, result.RestoredCount);
        StringAssert.Contains(File.ReadAllText(Path.Combine(_config.MinecraftFolder, "options.txt")), "resourcePacks:[\"vanilla\",\"file/Extracted\",\"file/Pack.zip\"]");
        Assert.IsTrue(File.GetLastWriteTimeUtc(local).Year > 2000);
        Assert.IsFalse(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "Old.zip")));
        Assert.AreEqual("personal", File.ReadAllText(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip")));
        Assert.AreEqual("same", File.ReadAllText(Path.Combine(result.BackupFolder!, "resourcepacks", "Pack.zip")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(result.BackupFolder!, "resourcepacks", "Old.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(result.BackupFolder!, "settings", "options.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(result.BackupFolder!, "shaderpacks")));
        Assert.IsFalse(Directory.Exists(_config.ResolvedModsFolder));
        Assert.IsTrue(_git.Touched.All(folder => folder == PacksRepo));
    }

    [TestMethod]
    public async Task ShaderReinstallSavesSelectionAndLeavesResourcePacksUntouched()
    {
        Write(Path.Combine(ShadersRepo, "Shader.zip"), "new");
        Write(Path.Combine(ShadersRepo, "active-shader.txt"), "Shader.zip");
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip"), "old");
        var props = Path.Combine(_config.MinecraftFolder, "config", "iris.properties");
        Write(props, "shaderPack=Old.zip\nenableShaders=true\n");
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Personal.zip"), "personal");
        var options = Path.Combine(_config.MinecraftFolder, "options.txt"); Write(options, "untouched");
        using var locked = new FileStream(options, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true, scope: SyncScope.Shaders);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.RestoredCount);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(result.BackupFolder!, "shaderpacks", "Shader.zip")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(result.BackupFolder!, "settings", "config", "iris.properties")), "Old.zip");
        StringAssert.Contains(File.ReadAllText(props), "Shader.zip");
        Assert.IsFalse(File.Exists(Path.Combine(result.BackupFolder!, "settings", "options.txt")));
        Assert.IsTrue(_git.Touched.All(folder => folder == ShadersRepo));
    }

    [TestMethod]
    public async Task OrderOnlyChangesSettingsWithoutReadingOrReplacingPackContents()
    {
        foreach (var name in new[] { "Top.zip", "Base.zip" })
        { Write(Path.Combine(PacksRepo, name), "remote"); Write(Path.Combine(_config.ResolvedResourcePacksFolder, name), "local"); }
        Write(Path.Combine(PacksRepo, "resourcepack-order.txt"), "Top.zip\nBase.zip\n");
        var options = Path.Combine(_config.MinecraftFolder, "options.txt");
        Write(options, "music:0.5\nresourcePacks:[\"vanilla\",\"file/Personal.zip\"]\n");
        var manifest = Path.Combine(_config.MinecraftFolder, ".modsync-managed-packs.json"); Write(manifest, "{}");
        _config.Config.EnforcePackOrder = false;
        using var remoteLock = new FileStream(Path.Combine(PacksRepo, "Top.zip"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var localLock = new FileStream(Path.Combine(_config.ResolvedResourcePacksFolder, "Top.zip"), FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.ResourcePackOrder);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(File.ReadAllText(options), "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Top.zip\",\"file/Personal.zip\"]");
        StringAssert.Contains(File.ReadAllText(options), "music:0.5");
        Assert.AreEqual("{}", File.ReadAllText(manifest));
        Assert.IsTrue(_git.Touched.All(folder => folder == PacksRepo));
        var backups = Directory.GetDirectories(Path.Combine(_config.MinecraftFolder, "modsync_backups"));
        Assert.AreEqual(1, backups.Length);
        Assert.IsTrue(File.Exists(Path.Combine(backups[0], "settings", "options.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(backups[0], "resourcepacks")));
        var time = File.GetLastWriteTimeUtc(options);
        var again = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.ResourcePackOrder);
        Assert.IsTrue(again.Success, again.Message);
        Assert.AreEqual(time, File.GetLastWriteTimeUtc(options));
        Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(_config.MinecraftFolder, "modsync_backups")).Length);
    }

    [TestMethod]
    public async Task MissingOrderOrMissingInstalledPackStopsBeforeChangingOptions()
    {
        var options = Path.Combine(_config.MinecraftFolder, "options.txt"); Write(options, "original");
        var engine = Engine();
        var missingOrder = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.ResourcePackOrder);
        Assert.IsFalse(missingOrder.Success);
        Write(Path.Combine(PacksRepo, "resourcepack-order.txt"), "Missing.zip");
        Write(Path.Combine(PacksRepo, "Missing.zip"), "remote");
        var missingPack = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.ResourcePackOrder);
        Assert.IsFalse(missingPack.Success);
        StringAssert.Contains(missingPack.Message!, "Sync Resource Packs first");
        Assert.AreEqual("original", File.ReadAllText(options));
        Assert.IsFalse(Directory.Exists(Path.Combine(_config.MinecraftFolder, "modsync_backups")));
    }

    [TestMethod]
    public async Task ReinstallAbortsBeforeWritesWhenRefreshOrBackupFails()
    {
        var local = Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"); Write(local, "old");
        Write(Path.Combine(PacksRepo, "Pack.zip"), "new");
        _git.FailedRefresh = PacksRepo;
        Assert.IsFalse((await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true, scope: SyncScope.ResourcePacks)).Success);
        Assert.AreEqual("old", File.ReadAllText(local));
        _git.FailedRefresh = null;
        Write(Path.Combine(_config.MinecraftFolder, "modsync_backups"), "blocked");
        Assert.IsFalse((await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true, scope: SyncScope.ResourcePacks)).Success);
        Assert.AreEqual("old", File.ReadAllText(local));
    }

    [TestMethod]
    public async Task ModsReinstallUsesOnlyModsRepositoryAndDisabledPackReinstallDoesNothing()
    {
        Write(Path.Combine(_config.MinecraftFolder, ".modsync-managed-packs.json"), "invalid unrelated metadata");
        Write(Path.Combine(ModsRepo, "mods", "New.jar"), "new");
        Write(Path.Combine(_config.ResolvedModsFolder, "Old.jar"), "old");
        var options = Path.Combine(_config.MinecraftFolder, "options.txt"); Write(options, "original");
        var result = await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true, scope: SyncScope.Mods);
        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(_git.Touched.All(folder => folder == ModsRepo));
        Assert.AreEqual("original", File.ReadAllText(options));
        _git.Touched.Clear(); _config.Config.SyncResourcePacks = false;
        Assert.IsFalse((await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true, scope: SyncScope.ResourcePacks)).Success);
        Assert.AreEqual(0, _git.Touched.Count);
    }


    [TestMethod]
    public async Task PushOrderPublishesOnlyPriorityDespiteDifferentPackContentsAndNewerMods()
    {
        _git.Behind[ModsRepo] = 20; _git.Behind[ShadersRepo] = 10; _git.Behind[PacksRepo] = 1;
        _config.Config.PublishResourcePackOrder = false;
        foreach (var name in new[] { "Top.zip", "Base.zip" })
        { Write(Path.Combine(PacksRepo, name), "remote"); Write(Path.Combine(_config.ResolvedResourcePacksFolder, name), "local"); }
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip"), "personal");
        var options = Path.Combine(_config.MinecraftFolder, "options.txt");
        var selection = "resourcePacks:[\"vanilla\",\"fabric\",\"file/Base.zip\",\"file/Top.zip\",\"file/Personal.zip\"]\n";
        Write(options, selection);
        using var remoteLock = new FileStream(Path.Combine(PacksRepo, "Top.zip"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var localLock = new FileStream(Path.Combine(_config.ResolvedResourcePacksFolder, "Top.zip"), FileMode.Open, FileAccess.Read, FileShare.None);
        var preview = await Engine().GetPushChangesAsync(scope: SyncScope.ResourcePackOrder);
        Assert.IsTrue(preview.Success, preview.Message);
        Assert.AreEqual(1, preview.Summary!.TotalActionableChanges);
        var result = await Engine().PushModsAsync(scope: SyncScope.ResourcePackOrder);
        Assert.IsTrue(result.Success, result.Message);
        var names = File.ReadAllLines(Path.Combine(PacksRepo, "resourcepack-order.txt")).Where(line => !line.StartsWith('#') && line.Length > 0).ToArray();
        CollectionAssert.AreEqual(new[] { "Top.zip", "Base.zip" }, names);
        CollectionAssert.AreEqual(new[] { "resourcepack-order.txt" }, _git.CommitPaths[PacksRepo]);
        Assert.AreEqual(selection, File.ReadAllText(options));
        Assert.IsTrue(_git.Touched.All(folder => folder == PacksRepo));
        Assert.IsFalse(File.Exists(Path.Combine(PacksRepo, "Personal.zip")));
    }

    [TestMethod]
    public async Task PushOrderRejectsPendingAssetCommitsBeforeUploadingAnything()
    {
        _git.Ahead[PacksRepo] = 1;
        var result = await Engine().PushModsAsync(scope: SyncScope.ResourcePackOrder);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message!, "unfinished upload");
        Assert.AreEqual(0, _git.Pushes.Count);
        Assert.IsFalse(File.Exists(Path.Combine(PacksRepo, "resourcepack-order.txt")));
    }


    [TestMethod]
    public async Task PackPushPublishesNewPacksAndPriorityTogetherAndSyncAppliesBothWithLegacyTogglesOff()
    {
        _config.Config.PublishResourcePackOrder = false; _config.Config.EnforcePackOrder = false;
        foreach (var name in new[] { "Top.zip", "Base.zip", "Disabled.zip" })
            Write(Path.Combine(_config.ResolvedResourcePacksFolder, name), "pack-" + name);
        var options = Path.Combine(_config.MinecraftFolder, "options.txt");
        Write(options, "music:0.4\nresourcePacks:[\"vanilla\",\"fabric\",\"file/Base.zip\",\"file/Top.zip\"]\n");
        var push = await Engine().PushModsAsync(scope: SyncScope.ResourcePacks);
        Assert.IsTrue(push.Success, push.Message);
        CollectionAssert.AreEquivalent(new[] { "Top.zip", "Base.zip", "Disabled.zip", "resourcepack-order.txt" }, _git.CommitPaths[PacksRepo]);
        var names = File.ReadAllLines(Path.Combine(PacksRepo, "resourcepack-order.txt")).Where(line => !line.StartsWith('#') && line.Length > 0).ToArray();
        CollectionAssert.AreEqual(new[] { "Top.zip", "Base.zip" }, names);
        Directory.Delete(_config.ResolvedResourcePacksFolder, true);
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip"), "personal");
        Write(options, "music:0.4\nresourcePacks:[\"vanilla\",\"fabric\",\"file/Personal.zip\"]\n");
        var sync = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.ResourcePacks);
        Assert.IsTrue(sync.Success, sync.Message);
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "Disabled.zip")));
        Assert.AreEqual("music:0.4\nresourcePacks:[\"vanilla\",\"fabric\",\"file/Base.zip\",\"file/Top.zip\",\"file/Personal.zip\"]\n", File.ReadAllText(options));
        var repeat = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.ResourcePacks);
        Assert.IsTrue(repeat.Success, repeat.Message);
        Assert.IsFalse(repeat.Summary!.HasChanges);
        Assert.IsTrue(_git.Touched.All(folder => folder == PacksRepo));
    }

    [TestMethod]
    public async Task PackPushWithoutSavedSelectionPreservesExistingPublishedOrder()
    {
        Write(Path.Combine(PacksRepo, "resourcepack-order.txt"), "# Existing priority\nPack.zip\n");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "pack");
        var result = await Engine().PushModsAsync(scope: SyncScope.ResourcePacks);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("# Existing priority\nPack.zip\n", File.ReadAllText(Path.Combine(PacksRepo, "resourcepack-order.txt")));
        CollectionAssert.AreEqual(new[] { "Pack.zip" }, _git.CommitPaths[PacksRepo]);
    }


    [TestMethod]
    public async Task ModsOnlyPushWorksDespiteNewerPacksAndInvalidPackTracking()
    {
        _git.Behind[PacksRepo] = 10; _git.Behind[ShadersRepo] = 10;
        Write(Path.Combine(_config.MinecraftFolder, ".modsync-managed-packs.json"), "invalid pack tracking");
        Write(Path.Combine(ModsRepo, "mods", "Old.jar"), "old");
        Write(Path.Combine(_config.ResolvedModsFolder, "New.jar"), "new");
        Write(Path.Combine(PacksRepo, "Pack.zip"), "pack");
        var preview = await Engine().GetPushChangesAsync(scope: SyncScope.Mods);
        Assert.IsTrue(preview.Success, preview.Message);
        var result = await Engine().PushModsAsync(scope: SyncScope.Mods);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(_git.Touched.All(folder => folder == ModsRepo));
        CollectionAssert.AreEquivalent(new[] { "mods/New.jar", "mods/Old.jar" }, _git.CommitPaths[ModsRepo]);
        Assert.AreEqual("pack", File.ReadAllText(Path.Combine(PacksRepo, "Pack.zip")));
        Assert.AreEqual("invalid pack tracking", File.ReadAllText(Path.Combine(_config.MinecraftFolder, ".modsync-managed-packs.json")));
    }

    private sealed class FakeGit : IGitService
    {
        public ConcurrentBag<string> Touched = new();
        public ConcurrentDictionary<string, int> Behind = new(), Ahead = new();
        public Dictionary<string, string[]> CommitPaths = new();
        public List<string> Pushes = new();
        public string? FailedRefresh, FailedPush;
        public int ConcurrentDownloads, MaxDownloads, Refreshes;
        private int _active;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> EnsureGitAvailableAsync(Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult(true);
        public Task<bool> VerifyOrResetRemoteAsync(string folder, string url) { Touched.Add(folder); return Task.FromResult(true); }
        public Task<(bool Success, string? Error)> CloneAsync(string url, string folder, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => PullOrResetToRemoteAsync(folder, branch, token, progressCallback);
        public async Task<(bool Success, string? Error)> PullOrResetToRemoteAsync(string folder, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null)
        {
            Touched.Add(folder); Interlocked.Increment(ref Refreshes);
            int active = Interlocked.Increment(ref _active);
            if (ConcurrentDownloads > 0)
            {
                if (active >= ConcurrentDownloads) { MaxDownloads = active; _gate.TrySetResult(); }
                await _gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Interlocked.Decrement(ref _active);
            if (folder != FailedRefresh) Behind[folder] = 0;
            return (folder != FailedRefresh, folder == FailedRefresh ? "offline" : null);
        }
        public Task<(bool Success, string? Error)> FetchAsync(string folder, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) { Touched.Add(folder); return Task.FromResult<(bool, string?)>((true, null)); }
        public Task<GitStatusInfo> GetStatusAsync(string folder, string url, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null)
        {
            Touched.Add(folder); return Task.FromResult(new GitStatusInfo { IsCloned = true, IsConnected = true, RepositoryUrl = url, Branch = branch, BehindCount = Behind.GetValueOrDefault(folder), AheadCount = Ahead.GetValueOrDefault(folder) });
        }
        public Task<(bool Success, string? Error)> StageAndCommitAsync(string folder, string message, Action<SyncProgressInfo>? progressCallback = null, IReadOnlyList<string>? paths = null)
        {
            Touched.Add(folder); CommitPaths[folder] = paths!.ToArray(); Ahead.AddOrUpdate(folder, 1, (_, old) => old + 1); return Task.FromResult<(bool, string?)>((true, null));
        }
        public Task<(bool Success, string? Error)> PushAsync(string folder, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null)
        {
            Touched.Add(folder); Pushes.Add(folder); if (folder != FailedPush) Ahead[folder] = 0;
            return Task.FromResult<(bool, string?)>((folder != FailedPush, folder == FailedPush ? "upload failed" : null));
        }
    }
}
