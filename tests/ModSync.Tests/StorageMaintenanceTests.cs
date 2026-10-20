using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class StorageMaintenanceTests
{
    private string root = null!;
    [TestInitialize] public void Setup() => root = Path.Combine(Path.GetTempPath(), "ModSync_storage_" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private string Write(string name)
    {
        string file = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "fixture"); return file;
    }

    [TestMethod]
    public void UpgradeRemovesOldArtifactsOnceAndKeepsSettingsRepositoriesHooksAndPartialDownloads()
    {
        var deleted = new[] { Write("backups/instance/reinstall_old/mods/old.jar"), Write("ModSync.before-relocation.20261006.exe"),
            Write(".updates/" + new string('a', 64) + ".exe"), Write(".updates/" + new string('b', 64) + ".delta"), Write("logs/modsync-old.log") };
        File.SetLastWriteTimeUtc(deleted[^1], DateTime.UtcNow.AddDays(-30));
        var kept = new[] { Write("config.json"), Write("ModSync.exe"), Write("ModSync.exe.previous"), Write("repository/mod.jar"),
            Write("launch-hooks/instance/prelaunch.ps1"), Write("pending-updates/instance.txt"), Write(".updates/current.part"), Write("logs/modsync-today.log") };
        StorageMaintenance.Run(root);
        Assert.IsTrue(deleted.All(file => !File.Exists(file)));
        Assert.IsTrue(kept.All(File.Exists));
        string optional = Write("backups/instance/reinstall_new/mods/new.jar");
        StorageMaintenance.Run(root);
        Assert.IsTrue(File.Exists(optional), "Future optional backups survive routine cleanup.");
    }

    [TestMethod]
    public void CleanupRejectsPathsOutsideRootAndDoesNotWriteInMinecraftFolders()
    {
        Write("options.txt"); string backup = Write("backups/keep.jar");
        StorageMaintenance.Run(root);
        Assert.IsTrue(File.Exists(backup));
        Assert.IsFalse(File.Exists(Path.Combine(root, StorageMaintenance.MigrationMarker)));
        Assert.ThrowsException<IOException>(() => StorageMaintenance.DeleteTree(root, Path.GetDirectoryName(root)!));
    }

    [TestMethod]
    public void JunctionsAreNotTraversedAndFailedCleanupRetries()
    {
        string outside = Write("external/keep.jar");
        string backups = Path.Combine(root, "backups"); Directory.CreateDirectory(backups);
        string link = Path.Combine(backups, "linked");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") {
            Arguments = $"/c mklink /J \"{link}\" \"{Path.GetDirectoryName(outside)}\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(); Assert.AreEqual(0, process.ExitCode);
        try
        {
            StorageMaintenance.Run(root);
            Assert.IsTrue(File.Exists(outside));
            Assert.IsFalse(File.Exists(Path.Combine(root, StorageMaintenance.MigrationMarker)));
        }
        finally { Directory.Delete(link); }
        StorageMaintenance.Run(root);
        Assert.IsTrue(File.Exists(outside));
        Assert.IsTrue(File.Exists(Path.Combine(root, StorageMaintenance.MigrationMarker)));
    }
}
