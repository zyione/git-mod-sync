using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class LaunchBridgeTests
{
    [TestMethod]
    public async Task BrokerWaitsForDecisionAndReturnsWithoutClosingDashboard()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = LaunchBridge.ListenAsync((instance, _) => { entered.TrySetResult(instance); return decision.Task; }, _ => { }, stop.Token);
        try
        {
            string instance = Path.Combine(Path.GetTempPath(), "ModSync test instance");
            var broker = LaunchBridge.WaitForDecisionAsync(instance, startIfMissing: false);
            Assert.AreEqual(Path.GetFullPath(instance), await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(broker.IsCompleted, "Minecraft must wait for the player's decision.");
            decision.SetResult(true);
            Assert.AreEqual(0, await broker.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(listener.IsCompleted, "The dashboard must be able to stay open after releasing Minecraft.");
        }
        finally { stop.Cancel(); await listener; }
    }

    [TestMethod]
    public async Task CancellationReturnsNonzeroAndNeverStartsMinecraft()
    {
        using var stop = new CancellationTokenSource();
        var listener = LaunchBridge.ListenAsync((_, _) => Task.FromResult(false), _ => { }, stop.Token);
        try { Assert.AreEqual(1, await LaunchBridge.WaitForDecisionAsync(Path.GetTempPath(), startIfMissing: false).WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { stop.Cancel(); await listener; }
    }
}
