using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class SyncCompletionTests
{
    [TestMethod]
    public void CompletionNamesOnlyAppliedChangesAndExplainsOrderAndRestart()
    {
        var summary = new SyncSummary();
        summary.Changes.Add(new ModChange { RelativePath = "resourcepacks/A.zip", Type = ChangeType.Added });
        summary.Changes.Add(new ModChange { RelativePath = "options.txt (resource pack order)", Type = ChangeType.Updated, NewContent = "order" });
        string text = SyncCompletionService.Describe(summary, SyncScope.ResourcePacks, true, true, true);
        StringAssert.Contains(text, "Resource packs updated.");
        StringAssert.Contains(text, "Repository pack order applied.");
        StringAssert.Contains(text, "Start Minecraft");
        Assert.IsFalse(text.Contains("Mods updated"));
        Assert.IsFalse(text.Contains("Shaders updated"));
        string unchanged = SyncCompletionService.Describe(new(), SyncScope.ResourcePackOrder, true, true, true);
        StringAssert.Contains(unchanged, "already matches");
        Assert.IsFalse(unchanged.Contains("Start Minecraft"));
        string noOrder = SyncCompletionService.Describe(new(), SyncScope.ResourcePacks, true, true, false);
        StringAssert.Contains(noOrder, "No repository pack order provided");
        Assert.IsFalse(noOrder.Contains("order applied"));
        string failedLoader = SyncCompletionService.Describe(new(), SyncScope.Mods, false, false, false, fabricFailed: true);
        StringAssert.Contains(failedLoader, "Fabric Loader needs attention");
        Assert.IsFalse(failedLoader.Contains("Already up to date"));
        Assert.IsFalse(failedLoader.Contains("Start Minecraft"));
    }
}
