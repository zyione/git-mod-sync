using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class IgnoreAndSyncTests
{
    [TestMethod]
    public void TestExactFilenameMatching()
    {
        var patterns = new List<string> { "optifine.jar", "jei-1.20.1.jar" };

        Assert.IsTrue(ModIgnoreService.IsIgnored("optifine.jar", patterns));
        Assert.IsTrue(ModIgnoreService.IsIgnored("OPTIFINE.JAR", patterns)); // Case-insensitive
        Assert.IsTrue(ModIgnoreService.IsIgnored("jei-1.20.1.jar", patterns));
        Assert.IsFalse(ModIgnoreService.IsIgnored("sodium.jar", patterns));
    }

    [TestMethod]
    public void TestWildcardPatternMatching()
    {
        var patterns = new List<string> { "*zoom*", "iris-*.jar", "replaymod*" };

        // *zoom*
        Assert.IsTrue(ModIgnoreService.IsIgnored("ok-zoomer-1.20.1.jar", patterns));
        Assert.IsTrue(ModIgnoreService.IsIgnored("zoom.jar", patterns));
        Assert.IsFalse(ModIgnoreService.IsIgnored("sodium.jar", patterns));

        // iris-*.jar
        Assert.IsTrue(ModIgnoreService.IsIgnored("iris-mc1.20.1-1.7.0.jar", patterns));
        Assert.IsFalse(ModIgnoreService.IsIgnored("iris.txt", patterns));

        // replaymod*
        Assert.IsTrue(ModIgnoreService.IsIgnored("replaymod-1.20.1.jar", patterns));
        Assert.IsTrue(ModIgnoreService.IsIgnored("replaymod.jar", patterns));
    }

    [TestMethod]
    public void TestPatternWithoutExtension()
    {
        var patterns = new List<string> { "optifine", "xaeros_minimap" };

        // Should match exact with .jar
        Assert.IsTrue(ModIgnoreService.IsIgnored("optifine.jar", patterns));
        Assert.IsTrue(ModIgnoreService.IsIgnored("OPTIFINE.jar", patterns));
        // Should match versioned prefix like optifine-1.20.jar or xaeros_minimap_23.0.jar
        Assert.IsTrue(ModIgnoreService.IsIgnored("optifine-1.20.1.jar", patterns));
        Assert.IsTrue(ModIgnoreService.IsIgnored("xaeros_minimap_23.9.7.jar", patterns));
        Assert.IsFalse(ModIgnoreService.IsIgnored("xaeros_worldmap.jar", patterns));
    }

    [TestMethod]
    public void TestCommentsAndWhitespaceIgnored()
    {
        var patterns = new List<string> { "# This is a comment", "   ", "// Another comment", "valid-mod.jar" };

        Assert.IsFalse(ModIgnoreService.IsIgnored("# This is a comment", patterns));
        Assert.IsTrue(ModIgnoreService.IsIgnored("valid-mod.jar", patterns));
    }

    [TestMethod]
    public void TestSyncSummaryActionableChangesWithIgnored()
    {
        var summary = new SyncSummary();
        summary.Changes.Add(new ModChange { RelativePath = "added-mod.jar", Type = ChangeType.Added });
        summary.Changes.Add(new ModChange { RelativePath = "ignored-personal-mod.jar", Type = ChangeType.Ignored });

        Assert.AreEqual(1, summary.AddedCount);
        Assert.AreEqual(1, summary.IgnoredCount);
        Assert.AreEqual(1, summary.TotalActionableChanges);
        Assert.IsTrue(summary.HasChanges);

        // When only ignored mods exist
        var onlyIgnoredSummary = new SyncSummary();
        onlyIgnoredSummary.Changes.Add(new ModChange { RelativePath = "my-hud.jar", Type = ChangeType.Ignored });
        onlyIgnoredSummary.Changes.Add(new ModChange { RelativePath = "unwanted-mod.jar", Type = ChangeType.Ignored });

        Assert.AreEqual(0, onlyIgnoredSummary.AddedCount);
        Assert.AreEqual(0, onlyIgnoredSummary.RemovedCount);
        Assert.AreEqual(0, onlyIgnoredSummary.UpdatedCount);
        Assert.AreEqual(2, onlyIgnoredSummary.IgnoredCount);
        Assert.AreEqual(0, onlyIgnoredSummary.TotalActionableChanges);
        Assert.IsFalse(onlyIgnoredSummary.HasChanges, "Summary should NOT report actionable changes when only ignored mods exist.");
    }
}
