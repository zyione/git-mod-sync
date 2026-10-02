using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;

namespace ModSync.Tests;
[TestClass]
public class InstanceSelectionTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "ModSync_instances_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }
    private string Instance(string name) { var folder = Path.Combine(_root, name); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "options.txt"), ""); return Path.GetFullPath(folder); }
    [TestMethod] public void DiscoveryFindsCurrentAndLauncherGameRootsWithoutDuplicates()
    {
        var current = Instance("current"); var nested = Instance("instances/Profile/.minecraft");
        var found = InstanceDiscoveryService.Discover(current, current, new[] { Path.Combine(_root, "instances") });
        Assert.AreEqual(2, found.Count); Assert.AreEqual(current, found[0].Folder); Assert.IsTrue(found.Any(i => i.Folder == nested));
    }
    [TestMethod] public void DiscoveryDoesNotMistakeAnEmptyFolderOrModsOnlyFolderForAGame()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        Assert.IsFalse(InstanceDiscoveryService.IsInstance(_root));
        Assert.AreEqual(0, InstanceDiscoveryService.Discover(_root, _root, Array.Empty<string>()).Count);
    }
    [TestMethod] public void LauncherCustomDirectoriesAreFoundAndMalformedProfilesDoNotBlockOtherInstances()
    {
        var current = Instance("current"); var custom = Instance("custom"); var profiles = Path.Combine(_root, "profiles.json");
        File.WriteAllText(profiles, JsonSerializer.Serialize(new { profiles = new { test = new { name = "Custom profile", gameDir = custom } } }));
        var found = InstanceDiscoveryService.Discover(current, current, Array.Empty<string>(), profiles);
        Assert.AreEqual(2, found.Count); Assert.IsTrue(found.Any(i => i.Folder == custom));
        File.WriteAllText(profiles, "broken");
        Assert.AreEqual(1, InstanceDiscoveryService.Discover(current, current, Array.Empty<string>(), profiles).Count);
    }
    [TestMethod] public void ChoiceSurvivesReloadAndAllCategoryFoldersFollowTheInstance()
    {
        var configPath = Path.Combine(_root, "config.json"); var chosen = Instance("game");
        var config = new ConfigService(new LoggingService(), configPath);
        Assert.IsTrue(config.SelectInstance(chosen));
        var loaded = new ConfigService(new LoggingService(), configPath); Assert.IsTrue(loaded.Load().Success);
        Assert.IsTrue(loaded.Config.InstanceSelectionCompleted); Assert.AreEqual(chosen, loaded.MinecraftFolder);
        Assert.AreEqual(Path.Combine(chosen, "resourcepacks"), loaded.ResolvedResourcePacksFolder);
        Assert.AreEqual(Path.Combine(chosen, "shaderpacks"), loaded.ResolvedShaderPacksFolder);
    }
    [TestMethod] public void FailedSaveRestoresPreviousInstanceAndConfirmationFlag()
    {
        var config = new ConfigService(new LoggingService(), Path.Combine(_root, "missing", "config.json"));
        string old = config.Config.ModsFolder;
        Assert.IsFalse(config.SelectInstance(Instance("game")));
        Assert.AreEqual(old, config.Config.ModsFolder); Assert.IsFalse(config.Config.InstanceSelectionCompleted);
    }
    [TestMethod] public void ExistingSyncedConfigAndCustomFolderSkipFirstRunAfterUpgrade()
    {
        var file = Path.Combine(_root, "config.json");
        foreach (var json in new[] { "{\"firstSyncCompleted\":true}", JsonSerializer.Serialize(new { modsFolder = Path.Combine(Instance("old"), "mods") }) })
        {
            File.WriteAllText(file, json); var config = new ConfigService(new LoggingService(), file);
            Assert.IsTrue(config.Load().Success); Assert.IsTrue(config.Config.InstanceSelectionCompleted);
        }
        File.WriteAllText(file, "{}"); var fresh = new ConfigService(new LoggingService(), file); fresh.Load();
        Assert.IsFalse(fresh.Config.InstanceSelectionCompleted);
    }
    [TestMethod] public void ExplicitUnconfirmedChoiceIsNotAutoMigrated()
    {
        var file = Path.Combine(_root, "config.json");
        File.WriteAllText(file, "{\"firstSyncCompleted\":true,\"instanceSelectionCompleted\":false}");
        var config = new ConfigService(new LoggingService(), file); config.Load(); Assert.IsFalse(config.Config.InstanceSelectionCompleted);
    }
    [TestMethod] public void RecoveryRoutesCommonErrorsToUsefulNonDestructiveActions()
    {
        Assert.AreEqual(RecoveryAction.Retry, ErrorRecoveryService.Suggest("Minecraft is currently running", true).Action);
        Assert.AreEqual(RecoveryAction.SignIn, ErrorRecoveryService.Suggest("401 authentication failed", true).Action);
        Assert.AreEqual(RecoveryAction.SyncSection, ErrorRecoveryService.Suggest("newer repository changes", true).Action);
        Assert.AreEqual(RecoveryAction.ChooseInstance, ErrorRecoveryService.Suggest("instance missing", true).Action);
        Assert.AreEqual(RecoveryAction.OpenBackups, ErrorRecoveryService.Suggest("not enough space", true).Action);
        Assert.AreEqual(RecoveryAction.ViewLogs, ErrorRecoveryService.Suggest("checksum verification failed", true).Action);
        Assert.AreEqual(RecoveryAction.ViewLogs, ErrorRecoveryService.Suggest("unknown", false).Action);
    }
}
