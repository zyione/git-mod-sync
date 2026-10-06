using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;
using ModSync.Utils;

namespace ModSync.Tests;

[TestClass]
public class PortableLaunchTests
{
    private string root = null!, app = null!, game = null!, cfg = null!;
    [TestInitialize] public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "ModSync_portable_" + Guid.NewGuid().ToString("N"));
        app = Path.Combine(root, "ModSync"); game = Path.Combine(root, "instances", "Big Test # Ω", ".minecraft");
        Directory.CreateDirectory(app); Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(root, "UltimMC.exe"), "fixture");
        File.WriteAllText(Path.Combine(game, "options.txt"), "");
        cfg = Path.Combine(Path.GetDirectoryName(game)!, "instance.cfg");
        File.WriteAllText(cfg, "name=Big Test\nOverrideCommands=false\nPreLaunchCommand=dormant\nUnrelated=keep\n");
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(root, true);

    [TestMethod] public void EnablePreservesInheritedCommandsAndDisableRestoresOnlyOwnedKeys()
    {
        File.WriteAllText(Path.Combine(root, "ultimmc.cfg"), "PreLaunchCommand=echo before\nWrapperCommand=wrapper.exe\nPostExitCommand=echo after\n");
        var service = new LaunchSetupService(app, () => false);
        service.Enable(game);
        Assert.IsTrue(service.Inspect(game).Healthy);
        var current = new UltimMcSettings(File.ReadAllText(cfg));
        Assert.AreEqual("wrapper.exe", current.Get("WrapperCommand"));
        Assert.AreEqual("echo after", current.Get("PostExitCommand"));
        Assert.IsTrue(current.Get("PreLaunchCommand")!.Contains("$INST_DIR/"));
        Assert.IsFalse(current.Get("PreLaunchCommand")!.Contains(root));
        Assert.AreEqual(1, Directory.GetFiles(app, "instance.cfg.backup", SearchOption.AllDirectories).Length);
        Assert.AreEqual(1, Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length, "Setup must not write game files.");
        File.AppendAllText(cfg, "LaterSetting=retained\n");
        service.Disable(game);
        current = new UltimMcSettings(File.ReadAllText(cfg));
        Assert.AreEqual("false", current.Get("OverrideCommands")); Assert.AreEqual("dormant", current.Get("PreLaunchCommand"));
        Assert.AreEqual("retained", current.Get("LaterSetting")); Assert.IsNull(current.Get("WrapperCommand"));
        Assert.IsFalse(service.Inspect(game).Installed);
    }
    [TestMethod] public void OpenLauncherPreventsWritesAndExternalCommandEditsAreNeverDiscarded()
    {
        string before = File.ReadAllText(cfg);
        Assert.ThrowsException<IOException>(() => new LaunchSetupService(app, () => true).Enable(game));
        Assert.AreEqual(before, File.ReadAllText(cfg));
        var service = new LaunchSetupService(app, () => false); service.Enable(game);
        File.WriteAllText(cfg, new UltimMcSettings(File.ReadAllText(cfg)).With(new Dictionary<string,string?> { ["PreLaunchCommand"] = "my-new-command.exe" }));
        Assert.IsFalse(service.Inspect(game).Healthy);
        Assert.ThrowsException<IOException>(() => service.Enable(game));
        Assert.ThrowsException<IOException>(() => service.Disable(game));
        Assert.AreEqual("my-new-command.exe", new UltimMcSettings(File.ReadAllText(cfg)).Get("PreLaunchCommand"));
    }
    [TestMethod] public void MissingScriptCanBeRepairedAndMovedLauncherKeepsHookIdentity()
    {
        var service = new LaunchSetupService(app, () => false); service.Enable(game);
        var script = Directory.GetFiles(app, "prelaunch.ps1", SearchOption.AllDirectories).Single(); File.Delete(script);
        Assert.IsFalse(service.Inspect(game).Healthy); service.Enable(game); Assert.IsTrue(service.Inspect(game).Healthy);
        string moved = root + "_moved";
        Directory.Move(root, moved);
        app = app.Replace(root, moved); game = game.Replace(root, moved); root = moved;
        Assert.IsTrue(new LaunchSetupService(app, () => false).Inspect(game).Healthy);
    }
    [TestMethod] public void FlatSettingsRoundTripEscapesAndPreserveUnknownLines()
    {
        string value = "C:\\folder # Ω\\file\tline\nend";
        var settings = new UltimMcSettings("# comment\nUnknown=left\nKey=old\nKey=last\n");
        Assert.AreEqual("last", settings.Get("Key"));
        var updated = settings.With(new Dictionary<string, string?> { ["Key"] = value });
        Assert.AreEqual(value, new UltimMcSettings(updated).Get("Key")); Assert.IsTrue(updated.StartsWith("# comment\nUnknown=left\n"));
    }
    [TestMethod] public void InstanceProfilesRestoreRepositoriesExclusionsAndRecoveryIdentity()
    {
        string other = Path.Combine(root, "Other"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "options.txt"), "");
        var config = new ConfigService(new LoggingService(), Path.Combine(app, "config.json"));
        Assert.IsTrue(config.SelectInstance(game)); config.Config.Repository = "https://example.test/one.git";
        config.Config.IgnoredMods.Add("personal.jar"); var key = config.InstanceStorageKey; config.Save();
        Assert.IsTrue(config.SelectInstance(other)); Assert.AreEqual(0, config.Config.IgnoredMods.Count);
        config.Config.Repository = "https://example.test/two.git"; config.Save();
        Assert.IsTrue(config.IsKnownInstance(game)); Assert.IsTrue(config.SelectInstance(game));
        Assert.AreEqual("https://example.test/one.git", config.Config.Repository); Assert.AreEqual("personal.jar", config.Config.IgnoredMods.Single());
        Assert.AreEqual(key, config.InstanceStorageKey);
        Assert.IsTrue(config.SelectInstance(other)); Assert.AreEqual("https://example.test/two.git", config.Config.Repository);
    }
    [TestMethod] public void MigrationCopiesRecoveryOnlyOnceAndLeavesOriginalsUntouched()
    {
        string old = Path.Combine(root, "old-data"); Directory.CreateDirectory(Path.Combine(old, "pending-updates"));
        string pending = Path.Combine(old, "pending-updates", "test.txt"); File.WriteAllText(pending, "pending");
        File.WriteAllText(Path.Combine(old, "config.json"), System.Text.Json.JsonSerializer.Serialize(new ModSync.Models.AppConfig { ModsFolder = Path.Combine(game, "mods") }));
        var previous = PathUtils.DataDirectory;
        try
        {
            AppStorage.Initialize(app, old);
            Assert.AreEqual(app, PathUtils.GetDataDirectory()); Assert.IsTrue(File.Exists(Path.Combine(app, "config.json")));
            File.Delete(Path.Combine(app, "pending-updates", "test.txt")); AppStorage.Initialize(app, old);
            Assert.IsFalse(File.Exists(Path.Combine(app, "pending-updates", "test.txt")), "Do not resurrect completed update markers.");
            Assert.AreEqual("pending", File.ReadAllText(pending));
            Assert.ThrowsException<IOException>(() => AppStorage.Initialize(game, old));
        }
        finally { PathUtils.DataDirectory = previous; }
    }
    [TestMethod] public void RepositoryHashCacheReusesOnlyMatchingCommitAndFileMetadata()
    {
        string file = Path.Combine(root, "repo", "mod.jar"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "first");
        int reads = 0; string Compute(string path) { reads++; return HashUtils.ComputeSha256(path); }
        var cacheFile = Path.Combine(app, "hashes.json");
        var cache = new RepositoryHashCache(cacheFile, [(Path.GetDirectoryName(file)!, "commit1")], Compute);
        var first = cache.Get(file); cache.Save();
        cache = new RepositoryHashCache(cacheFile, [(Path.GetDirectoryName(file)!, "commit1")], Compute);
        Assert.AreEqual(first, cache.Get(file)); Assert.AreEqual(1, reads);
        File.WriteAllText(file, "second content"); Assert.AreNotEqual(first, cache.Get(file)); Assert.AreEqual(2, reads);
        cache.Save(); cache = new RepositoryHashCache(cacheFile, [(Path.GetDirectoryName(file)!, "commit2")], Compute);
        cache.Get(file); Assert.AreEqual(3, reads);
    }

    [TestMethod] public async Task GeneratedScriptPreservesQuotedArgumentsAndStopsOnOriginalCommandFailure()
    {
        string prior = Path.Combine(root, "prior command.ps1"), output = Path.Combine(root, "arguments.json");
        File.WriteAllText(prior, "$args | ConvertTo-Json | Set-Content -LiteralPath $env:MODSYNC_TEST_OUTPUT; exit 7");
        string command = "powershell.exe -NoProfile -File \"" + prior + "\" \"space argument\" \"C:\\trailing\\\" \"$MODSYNC_TEST_VALUE\"";
        File.WriteAllText(Path.Combine(root, "ultimmc.cfg"), "PreLaunchCommand=" + UltimMcSettings.Encode(command) + "\n");
        new LaunchSetupService(app, () => false).Enable(game);
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = root };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Directory.GetFiles(app, "prelaunch.ps1", SearchOption.AllDirectories).Single() }) start.ArgumentList.Add(arg);
        start.Environment["INST_MC_DIR"] = game; start.Environment["MODSYNC_TEST_OUTPUT"] = output; start.Environment["MODSYNC_TEST_VALUE"] = "expanded value";
        using var process = System.Diagnostics.Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        string error = await errors; await stdout;
        Assert.AreEqual(1, process.ExitCode, error); Assert.IsTrue(error.Contains("failed with code 7"), error);
        var arguments = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(output))!;
        CollectionAssert.AreEqual(new[] { "space argument", "C:\\trailing\\", "expanded value" }, arguments);
    }

    [TestMethod] public void ImportMatchesOnlyTheSelectedLegacyInstance()
    {
        string old = Path.Combine(root, "legacy"), installation = Path.Combine(old, "one"); Directory.CreateDirectory(installation);
        var original = new ModSync.Models.AppConfig { ModsFolder = Path.Combine(game, "mods"), IgnoredMods = ["keep.jar"] };
        string serialized = System.Text.Json.JsonSerializer.Serialize(original); File.WriteAllText(Path.Combine(installation, "config.json"), serialized);
        var imported = AppStorage.ImportMatchingInstance(game, app, old);
        Assert.IsNotNull(imported); Assert.AreEqual("keep.jar", imported.IgnoredMods.Single());
        Assert.AreEqual(serialized, File.ReadAllText(Path.Combine(installation, "config.json")));
        Assert.IsNull(AppStorage.ImportMatchingInstance(Path.Combine(root, "other"), app, old));
        Directory.CreateDirectory(Path.Combine(old, "two")); File.WriteAllText(Path.Combine(old, "two", "config.json"), serialized);
        Assert.IsNull(AppStorage.ImportMatchingInstance(game, app, old), "Ambiguous legacy settings must not be selected silently.");
    }

    [TestMethod] public void SharedRepositoryAndBranchAreCheckedOnceButDifferentBranchesStaySeparate()
    {
        var config = new ConfigService(new LoggingService(), Path.Combine(app, "config.json"));
        config.Config.ResourcePackRepository = config.Config.ShaderPackRepository = config.Config.Repository;
        var selected = new RepositorySyncService(config, new GitService(new LoggingService())).Selected(ModSync.Models.SyncScope.All);
        Assert.AreEqual(1, selected.Count);
        config.Config.ShaderPackBranch = "shaders";
        Assert.AreEqual(2, new RepositorySyncService(config, new GitService(new LoggingService())).Selected(ModSync.Models.SyncScope.All).Count);
    }

    [TestMethod] public void UltimMcDiscoveryUsesConfiguredInstanceDirectoryAndDisplayNames()
    {
        string custom = Path.Combine(root, "custom instances", "Profile", ".minecraft"); Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, "options.txt"), "");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(custom)!, "instance.cfg"), "name=BigChadGuys Friendly Name\n");
        File.WriteAllText(Path.Combine(root, "ultimmc.cfg"), "InstanceDir=custom instances\n");
        var found = InstanceDiscoveryService.Discover(app, Path.Combine(root, "unselected"));
        Assert.IsTrue(found.Any(i => i.Folder == custom && i.Name == "BigChadGuys Friendly Name"));
    }
}
