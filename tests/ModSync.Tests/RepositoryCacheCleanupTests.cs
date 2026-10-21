using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class RepositoryCacheCleanupTests
{
    private string root = null!, repo = null!;
    private ConfigService config = null!;
    private GitService git = null!;
    private static string Run(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, error); return output.Trim();
    }
    [TestInitialize] public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "ModSync_cache_cleanup_" + Guid.NewGuid().ToString("N"));
        repo = Path.Combine(root, "repository"); Directory.CreateDirectory(Path.Combine(repo, "mods"));
        var logger = new LoggingService(); config = new ConfigService(logger, Path.Combine(root, "config.json")); git = new(logger);
        config.Config.RepositoryFolder = repo; config.Config.ModsFolder = Path.Combine(root, "game", "mods");
        config.Config.ResourcePackRepository = config.Config.ShaderPackRepository = "";
        Run(repo, "init", "-b", "main"); Run(repo, "config", "user.name", "Tests"); Run(repo, "config", "user.email", "tests@example.test");
        File.WriteAllText(Path.Combine(repo, "mods", "shared.jar"), "published");
        Run(repo, "add", "."); Run(repo, "commit", "-m", "Published");
        Run(repo, "update-ref", "refs/remotes/origin/main", "HEAD");
    }
    [TestCleanup] public void Cleanup()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
    [TestMethod] public async Task RemovesCleanCacheWithModsSubfolderWithoutTouchingInstalledCopy()
    {
        Directory.CreateDirectory(config.ResolvedModsFolder); string installed = Path.Combine(config.ResolvedModsFolder, "shared.jar"); File.WriteAllText(installed, "installed");
        Assert.IsTrue(await git.CanDiscardCacheAsync(repo));
        await new RepositoryCacheCleanup(config, git).ReleaseAsync();
        Assert.IsFalse(Directory.Exists(repo)); Assert.AreEqual("installed", File.ReadAllText(installed));
    }
    [TestMethod] public async Task RetentionSurvivesReloadAndDisablingRestoresCleanup()
    {
        Assert.IsFalse(config.Config.KeepRepositoryCopies);
        config.Config.KeepRepositoryCopies = true;
        config.Save();
        var reloaded = new ConfigService(new LoggingService(), config.ConfigFilePath);
        Assert.IsTrue(reloaded.Load().Success);
        Assert.IsTrue(reloaded.Config.KeepRepositoryCopies);
        await new RepositoryCacheCleanup(reloaded, git).ReleaseAsync();
        Assert.IsTrue(Directory.Exists(repo));
        reloaded.Config.KeepRepositoryCopies = false;
        await new RepositoryCacheCleanup(reloaded, git).ReleaseAsync();
        Assert.IsFalse(Directory.Exists(repo));
    }
    [TestMethod] public async Task NotificationDiffIncludesPendingCommitsAndHandlesSpacesAndUnicode()
    {
        File.WriteAllText(Path.Combine(repo, "mods", "shared.jar"), "updated");
        File.WriteAllText(Path.Combine(repo, "mods", "New Pack 日本.jar"), "added");
        Run(repo, "add", "."); Run(repo, "commit", "-m", "Pending upload");
        var changes = (await git.GetUnpushedChangesAsync(repo, "main"))!;
        Assert.AreEqual(2, changes.Count);
        Assert.IsTrue(changes.Any(c => c.Path == "mods/shared.jar" && c.Type == ModSync.Models.ChangeType.Updated));
        Assert.IsTrue(changes.Any(c => c.Path == "mods/New Pack 日本.jar" && c.Type == ModSync.Models.ChangeType.Added));
        Run(repo, "update-ref", "refs/remotes/origin/main", "HEAD");
        Assert.AreEqual(0, (await git.GetUnpushedChangesAsync(repo, "main"))!.Count);
    }
    [TestMethod] public async Task DirtyAndUntrackedFilesArePreserved()
    {
        File.WriteAllText(Path.Combine(repo, "mods", "shared.jar"), "unpublished edit");
        Assert.IsFalse(await git.CanDiscardCacheAsync(repo));
        await new RepositoryCacheCleanup(config, git).ReleaseAsync(); Assert.IsTrue(Directory.Exists(repo));
        Run(repo, "reset", "--hard", "HEAD"); File.WriteAllText(Path.Combine(repo, "personal.jar"), "personal");
        Assert.IsFalse(await git.CanDiscardCacheAsync(repo));
    }
    [TestMethod] public async Task UnpushedCommitsAndOtherLocalBranchesArePreserved()
    {
        Run(repo, "checkout", "-b", "pending"); File.WriteAllText(Path.Combine(repo, "mods", "shared.jar"), "unpublished");
        Run(repo, "add", "."); Run(repo, "commit", "-m", "Unpublished"); Run(repo, "checkout", "main");
        Assert.IsFalse(await git.CanDiscardCacheAsync(repo));
        await new RepositoryCacheCleanup(config, git).ReleaseAsync(); Assert.IsTrue(Directory.Exists(repo));
    }
    [TestMethod] public async Task CustomCacheOutsidePortableAppIsPreserved()
    {
        string separate = Path.Combine(root, "separate-app"); Directory.CreateDirectory(separate);
        var outside = new ConfigService(new LoggingService(), Path.Combine(separate, "config.json"));
        outside.Config.RepositoryFolder = repo;
        await new RepositoryCacheCleanup(outside, git).ReleaseAsync(); Assert.IsTrue(Directory.Exists(repo));
    }
    [TestMethod] public async Task MisconfiguredCacheOverlappingInstalledContentIsNeverDeleted()
    {
        config.Config.ModsFolder = Path.Combine(repo, "mods");
        await new RepositoryCacheCleanup(config, git).ReleaseAsync();
        Assert.IsTrue(File.Exists(Path.Combine(repo, "mods", "shared.jar")));
    }
}
