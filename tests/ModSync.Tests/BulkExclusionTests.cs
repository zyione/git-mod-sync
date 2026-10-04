using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class BulkExclusionTests
{
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
