using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class GitScopeTests
{
    [TestMethod]
    public async Task UnchangedRepositoryDoesNotFetchAndChangedRepositoryRefreshesOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "ModSync_fast_git_" + Guid.NewGuid().ToString("N"));
        string remote = Path.Combine(root, "remote"), cache = Path.Combine(root, "cache"); Directory.CreateDirectory(remote);
        try
        {
            Git(remote, "init", "-b", "main"); Git(remote, "config", "user.name", "Tests"); Git(remote, "config", "user.email", "tests@example.test");
            File.WriteAllText(Path.Combine(remote, "mod.jar"), "version one"); Git(remote, "add", "."); Git(remote, "commit", "-m", "Initial");
            var service = new GitService(new LoggingService());
            var first = await service.CheckRepositoryAsync(cache, remote, "main", true); Assert.IsFalse(first.HasUpdates);
            string fetch = Path.Combine(cache, ".git", "FETCH_HEAD"); File.WriteAllText(fetch, "untouched sentinel");
            var warm = await service.CheckRepositoryAsync(cache, remote, "main", true);
            Assert.IsFalse(warm.HasUpdates); Assert.AreEqual("untouched sentinel", File.ReadAllText(fetch), "Unchanged checks must not fetch.");
            File.WriteAllText(Path.Combine(cache, "mod.jar"), "damaged");
            await service.CheckRepositoryAsync(cache, remote, "main", true);
            Assert.AreEqual("version one", File.ReadAllText(Path.Combine(cache, "mod.jar"))); Assert.AreEqual("untouched sentinel", File.ReadAllText(fetch));
            File.WriteAllText(Path.Combine(remote, "mod.jar"), "version two"); Git(remote, "add", "."); Git(remote, "commit", "-m", "Second");
            var preview = await service.CheckRepositoryAsync(cache, remote, "main", false);
            Assert.IsTrue(preview.HasUpdates); Assert.AreEqual("untouched sentinel", File.ReadAllText(fetch));
            var refreshed = await service.CheckRepositoryAsync(cache, remote, "main", true);
            Assert.IsFalse(refreshed.HasUpdates); Assert.AreEqual("version two", File.ReadAllText(Path.Combine(cache, "mod.jar")));
            File.WriteAllText(fetch, "after update"); await service.CheckRepositoryAsync(cache, remote, "main", true);
            Assert.AreEqual("after update", File.ReadAllText(fetch));
            await Assert.ThrowsExceptionAsync<IOException>(() => service.CheckRepositoryAsync(cache, remote, "missing", true));
            await Assert.ThrowsExceptionAsync<IOException>(() => service.CheckRepositoryAsync(cache, Path.Combine(root, "offline"), "main", true));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
    private static string Git(string folder, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, error);
        return output.Trim();
    }

    [TestMethod]
    public async Task ScopedCommitIncludesAddUpdateDeleteAndLeavesUnrelatedStagedChanges()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ModSync_GitScope_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Git(folder, "init"); Git(folder, "config", "user.name", "Tests"); Git(folder, "config", "user.email", "tests@example.test");
            Directory.CreateDirectory(Path.Combine(folder, "resourcepacks"));
            foreach (var name in new[] { "Updated.zip", "Removed.zip" }) File.WriteAllText(Path.Combine(folder, "resourcepacks", name), "old");
            File.WriteAllText(Path.Combine(folder, "Unrelated.txt"), "old");
            Git(folder, "add", "-A"); Git(folder, "commit", "-m", "Initial");
            File.WriteAllText(Path.Combine(folder, "resourcepacks", "Updated.zip"), "new");
            File.WriteAllText(Path.Combine(folder, "resourcepacks", "New [pack].zip"), "new");
            File.Delete(Path.Combine(folder, "resourcepacks", "Removed.zip"));
            File.WriteAllText(Path.Combine(folder, "Unrelated.txt"), "new");
            Git(folder, "add", "Unrelated.txt");
            var result = await new GitService(new LoggingService()).StageAndCommitAsync(folder, "Packs only", paths: new[] { "resourcepacks/Updated.zip", "resourcepacks/Removed.zip", "resourcepacks/New [pack].zip" });
            Assert.IsTrue(result.Success, result.Error);
            string committed = Git(folder, "show", "--format=", "--name-only", "HEAD");
            Assert.IsTrue(committed.Contains("New [pack].zip") && committed.Contains("Updated.zip") && committed.Contains("Removed.zip"));
            Assert.IsFalse(committed.Contains("Unrelated.txt"));
            Assert.AreEqual("old", Git(folder, "show", "HEAD:Unrelated.txt"));
            Assert.AreEqual("M  Unrelated.txt", Git(folder, "status", "--porcelain"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, true);
        }
    }
}
