using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class RelocationTests
{
    private string _root = null!;
    private RelocationLocation _location = null!;
    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ModSync_Relocation_" + Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(_root, "UltimMC", "instances", "BigChadGuysPlus");
        Directory.CreateDirectory(Path.Combine(instance, ".minecraft", "mods"));
        File.WriteAllText(Path.Combine(_root, "UltimMC", "UltimMC.exe"), "launcher fixture");
        File.WriteAllText(Path.Combine(instance, "instance.cfg"), "launch command fixture");
        string source = Path.Combine(instance, ".minecraft", "ModSync.exe");
        File.WriteAllText(source, "app fixture");
        File.WriteAllText(Path.Combine(instance, ".minecraft", "mods", "personal.jar"), "personal fixture");
        _location = AppRelocation.Detect(source)!;
    }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, recursive: true); }

    [TestMethod]
    public void DetectionIsReadOnlyAndRequiresRealLauncherStructure()
    {
        Assert.IsNotNull(_location);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(_location.Destination)));
        Assert.IsNull(AppRelocation.Detect(Path.Combine(_root, "UltimMC", "ModSync", "ModSync.exe")));
        File.Delete(Path.Combine(_location.Launcher, "UltimMC.exe"));
        Assert.IsNull(AppRelocation.Detect(_location.Source));
    }

    [TestMethod]
    public async Task ConfirmedMoveCreatesMissingFolderAndPreservesAllGameFiles()
    {
        var helper = AppRelocation.Prepare(_location, 0, restart: false);
        Assert.IsTrue(File.Exists(_location.Source), "Preparation must retain the original.");
        await Run(helper);
        Assert.IsTrue(Success(), File.ReadAllText(Path.Combine(Path.GetDirectoryName(_location.Destination)!, "relocation-result.json")));
        Assert.IsFalse(File.Exists(_location.Source));
        Assert.AreEqual("app fixture", File.ReadAllText(_location.Destination));
        Assert.AreEqual("launch command fixture", File.ReadAllText(Path.Combine(_location.Instance, "instance.cfg")));
        Assert.AreEqual("personal fixture", File.ReadAllText(Path.Combine(_location.Instance, ".minecraft", "mods", "personal.jar")));
    }

    [TestMethod]
    public async Task DestinationCreatedAfterConfirmationIsNeverOverwritten()
    {
        var helper = AppRelocation.Prepare(_location, 0, restart: false);
        File.WriteAllText(_location.Destination, "existing app");
        await Run(helper);
        Assert.IsFalse(Success());
        Assert.AreEqual("existing app", File.ReadAllText(_location.Destination));
        Assert.IsTrue(File.Exists(_location.Source));
        Assert.ThrowsException<IOException>(() => AppRelocation.Prepare(_location, 0, restart: false));
    }

    [TestMethod]
    public async Task CorruptCopyDoesNotRemoveOriginal()
    {
        var helper = AppRelocation.Prepare(_location, 0, restart: false);
        using var plan = JsonDocument.Parse(File.ReadAllText(helper.Instructions));
        File.WriteAllText(plan.RootElement.GetProperty("Staged").GetString()!, "corrupted");
        await Run(helper);
        Assert.IsFalse(Success());
        Assert.AreEqual("app fixture", File.ReadAllText(_location.Source));
        Assert.IsFalse(File.Exists(_location.Destination));
    }

    [TestMethod]
    public async Task SourceChangedDuringMoveIsKept()
    {
        var helper = AppRelocation.Prepare(_location, 0, restart: false);
        File.WriteAllText(_location.Source, "newer source");
        await Run(helper);
        Assert.IsFalse(Success());
        Assert.AreEqual("newer source", File.ReadAllText(_location.Source));
    }

    [TestMethod]
    public void DestinationInsideInstanceOrWithoutLauncherIsRejected()
    {
        Assert.ThrowsException<IOException>(() => AppRelocation.ValidateDestination(_location.Instance));
        Directory.CreateDirectory(Path.Combine(_location.Instance, "instances"));
        File.WriteAllText(Path.Combine(_location.Instance, "UltimMC.exe"), "fake nested launcher");
        Assert.ThrowsException<IOException>(() => AppRelocation.ValidateDestination(_location.Instance));
    }
    private bool Success() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(_location.Destination)!, "relocation-result.json"))).RootElement.GetProperty("Success").GetBoolean();
    private static async Task Run((string Script, string Instructions) helper)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper.Script, "-Instructions", helper.Instructions }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
    }
}
