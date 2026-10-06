using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class LaunchReadinessTests
{
    [TestMethod]
    public void UnknownOrFailedLoaderNeverCountsAsFullySynced()
    {
        var content = new SyncSummary();
        Assert.IsFalse(LaunchReadiness.IsReady(content, true, null));
        Assert.IsFalse(LaunchReadiness.IsReady(content, true, new FabricStatusInfo()));
        Assert.IsFalse(LaunchReadiness.IsReady(content, true, new() { IsConfigured = true, IsUpToDate = true, ErrorMessage = "Unreadable profile" }));
        Assert.IsTrue(LaunchReadiness.IsReady(content, false, null));
        Assert.IsTrue(LaunchReadiness.IsReady(content, true, new() { IsConfigured = true, IsUpToDate = true }));
        content.Changes.Add(new() { Type = ChangeType.Updated, IsInternal = true });
        Assert.IsFalse(LaunchReadiness.IsReady(content, false, null));
    }

    [TestMethod]
    public void ExcludedModsDoNotPreventReadiness()
    {
        var content = new SyncSummary();
        content.Changes.Add(new() { Type = ChangeType.Ignored, RelativePath = "Personal.jar" });
        Assert.IsTrue(LaunchReadiness.IsReady(content, false, null));
        content.Changes.Add(new() { Type = ChangeType.Removed, RelativePath = "Unexcluded.jar" });
        Assert.IsFalse(LaunchReadiness.IsReady(content, false, null));
    }

    [TestMethod]
    public void PersonalReminderIsPerInstanceAndReturnsForNewMods()
    {
        var config = new AppConfig();
        Assert.IsTrue(LaunchReadiness.NeedsPersonalReminder(config, "one", Array.Empty<string>()));
        config.PersonalModsAcknowledged["one"] = new() { "known.jar" };
        Assert.IsFalse(LaunchReadiness.NeedsPersonalReminder(config, "one", new[] { "KNOWN.jar" }));
        Assert.IsTrue(LaunchReadiness.NeedsPersonalReminder(config, "one", new[] { "known.jar", "new.jar" }));
        Assert.IsTrue(LaunchReadiness.NeedsPersonalReminder(config, "two", new[] { "known.jar" }));
    }
}
