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
            Write(".updates/" + new string('a', 64) + ".exe"), Write(".updates/" + new string('b', 64) + ".delta"),
            Write("content/" + new string('c', 64)), Write("content/" + new string('d', 64) + ".part"), Write("logs/modsync-old.log") };
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
    public void EveryLaunchRemovesReappearingArtifactsEvenWithMigrationMarker()
    {
        Write(StorageMaintenance.MigrationMarker);
        File.SetLastWriteTimeUtc(Path.Combine(root, StorageMaintenance.MigrationMarker), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string intentional = Write("backups/instance/reinstall_2026-10-20_120000_new/mods/keep.jar");
        for (int launch = 0; launch < 3; launch++)
        {
            var old = new[] { Write("ModSync.before-old-update.exe"), Write(".updates/" + new string('a', 64) + ".exe"),
                Write("content/" + new string('b', 64)), Write("backups/instance/reinstall_2019-01-01_000000_old/mods/old.jar") };
            StorageMaintenance.Run(root);
            Assert.IsTrue(old.All(file => !File.Exists(file)), "Each future launch must retry old artifacts, regardless of migration markers.");
            Assert.IsTrue(File.Exists(intentional), "Intentional backups newer than the migration cutoff are preserved.");
        }
    }

    [TestMethod]
    public void LockedOldApplicationFileIsRetriedOnLaterLaunch()
    {
        string old = Write("ModSync.before-locked.exe");
        var messages = new List<string>();
        using (var locked = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            StorageMaintenance.Run(root, messages.Add);
            Assert.IsTrue(File.Exists(old)); Assert.IsTrue(messages.Count > 0);
        }
        StorageMaintenance.Run(root);
        Assert.IsFalse(File.Exists(old));
    }

    [TestMethod]
    public void HealthyLaunchRemovesPreviousExecutablesAndAbandonedHelpersRepeatedly()
    {
        string current = Write("ModSync.exe"); string settings = Write("config.json");
        for (int launch = 0; launch < 3; launch++)
        {
            string id = Guid.NewGuid().ToString("N");
            var old = new[] { Write("ModSync.exe.previous"), Write("ModSync.update.exe"), Write("update-" + id + ".ps1"),
                Write("update-" + id + ".json"), Write("relocate-" + id + ".exe"), Write("update_modsync.bat") };
            foreach (string file in old.Skip(2)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
            StorageMaintenance.AfterHealthyStartup(root, current);
            Assert.IsTrue(old.All(file => !File.Exists(file)));
            Assert.IsTrue(File.Exists(current)); Assert.IsTrue(File.Exists(settings));
        }
    }

    [TestMethod]
    public void ActiveUpdateKeepsRollbackUntilHelperFinishesThenRetries()
    {
        string current = Write("ModSync.exe"), previous = Write("ModSync.exe.previous"), staged = Write("ModSync.update.exe");
        string id = Guid.NewGuid().ToString("N"); string instructions = Write("update-" + id + ".json");
        StorageMaintenance.AfterHealthyStartup(root, current);
        Assert.IsTrue(File.Exists(previous)); Assert.IsTrue(File.Exists(staged)); Assert.IsTrue(File.Exists(instructions));
        File.Delete(instructions);
        StorageMaintenance.AfterHealthyStartup(root, current);
        Assert.IsFalse(File.Exists(previous)); Assert.IsFalse(File.Exists(staged)); Assert.IsTrue(File.Exists(current));
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
