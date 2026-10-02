using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class PackSyncTests
{
    private string _root = null!;
    private ConfigService _config = null!;
    private LoggingService _logger = null!;
    private string Repo => _config.ResolvedRepositoryFolder;
    private string Instance => _config.MinecraftFolder;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ModSync_PackTests_" + Guid.NewGuid().ToString("N"));
        _logger = new LoggingService();
        _config = new ConfigService(_logger);
        _config.Config.ResourcePackRepository = "";
        _config.Config.ShaderPackRepository = "";
        _config.Config.RepositoryFolder = Path.Combine(_root, "repository");
        _config.Config.ModsFolder = Path.Combine(_root, "instance", "mods");
        _config.Config.RequireConfirmationBeforeSync = false;
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        Directory.CreateDirectory(Instance);
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private ModSyncService Engine() => new(_config, new OfflineGit(), new AuthenticationService(_logger),
        new MinecraftCheckService(_logger), new ModIgnoreService(_config, _logger), _logger);

    [TestMethod]
    public void OrderReversesPriorityAndPreservesBuiltinsPersonalAndOtherSettings()
    {
        var options = "fov:90\r\nresourcePacks:[\"vanilla\",\"fabric\",\"file/Top.zip\",\"file/Personal.zip\"]\r\nmusic:0.4\r\n";
        var updated = PackSyncService.ApplyPackOrder(options, new[] { "Top.zip", "Base.zip" }, new[] { "Top.zip", "Base.zip" });
        Assert.AreEqual("fov:90\r\nresourcePacks:[\"vanilla\",\"fabric\",\"file/Base.zip\",\"file/Top.zip\",\"file/Personal.zip\"]\r\nmusic:0.4\r\n", updated);
        Assert.AreEqual(updated, PackSyncService.ApplyPackOrder(updated, new[] { "Top.zip", "Base.zip" }, new[] { "Top.zip", "Base.zip" }));
    }

    [TestMethod]
    public void MalformedOptionsOrInvalidDeclarationsFailWithoutWriting()
    {
        Assert.ThrowsException<JsonException>(() => PackSyncService.ApplyPackOrder("resourcePacks:broken", new[] { "A.zip" }, new[] { "A.zip" }));
        Assert.ThrowsException<InvalidDataException>(() => PackSyncService.ApplyPackOrder("", new[] { "Missing.zip" }, new[] { "A.zip" }));
        Assert.ThrowsException<InvalidDataException>(() => PackSyncService.ApplyPackOrder("", new[] { "A.zip", "A.zip" }, new[] { "A.zip" }));
        Assert.ThrowsException<InvalidDataException>(() => PackSyncService.ValidatePackName("../escape.zip"));
        Assert.ThrowsException<InvalidDataException>(() => PackSyncService.ValidatePackName("C:\\escape.zip"));
    }

    [TestMethod]
    public void ScansZipAndExtractedResourcePacksOnly()
    {
        var packs = Path.Combine(Repo, "resourcepacks");
        Write(Path.Combine(packs, "Zip.zip"), "zip");
        Write(Path.Combine(packs, "Folder", "pack.mcmeta"), "{}");
        Write(Path.Combine(packs, "Folder", "assets", "texture.png"), "texture");
        Write(Path.Combine(packs, "README.txt"), "preserve");
        Write(Path.Combine(packs, "unrelated", "data.txt"), "preserve");
        var files = PackSyncService.ScanPacks(packs, true);
        Assert.AreEqual(3, files.Count);
        Assert.IsTrue(files.ContainsKey("Folder/assets/texture.png"));
        Assert.AreEqual(1, PackSyncService.ScanPacks(packs, false).Count);
    }

    [TestMethod]
    public async Task CombinedSyncCopiesAllAssetsAndAppliesSettingsEvenWithoutModChanges()
    {
        Write(Path.Combine(Repo, "mods", "Test.jar"), "mod");
        Write(Path.Combine(Repo, "resourcepacks", "Top.zip"), "pack");
        Write(Path.Combine(Repo, "resourcepacks", "Folder", "pack.mcmeta"), "{}");
        Write(Path.Combine(Repo, "shaderpacks", "Shader.zip"), "shader");
        Write(Path.Combine(Repo, "resourcepack-order.txt"), "# Highest first\nTop.zip\nFolder\n");
        Write(Path.Combine(Repo, "active-shader.txt"), "Shader.zip\n");
        Write(Path.Combine(Instance, "options.txt"), "fov:88\nresourcePacks:[\"vanilla\",\"fabric\",\"file/Personal.zip\"]\nsound:0.6\n");
        var engine = Engine();
        var result = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual("mod", File.ReadAllText(Path.Combine(_config.ResolvedModsFolder, "Test.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "Folder", "pack.mcmeta")));
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip")));
        Assert.IsTrue(File.ReadAllText(Path.Combine(Instance, "options.txt")).Contains("fov:88\n"));
        Assert.IsTrue(File.ReadAllText(Path.Combine(Instance, "config", "iris.properties")).Contains("enableShaders=true"));
        Assert.IsFalse(new PackSyncService(_config).Plan().HasChanges, "A second sync must be idempotent.");
        Write(Path.Combine(Instance, "options.txt"), "fov:88\nresourcePacks:[\"vanilla\"]\n");
        var settingsOnly = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true);
        Assert.IsTrue(settingsOnly.Success, settingsOnly.Message);
        Assert.IsTrue(settingsOnly.Summary!.Updated.Any(x => x.RelativePath.Contains("order")));
    }

    [TestMethod]
    public async Task LegacyRootModsRemainSupported()
    {
        Write(Path.Combine(Repo, "Legacy.jar"), "legacy");
        var result = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedModsFolder, "Legacy.jar")));
    }

    [TestMethod]
    public async Task RemovedManagedPacksAreDeletedWhilePersonalFilesRemain()
    {
        var pack = Path.Combine(Repo, "resourcepacks", "Managed.zip");
        Write(pack, "managed");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip"), "personal");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "README.txt"), "readme");
        var engine = Engine();
        Assert.IsTrue((await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true)).Success);
        File.Delete(pack);
        Assert.IsTrue((await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true)).Success);
        Assert.IsFalse(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "Managed.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "README.txt")));
    }

    [TestMethod]
    public async Task InvalidOrderPreventsAllAssetWrites()
    {
        Write(Path.Combine(Repo, "mods", "Test.jar"), "mod");
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "pack");
        Write(Path.Combine(Repo, "resourcepack-order.txt"), "Missing.zip");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true));
        Assert.IsFalse(File.Exists(Path.Combine(_config.ResolvedModsFolder, "Test.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip")));
    }

    [TestMethod]
    public void MissingDeclarationsLeaveUserSettingsUntouched()
    {
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "pack");
        Write(Path.Combine(Repo, "shaderpacks", "Shader.zip"), "shader");
        Write(Path.Combine(Instance, "options.txt"), "resourcePacks:invalid-but-untouched\n");
        var plan = new PackSyncService(_config).Plan();
        Assert.IsFalse(plan.Changes.Any(x => x.RelativePath.Contains("options") || x.RelativePath.Contains("iris")));
    }

    [TestMethod]
    public void TogglesDisableAssetsAndDeclarations()
    {
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "pack");
        Write(Path.Combine(Repo, "shaderpacks", "Shader.zip"), "shader");
        Write(Path.Combine(Repo, "resourcepack-order.txt"), "Missing.zip");
        Write(Path.Combine(Repo, "active-shader.txt"), "Missing.zip");
        _config.Config.SyncResourcePacks = false;
        _config.Config.SyncShaderPacks = false;
        Assert.IsFalse(new PackSyncService(_config).Plan().HasChanges);
        _config.Config.SyncResourcePacks = true;
        _config.Config.SyncShaderPacks = true;
        _config.Config.EnforcePackOrder = false;
        _config.Config.EnforceActiveShader = false;
        Assert.IsTrue(new PackSyncService(_config).Plan().HasChanges);
    }

    [TestMethod]
    public void LegacyShadersAndCustomFoldersPreserveOtherSettings()
    {
        _config.Config.ShaderPacksFolder = "visuals/shaders";
        Write(Path.Combine(Repo, "shaderpacks", "Shader.zip"), "shader");
        Write(Path.Combine(Repo, "active-shader.txt"), "Shader.zip");
        Write(Path.Combine(Instance, "optionsshaders.txt"), "shadowResMul=2.0\r\nshaderPack=Old.zip\r\n");
        var plan = new PackSyncService(_config).Plan();
        var settings = plan.Changes.Single(x => x.NewContent != null && x.RelativePath.Contains("active shader"));
        Assert.AreEqual("shadowResMul=2.0\r\nshaderPack=Shader.zip\r\n", settings.NewContent);
        Assert.AreEqual(Path.Combine(Instance, "visuals", "shaders", "Shader.zip"), plan.Added.First().DestinationPath);
    }

    [TestMethod]
    public async Task PushPreviewIncludesPacksAndUsesModsSubfolder()
    {
        Write(Path.Combine(Repo, "mods", "Test.jar"), "original");
        Write(Path.Combine(_config.ResolvedModsFolder, "Test.jar"), "updated");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "pack");
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip"), "shader");
        var result = await Engine().GetPushChangesAsync();
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(1, result.Summary!.UpdatedCount);
        Assert.AreEqual(2, result.Summary.AddedCount);
        Assert.IsTrue(result.Summary.Added.Any(x => x.DestinationPath == Path.Combine(Repo, "resourcepacks", "Pack.zip")));
        var apply = typeof(ModSyncService).GetMethod("ApplyChangesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var applied = await (Task<(bool Success, string? Error)>)apply.Invoke(Engine(),
            new object?[] { result.Summary, _config.ResolvedModsFolder, Repo, null })!;
        Assert.IsTrue(applied.Success, applied.Error);
        Assert.AreEqual("updated", File.ReadAllText(Path.Combine(Repo, "mods", "Test.jar")));
        Assert.AreEqual("pack", File.ReadAllText(Path.Combine(Repo, "resourcepacks", "Pack.zip")));
        Assert.AreEqual("shader", File.ReadAllText(Path.Combine(Repo, "shaderpacks", "Shader.zip")));
        Assert.IsFalse(File.Exists(Path.Combine(Repo, "Test.jar")));
    }

    [TestMethod]
    public async Task CleanReinstallAlsoSynchronizesPacksAndKeepsPersonalPacks()
    {
        Write(Path.Combine(Repo, "mods", "New.jar"), "new");
        Write(Path.Combine(_config.ResolvedModsFolder, "Old.jar"), "old");
        Write(Path.Combine(Repo, "resourcepacks", "Shared.zip"), "shared");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip"), "personal");
        var result = await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(result.BackupFolder!, "mods", "Old.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "Shared.zip")));
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip")));
    }

    [TestMethod]
    public void NewlyDownloadedIrisOverridesLegacyShaderFallback()
    {
        Write(Path.Combine(Repo, "mods", "iris-1.0.jar"), "iris");
        Write(Path.Combine(Repo, "shaderpacks", "Shader.zip"), "shader");
        Write(Path.Combine(Repo, "active-shader.txt"), "Shader.zip");
        Write(Path.Combine(Instance, "optionsshaders.txt"), "shaderPack=Old.zip\n");
        var plan = new PackSyncService(_config).Plan();
        Assert.IsTrue(plan.Changes.Any(x => x.DestinationPath == Path.Combine(Instance, "config", "iris.properties")));
        Assert.IsFalse(plan.Changes.Any(x => x.DestinationPath == Path.Combine(Instance, "optionsshaders.txt")));
    }

    [TestMethod]
    public void OverlappingFoldersAreRejected()
    {
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "pack");
        _config.Config.ResourcePacksFolder = _config.ResolvedModsFolder;
        Assert.ThrowsException<InvalidDataException>(() => new PackSyncService(_config).Plan());
        _config.Config.ResourcePacksFolder = "shaderpacks/nested";
        Assert.ThrowsException<InvalidDataException>(() => new PackSyncService(_config).Plan());
    }

    [TestMethod]
    public async Task ResourceOnlySyncLeavesModsAndShaderSettingsUntouchedAndIsIdempotent()
    {
        Write(Path.Combine(Repo, "mods", "New.jar"), "new");
        Write(Path.Combine(_config.ResolvedModsFolder, "Personal.jar"), "personal");
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "resource");
        Write(Path.Combine(Repo, "shaderpacks", "Shader.zip"), "shader");
        Write(Path.Combine(Repo, "active-shader.txt"), "Missing.zip");
        Write(Path.Combine(Repo, "resourcepack-order.txt"), "Pack.zip");
        var engine = Engine();
        var first = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.ResourcePacks);
        Assert.IsTrue(first.Success, first.Message);
        Assert.IsFalse(File.Exists(Path.Combine(_config.ResolvedModsFolder, "New.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedModsFolder, "Personal.jar")));
        Assert.IsFalse(Directory.Exists(_config.ResolvedShaderPacksFolder));
        var pack = Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip");
        var options = Path.Combine(Instance, "options.txt");
        var packTime = File.GetLastWriteTimeUtc(pack);
        var optionsTime = File.GetLastWriteTimeUtc(options);
        var second = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.ResourcePacks);
        Assert.IsTrue(second.Success, second.Message);
        Assert.IsFalse(second.Summary!.HasChanges);
        Assert.AreEqual(packTime, File.GetLastWriteTimeUtc(pack));
        Assert.AreEqual(optionsTime, File.GetLastWriteTimeUtc(options));
    }

    [TestMethod]
    public async Task ShaderOnlySyncDoesNotReadInvalidResourceOrderOrModifyMods()
    {
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "resource");
        Write(Path.Combine(Repo, "resourcepack-order.txt"), "Missing.zip");
        Write(Path.Combine(Repo, "shaderpacks", "Shader.zip"), "shader");
        Write(Path.Combine(Repo, "active-shader.txt"), "Shader.zip");
        var result = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.Shaders);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsFalse(Directory.Exists(_config.ResolvedResourcePacksFolder));
        Assert.IsFalse(Directory.Exists(_config.ResolvedModsFolder));
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip")));
        Assert.IsFalse(new PackSyncService(_config).Plan(scope: SyncScope.Shaders).HasChanges);
    }

    [TestMethod]
    public async Task ModsOnlySyncSkipsAllVisualDeclarations()
    {
        Write(Path.Combine(Repo, "mods", "Test.jar"), "mod");
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "resource");
        Write(Path.Combine(Repo, "resourcepack-order.txt"), "Missing.zip");
        var result = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true, scope: SyncScope.Mods);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(File.Exists(Path.Combine(_config.ResolvedModsFolder, "Test.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(Instance, "options.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(Instance, ".modsync-managed-packs.json")));
    }

    [TestMethod]
    public void PublishingOrderUsesSharedPacksOnlyAndHighestPriorityFirst()
    {
        foreach (var name in new[] { "Base.zip", "Top.zip" })
        {
            Write(Path.Combine(Repo, "resourcepacks", name), "resource");
            Write(Path.Combine(_config.ResolvedResourcePacksFolder, name), "resource");
        }
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip"), "personal");
        var options = "resourcePacks:[\"vanilla\",\"file/Base.zip\",\"file/Top.zip\",\"file/Personal.zip\"]\nfov:90\n";
        Write(Path.Combine(Instance, "options.txt"), options);
        _config.Config.PublishResourcePackOrder = true;
        var plan = new PackSyncService(_config).Plan(push: true);
        var order = plan.Added.Single(x => x.RelativePath.StartsWith("resourcepack-order.txt"));
        Assert.IsTrue(order.NewContent!.EndsWith("Top.zip\nBase.zip\n"));
        Assert.IsFalse(order.NewContent.Contains("Personal"));
        Assert.AreEqual(options, File.ReadAllText(Path.Combine(Instance, "options.txt")));
        Write(Path.Combine(Repo, "resourcepack-order.txt"), order.NewContent);
        Assert.IsFalse(new PackSyncService(_config).Plan(push: true).Changes.Any(x => x.RelativePath.StartsWith("resourcepack-order.txt")));
    }

    [TestMethod]
    public async Task UnreadableRepositoryModsAbortBeforeDeletingLocalFiles()
    {
        Write(Path.Combine(Repo, "mods", "Locked.jar"), "mod");
        Write(Path.Combine(_config.ResolvedModsFolder, "Existing.jar"), "existing");
        using var locked = new FileStream(Path.Combine(Repo, "mods", "Locked.jar"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsExceptionAsync<IOException>(() => Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true));
        Assert.AreEqual("existing", File.ReadAllText(Path.Combine(_config.ResolvedModsFolder, "Existing.jar")));
    }

    [TestMethod]
    public async Task SettingsChangedAfterPlanningArePreserved()
    {
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "resource");
        Write(Path.Combine(Repo, "resourcepack-order.txt"), "Pack.zip");
        Write(Path.Combine(Instance, "options.txt"), "fov:80\nresourcePacks:[\"vanilla\"]\n");
        var plan = new PackSyncService(_config).Plan();
        var newer = "fov:100\nresourcePacks:[\"vanilla\"]\n";
        Write(Path.Combine(Instance, "options.txt"), newer);
        var apply = typeof(ModSyncService).GetMethod("ApplyChangesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = await (Task<(bool Success, string? Error)>)apply.Invoke(Engine(), new object?[] { plan, Repo, _config.ResolvedModsFolder, null })!;
        Assert.IsFalse(result.Success);
        Assert.AreEqual(newer, File.ReadAllText(Path.Combine(Instance, "options.txt")));
    }

    [TestMethod]
    public async Task ChangedSourceIsRejectedBeforeReplacingExistingPack()
    {
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "source");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "existing");
        var plan = new PackSyncService(_config).Plan();
        Write(Path.Combine(Repo, "resourcepacks", "Pack.zip"), "changed during copy");
        var apply = typeof(ModSyncService).GetMethod("ApplyChangesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = await (Task<(bool Success, string? Error)>)apply.Invoke(Engine(), new object?[] { plan, Repo, _config.ResolvedModsFolder, null })!;
        Assert.IsFalse(result.Success);
        Assert.AreEqual("existing", File.ReadAllText(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip")));
    }

    [TestMethod]
    public async Task VisualOnlyPushPreviewNeverIncludesModsOrOtherPacks()
    {
        Write(Path.Combine(Repo, "mods", "Mod.jar"), "old");
        Write(Path.Combine(_config.ResolvedModsFolder, "Mod.jar"), "new");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Pack.zip"), "pack");
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip"), "shader");
        foreach (var scope in new[] { SyncScope.ResourcePacks, SyncScope.Shaders })
        {
            var preview = await Engine().GetPushChangesAsync(scope: scope);
            Assert.IsTrue(preview.Success, preview.Message);
            Assert.AreEqual(1, preview.Summary!.AddedCount);
            string prefix = scope == SyncScope.ResourcePacks ? "resourcepacks/" : "shaderpacks/";
            Assert.IsTrue(preview.Summary.Changes.All(c => c.RelativePath.StartsWith(prefix)));
            var apply = typeof(ModSyncService).GetMethod("ApplyChangesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var result = await (Task<(bool Success, string? Error)>)apply.Invoke(Engine(), new object?[] { preview.Summary, _config.ResolvedModsFolder, Repo, null })!;
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(Repo, "mods", "Mod.jar")));
        }
        Assert.IsFalse(Directory.Exists(Path.Combine(Instance, "modsync_backups")), "Uploads must not make local pack backups.");
    }

    [TestMethod]
    public async Task SyncBacksUpReplacedDeletedPacksAndSettingsAndNoOpMakesNoBackup()
    {
        Write(Path.Combine(Repo, "resourcepacks", "Shared.zip"), "new pack");
        Write(Path.Combine(Repo, "resourcepack-order.txt"), "Shared.zip");
        Write(Path.Combine(Repo, "shaderpacks", "Shared.zip"), "new shader");
        Write(Path.Combine(Repo, "active-shader.txt"), "Shared.zip");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Shared.zip"), "old pack");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "OldFolder", "pack.mcmeta"), "old metadata");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "OldFolder", "assets", "image.png"), "old texture");
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Shared.zip"), "old shader");
        Write(Path.Combine(Instance, "options.txt"), "fov:90\nresourcePacks:[\"vanilla\"]\n");
        Write(Path.Combine(Instance, "config", "iris.properties"), "shaderPack=Old.zip\n");
        Write(Path.Combine(Instance, ".modsync-managed-packs.json"), JsonSerializer.Serialize(new Dictionary<string,string[]> {
            ["resourcepacks"] = new[] { "Shared.zip", "OldFolder/pack.mcmeta", "OldFolder/assets/image.png" }, ["shaderpacks"] = new[] { "Shared.zip" } }));
        var engine = Engine();
        var result = await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true);
        Assert.IsTrue(result.Success, result.Message);
        var backup = Directory.GetDirectories(Path.Combine(Instance, "modsync_backups")).Single();
        Assert.AreEqual("old pack", File.ReadAllText(Path.Combine(backup, "resourcepacks", "Shared.zip")));
        Assert.AreEqual("old shader", File.ReadAllText(Path.Combine(backup, "shaderpacks", "Shared.zip")));
        Assert.AreEqual("old texture", File.ReadAllText(Path.Combine(backup, "resourcepacks", "OldFolder", "assets", "image.png")));
        Assert.AreEqual("fov:90\nresourcePacks:[\"vanilla\"]\n", File.ReadAllText(Path.Combine(backup, "settings", "options.txt")));
        Assert.AreEqual("shaderPack=Old.zip\n", File.ReadAllText(Path.Combine(backup, "settings", "config", "iris.properties")));
        Assert.IsTrue(File.Exists(Path.Combine(backup, "restore-paths.json")));
        Assert.IsTrue((await engine.SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true)).Success);
        Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(Instance, "modsync_backups")).Length);
    }

    [TestMethod]
    public async Task ReinstallSnapshotsPersonalPacksAndCustomFoldersBeforeChanges()
    {
        _config.Config.ResourcePacksFolder = "visuals/packs";
        _config.Config.ShaderPacksFolder = "visuals/shaders";
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip"), "personal");
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip"), "shader");
        Write(Path.Combine(_config.ResolvedShaderPacksFolder, "Shader.zip.txt"), "settings");
        Write(Path.Combine(Repo, "resourcepacks", "New.zip"), "new");
        var result = await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true);
        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.BackupFolder);
        Assert.AreEqual("personal", File.ReadAllText(Path.Combine(result.BackupFolder, "resourcepacks", "Personal.zip")));
        Assert.AreEqual("shader", File.ReadAllText(Path.Combine(result.BackupFolder, "shaderpacks", "Shader.zip")));
        Assert.AreEqual("settings", File.ReadAllText(Path.Combine(result.BackupFolder, "shaderpacks", "Shader.zip.txt")));
        Assert.AreEqual("personal", File.ReadAllText(Path.Combine(_config.ResolvedResourcePacksFolder, "Personal.zip")));
    }

    [TestMethod]
    public async Task BackupFailureLeavesPacksAndModsUntouched()
    {
        Write(Path.Combine(Repo, "mods", "New.jar"), "new");
        Write(Path.Combine(_config.ResolvedModsFolder, "Old.jar"), "old");
        Write(Path.Combine(Repo, "resourcepacks", "Shared.zip"), "new");
        string pack = Path.Combine(_config.ResolvedResourcePacksFolder, "Shared.zip");
        Write(pack, "old");
        using (var locked = new FileStream(pack, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await Engine().CleanReinstallAsync(forceIfMinecraftRunning: true);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(_config.ResolvedModsFolder, "Old.jar")));
            Assert.IsFalse(File.Exists(Path.Combine(_config.ResolvedModsFolder, "New.jar")));
        }
        Assert.AreEqual("old", File.ReadAllText(pack));
    }

    [TestMethod]
    public async Task UnavailableBackupDestinationAbortsSyncBeforeModOrPackWrites()
    {
        Write(Path.Combine(Instance, "modsync_backups"), "blocked destination");
        Write(Path.Combine(Repo, "mods", "New.jar"), "new");
        Write(Path.Combine(Repo, "resourcepacks", "Shared.zip"), "new");
        Write(Path.Combine(_config.ResolvedResourcePacksFolder, "Shared.zip"), "old");
        var result = await Engine().SyncModsAsync(forceIfMinecraftRunning: true, skipConfirmation: true);
        Assert.IsFalse(result.Success);
        Assert.IsFalse(File.Exists(Path.Combine(_config.ResolvedModsFolder, "New.jar")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(_config.ResolvedResourcePacksFolder, "Shared.zip")));
    }

    [TestMethod]
    public void NewRepositoryUsesModsFolderAndLegacyRootJarsRemainSupported()
    {
        Assert.AreEqual(Path.Combine(Repo, "mods"), PackSyncService.RepositoryModsFolder(Repo));
        Write(Path.Combine(Repo, "Legacy.jar"), "legacy");
        Assert.AreEqual(Repo, PackSyncService.RepositoryModsFolder(Repo));
        Directory.CreateDirectory(Path.Combine(Repo, "mods"));
        Assert.AreEqual(Path.Combine(Repo, "mods"), PackSyncService.RepositoryModsFolder(Repo));
    }

    private sealed class OfflineGit : IGitService
    {
        public Task<bool> EnsureGitAvailableAsync(Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult(true);
        public Task<bool> VerifyOrResetRemoteAsync(string repoDir, string expectedUrl) => Task.FromResult(true);
        public Task<(bool Success, string? Error)> CloneAsync(string repositoryUrl, string targetDir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult<(bool, string?)>((true, null));
        public Task<(bool Success, string? Error)> FetchAsync(string repoDir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult<(bool, string?)>((true, null));
        public Task<(bool Success, string? Error)> PullOrResetToRemoteAsync(string repoDir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult<(bool, string?)>((true, null));
        public Task<GitStatusInfo> GetStatusAsync(string repoDir, string repositoryUrl, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult(new GitStatusInfo { IsConnected = true, IsCloned = true });
        public Task<(bool Success, string? Error)> StageAndCommitAsync(string repoDir, string commitMessage, Action<SyncProgressInfo>? progressCallback = null, IReadOnlyList<string>? paths = null) => Task.FromResult<(bool, string?)>((true, null));
        public Task<(bool Success, string? Error)> PushAsync(string repoDir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult<(bool, string?)>((true, null));
    }
}
