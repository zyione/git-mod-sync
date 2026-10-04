using System.IO;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class BulkExclusionTests
{
    public static void WriteFabricMod(string path, string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var jar = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(jar.CreateEntry("fabric.mod.json").Open());
        writer.Write(System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, id, version = "1.0" }));
    }

    [TestMethod]
    public void IdentitySurvivesRenamesWithoutMatchingUnrelatedModsAndCanBeRemoved()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        try
        {
            var logger = new LoggingService();
            var config = new ConfigService(logger, Path.Combine(folder, "config.json"));
            config.Config.ModsFolder = folder;
            var service = new ModIgnoreService(config, logger);
            string old = Path.Combine(folder, "zoom-1.jar"), next = Path.Combine(folder, "totally-renamed-2.jar"), other = Path.Combine(folder, "zoom-extra-1.jar");
            WriteFabricMod(old, "zoom"); WriteFabricMod(next, "zoom"); WriteFabricMod(other, "zoom_extra");
            var result = service.ExcludeFiles(new[] { old, next }, rememberIdentity: true);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, result.Added);
            Assert.AreEqual(1, result.Skipped);
            Assert.IsTrue(service.IsIgnored(next));
            Assert.IsFalse(service.IsIgnored(other));
            StringAssert.Contains(File.ReadAllText(config.ConfigFilePath), "fabric-id:zoom");
            Assert.IsTrue(service.RemovePattern("fabric-id:zoom"));
            Assert.IsFalse(service.IsIgnored(next));
            File.WriteAllText(Path.Combine(folder, "unknown.jar"), "not a zip");
            var fallback = service.ExcludeFiles(new[] { Path.Combine(folder, "unknown.jar") }, true);
            Assert.AreEqual(1, fallback.FilenameOnly);
            Assert.IsTrue(service.IsIgnored("unknown.jar"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void BulkExclusionPersistsUniqueNamesAndLeavesFilesUntouched()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        try
        {
            var logger = new LoggingService();
            var config = new ConfigService(logger, Path.Combine(folder, "config.json"));
            config.Config.ModsFolder = folder;
            config.Config.IgnoredMods = new() { "*zoom*" };
            var service = new ModIgnoreService(config, logger);
            var paths = new[] { "first.jar", "second.JAR", "zoom.jar", "notes.txt" }
                .Select(name => Path.Combine(folder, name)).ToArray();
            foreach (string path in paths) File.WriteAllText(path, "unchanged");
            var result = service.ExcludeFiles(paths.Concat(new[] { paths[0], folder, Path.Combine(folder, "missing.jar") }));
            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.Added);
            Assert.AreEqual(5, result.Skipped);
            CollectionAssert.AreEqual(new[] { "*zoom*", "first.jar", "second.JAR" }, config.Config.IgnoredMods);
            StringAssert.Contains(File.ReadAllText(config.ConfigFilePath), "second.JAR");
            foreach (string path in paths) Assert.AreEqual("unchanged", File.ReadAllText(path));
            Assert.AreEqual(0, service.ExcludeFiles(paths).Added);
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void FailedSaveRestoresPreviousExclusions()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        try
        {
            var logger = new LoggingService();
            var config = new ConfigService(logger, Path.Combine(folder, "missing", "config.json"));
            config.Config.ModsFolder = folder;
            config.Config.IgnoredMods = new() { "existing.jar" };
            string mod = Path.Combine(folder, "new.jar");
            File.WriteAllText(mod, "untouched");
            var result = new ModIgnoreService(config, logger).ExcludeFiles(new[] { mod });
            Assert.IsFalse(result.Success);
            CollectionAssert.AreEqual(new[] { "existing.jar" }, config.Config.IgnoredMods);
            Assert.AreEqual("untouched", File.ReadAllText(mod));
        }
        finally { Directory.Delete(folder, true); }
    }
}
