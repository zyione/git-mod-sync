using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class GitScopeTests
{
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
