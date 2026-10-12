using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class AppUpdatePriorityTests
{
    [TestMethod]
    public async Task ConcurrentContentWaitsForOneAppCheck()
    {
        var release = new TaskCompletionSource<UpdateInfo>();
        int calls = 0;
        var priority = new AppUpdatePriority(force => { calls++; Assert.IsTrue(force); return release.Task; });
        var first = priority.CheckAsync(force: true);
        var second = priority.CheckAsync();
        Assert.AreSame(first, second);
        Assert.IsFalse(second.IsCompleted, "Content must wait for the app check.");
        Assert.AreEqual(1, calls);
        var update = new UpdateInfo { IsUpdateAvailable = true, LatestVersion = "9.0.0" };
        release.SetResult(update);
        await Task.WhenAll(first, second);
        Assert.AreSame(update, priority.AvailableUpdate);
    }

    [TestMethod]
    public async Task FailedRecheckRetainsKnownAppUpdate()
    {
        var update = new UpdateInfo { IsUpdateAvailable = true, LatestVersion = "9.0.0" };
        var result = update;
        var priority = new AppUpdatePriority(_ => Task.FromResult(result));
        await priority.CheckAsync();
        result = new UpdateInfo { ErrorMessage = "Offline" };
        await priority.CheckAsync(force: true);
        Assert.AreSame(update, priority.AvailableUpdate, "An offline recheck must not unlock content.");
        result = new UpdateInfo();
        await priority.CheckAsync(force: true);
        Assert.IsNull(priority.AvailableUpdate, "A successful current-version check can clear the gate.");
    }

    [TestMethod]
    public async Task OfflineWithoutKnownUpdateDoesNotBlockContent()
    {
        int calls = 0;
        var priority = new AppUpdatePriority(_ => { calls++; return Task.FromResult(new UpdateInfo { ErrorMessage = "Offline" }); });
        await priority.CheckAsync();
        Assert.IsNull(priority.AvailableUpdate);
        await priority.CheckAsync();
        Assert.AreEqual(1, calls, "Startup and content must not repeat a failed network check immediately.");
        await priority.CheckAsync(force: true);
        Assert.AreEqual(2, calls, "An explicit retry must contact the server immediately.");
    }
}
