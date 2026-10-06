using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;
using ModSync.Utils;

namespace ModSync.Tests;

[TestClass]
public class ManifestTests
{
    private string root = null!;
    private ConfigService config = null!;
    private ModIgnoreService ignores = null!;
    private LoggingService logger = null!;
    private const string Commit = "1111111111111111111111111111111111111111";
    private static string Hash(string content) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    private PackManifest Manifest(string content = "remote") => new() {
        Repositories = [new("mods", config.Config.Repository, "main", Commit)],
        Files = [new("mods", "shared.jar", "mods/shared.jar", Encoding.UTF8.GetByteCount(content), Hash(content))] };
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => new(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!.ToString()); return Task.FromResult(Reply(request)); }
    }
    private PackManifestService Service(Handler? handler = null) => new(config, ignores, new HttpClient(handler ?? new Handler()), () => null);
    private string Local(string name, string text)
    {
        string file = Path.Combine(config.ResolvedModsFolder, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); return file;
    }
    [TestInitialize] public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "ModSync_manifest_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        logger = new LoggingService(); config = new(logger, Path.Combine(root, "config.json"));
        config.Config.ModsFolder = Path.Combine(root, "game", "mods"); Directory.CreateDirectory(config.ResolvedModsFolder);
        config.Config.SyncResourcePacks = config.Config.SyncShaderPacks = config.Config.SyncFabricLoader = false;
        config.Config.RepositoryFolder = Path.Combine(root, "repo"); config.Config.InstanceStorageId = "test-instance";
        ignores = new(config, logger);
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(root, true);

    [TestMethod] public async Task CheckingOnlyRequestsManifestEvenWithNoRepositoryCache()
    {
        var manifest = Manifest(); var handler = new Handler { Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(manifest)) } };
        var service = Service(handler); var fetched = await service.FetchAsync(); var review = service.Plan(fetched);
        Assert.AreEqual(1, handler.Requests.Count); Assert.IsTrue(handler.Requests[0].Contains(PackManifestService.ManifestName));
        Assert.AreEqual(1, review.Summary.AddedCount); Assert.IsFalse(Directory.Exists(config.ResolvedRepositoryFolder));
        Assert.IsFalse(File.Exists(Path.Combine(config.ResolvedModsFolder, "shared.jar")));
    }
    [TestMethod] public async Task MissingManifestDoesNotFallbackToContentDownloads()
    {
        var handler = new Handler(); await Assert.ThrowsExceptionAsync<IOException>(() => Service(handler).FetchAsync()); Assert.AreEqual(1, handler.Requests.Count);
    }
    [TestMethod] public void UnchangedMetadataReusesHashButChangedExpectationAndFullVerificationReadAgain()
    {
        string file = Local("shared.jar", "one"); int reads = 0;
        var cache = new LocalFingerprintCache(Path.Combine(root, "hashes.json"), path => { reads++; return HashUtils.ComputeSha256(path); });
        cache.Get(file, Hash("one"), false); cache.Get(file, Hash("one"), false); Assert.AreEqual(1, reads);
        cache.Get(file, Hash("two"), false); Assert.AreEqual(2, reads);
        cache.Get(file, Hash("two"), true); Assert.AreEqual(3, reads);
        var timestamp = File.GetLastWriteTimeUtc(file); File.WriteAllText(file, "two"); File.SetLastWriteTimeUtc(file, timestamp);
        Assert.AreEqual(Hash("one"), cache.Get(file, Hash("two"), false));
        Assert.AreEqual(Hash("two"), cache.Get(file, Hash("two"), true));
    }
    [TestMethod] public void SameFilenameWithNewPublishedHashIsAnUpdate()
    {
        Local("shared.jar", "one"); var service = Service(); Assert.IsFalse(service.Plan(Manifest("one")).Summary.HasChanges);
        Assert.AreEqual(1, service.Plan(Manifest("two")).Summary.UpdatedCount);
    }
    [TestMethod] public void UnknownPersonalFilesAreKeptAndExcludedFilesNeverDownload()
    {
        Local("personal.jar", "personal"); config.Config.IgnoredMods.Add("shared.jar");
        var plan = Service().Plan(Manifest()); Assert.AreEqual(0, plan.Summary.RemovedCount); Assert.AreEqual(1, plan.Summary.IgnoredCount);
        Assert.AreEqual(0L, Service().DownloadBytes(plan));
    }
    [TestMethod] public void FabricIdentityExclusionsWorkBeforeAnyContentIsDownloaded()
    {
        var manifest = Manifest(); manifest.Files[0] = manifest.Files[0] with { FabricId = "personal_mod" };
        config.Config.IgnoredMods.Add("fabric-id:personal_mod"); Assert.AreEqual(1, Service().Plan(manifest).Summary.IgnoredCount);
    }
    [TestMethod] public async Task ApprovedDownloadUsesPinnedCommitAndDoesNotTouchGameUntilApply()
    {
        string local = Local("shared.jar", "before"); var manifest = Manifest();
        var handler = new Handler { Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent("remote") } };
        var service = Service(handler); var plan = service.Plan(manifest);
        await service.DownloadAsync(plan, null, default);
        Assert.IsTrue(handler.Requests.Single().Contains("/" + Commit + "/")); Assert.AreEqual("before", File.ReadAllText(local));
        service.BeginApply(plan); Assert.IsTrue(service.HasPendingUpdate); Assert.IsNull(service.State.Version);
        var engine = new ModSyncService(config, new GitService(logger), new AuthenticationService(logger), new MinecraftCheckService(logger), ignores, logger, minecraftStatus: () => (false, null));
        var result = await engine.ApplyManifestFilesAsync(plan.Summary); Assert.IsTrue(result.Success, result.Error);
        service.MarkVerified(manifest); Assert.AreEqual("1.1", service.State.Version); Assert.IsFalse(service.HasPendingUpdate);
        Assert.AreEqual("remote", File.ReadAllText(local));
        Assert.IsTrue(Directory.GetFiles(config.BackupFolder, "shared.jar", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "before"));
    }
    [TestMethod] public async Task CorruptDownloadCannotBeginInstallation()
    {
        Local("shared.jar", "before"); var handler = new Handler { Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent("broken") } };
        var service = Service(handler); var review = service.Plan(Manifest());
        await Assert.ThrowsExceptionAsync<IOException>(() => service.DownloadAsync(review, null, default));
        Assert.AreEqual("before", File.ReadAllText(Path.Combine(config.ResolvedModsFolder, "shared.jar"))); Assert.IsFalse(service.HasPendingUpdate);
    }
    [TestMethod] public async Task CancelledDownloadLeavesInstallationUnchanged()
    {
        Local("shared.jar", "before"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var service = Service(); await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => service.DownloadAsync(service.Plan(Manifest()), null, cancellation.Token));
        Assert.IsFalse(service.HasPendingUpdate);
    }
    [TestMethod] public void ModifiedLocalFileAfterReviewRequiresNewApproval()
    {
        string file = Local("shared.jar", "before"); var service = Service(); var review = service.Plan(Manifest()); File.WriteAllText(file, "changed later");
        Assert.ThrowsException<IOException>(() => service.BeginApply(review)); Assert.IsFalse(service.HasPendingUpdate);
    }
    [TestMethod] public void OnlyPreviouslyManagedFilesAreRemoved()
    {
        Local("shared.jar", "remote"); Local("personal.jar", "personal"); var service = Service(); service.MarkVerified(Manifest());
        var next = Manifest(); next.Version = "1.2"; next.Files.Clear(); var changes = service.Plan(next).Summary;
        Assert.AreEqual(1, changes.RemovedCount); Assert.AreEqual("shared.jar", changes.Removed.Single().RelativePath);
        config.Config.IgnoredMods.Add("shared.jar"); Assert.AreEqual(0, service.Plan(next).Summary.RemovedCount);
    }
    [TestMethod] public void VersionIsNotMarkedVerifiedWhenFilesAreMissing()
    { var service = Service(); Assert.ThrowsException<IOException>(() => service.MarkVerified(Manifest())); Assert.IsNull(service.State.Version); }

    [DataTestMethod]
    [DataRow("../outside.jar")][DataRow("C:/outside.jar")][DataRow("a\\b.jar")][DataRow("CON.jar")][DataRow("a/../b.jar")]
    public void UnsafeManifestPathsAreRejected(string path)
    { var manifest = Manifest(); manifest.Files[0] = manifest.Files[0] with { Path = path }; Assert.ThrowsException<IOException>(() => Service().Plan(manifest)); }

    [TestMethod] public void DuplicateCaseAndRepositoryMismatchAreRejected()
    {
        var manifest = Manifest(); manifest.Files.Add(manifest.Files[0] with { Path = "SHARED.jar" }); Assert.ThrowsException<IOException>(() => Service().Validate(manifest));
        manifest = Manifest(); manifest.Repositories[0] = manifest.Repositories[0] with { Url = "https://github.com/other/repo" }; Assert.ThrowsException<IOException>(() => Service().Validate(manifest));
    }
    [TestMethod] public void PackVersionsAreIntegersAndNeverAutomaticallyAdvanceMajor()
    {
        Assert.AreEqual("1.1", PackVersion.Next(null)); Assert.AreEqual("1.12", PackVersion.Next("1.11")); Assert.AreEqual("1.100", PackVersion.Next("1.99"));
        Assert.AreEqual("1.1000", PackVersion.Next("1.999")); Assert.AreEqual("2.2", PackVersion.Next("2.1"));
    }

    [TestMethod] public async Task InterruptedApplyRetainsOwnershipAndCanBeRecheckedAndCompleted()
    {
        var handler = new Handler { Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent("remote") } };
        var service = Service(handler); var manifest = Manifest(); var review = service.Plan(manifest);
        await service.DownloadAsync(review, null, default); service.BeginApply(review);
        Assert.IsTrue(service.HasPendingUpdate); Assert.IsNull(service.State.Version);
        var resumed = Service(handler); var fresh = resumed.Plan(manifest, full: true);
        Assert.AreEqual(1, fresh.Summary.AddedCount); Assert.AreEqual(0L, resumed.DownloadBytes(fresh));
        var engine = new ModSyncService(config, new GitService(logger), new AuthenticationService(logger), new MinecraftCheckService(logger), ignores, logger, minecraftStatus: () => (false, null));
        resumed.BeginApply(fresh); Assert.IsTrue((await engine.ApplyManifestFilesAsync(fresh.Summary)).Success);
        resumed.MarkVerified(manifest); Assert.AreEqual("1.1", resumed.State.Version); Assert.IsFalse(resumed.HasPendingUpdate);
    }
    [TestMethod] public void ResourceOrderAndShaderSettingsArePlannedWithoutDownloadingAssets()
    {
        config.Config.SyncResourcePacks = config.Config.SyncShaderPacks = true;
        var service = Service(); var manifest = Manifest();
        var resource = service.RepositoryFor("resourcepacks"); var shader = service.RepositoryFor("shaderpacks");
        manifest.Repositories.Add(new("resourcepacks", resource.Url, resource.Branch, Commit)); manifest.Repositories.Add(new("shaderpacks", shader.Url, shader.Branch, Commit));
        manifest.Files.Add(new("resourcepacks", "pack.zip", "pack.zip", 3, Hash("zip")));
        manifest.Files.Add(new("shaderpacks", "shader.zip", "shader.zip", 3, Hash("zip")));
        manifest.ResourcePackOrder = ["pack.zip"]; manifest.ActiveShader = "shader.zip";
        var review = service.Plan(manifest);
        Assert.IsTrue(review.Summary.Changes.Any(c => c.NewContent?.Contains("file/pack.zip") == true));
        Assert.IsTrue(review.Summary.Changes.Any(c => c.NewContent?.Contains("enableShaders=true") == true));
        Assert.IsFalse(File.Exists(Path.Combine(config.MinecraftFolder, "options.txt")));
        manifest.ResourcePackOrder = ["missing.zip"]; Assert.ThrowsException<IOException>(() => service.Validate(manifest));
    }

    private sealed class PublisherGit : IGitService
    {
        public int Pushes; public string[]? Committed; public bool Ahead;
        public Task<bool> EnsureGitAvailableAsync(Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult(true);
        public Task<bool> VerifyOrResetRemoteAsync(string repoDir, string expectedUrl) => Task.FromResult(true);
        public Task<(bool Success, string? Error)> CloneAsync(string url, string dir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult((true, (string?)null));
        public Task<(bool Success, string? Error)> FetchAsync(string dir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult((true, (string?)null));
        public Task<(bool Success, string? Error)> PullOrResetToRemoteAsync(string dir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult((true, (string?)null));
        public Task<GitStatusInfo> GetStatusAsync(string dir, string url, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null) => Task.FromResult(new GitStatusInfo { IsConnected = true, AheadCount = Ahead ? 1 : 0 });
        public Task<(string Commit, bool Clean)> SnapshotAsync(string path) => Task.FromResult((ManifestTests.Commit, true));
        public Task<(bool Success, string? Error)> StageAndCommitAsync(string dir, string message, Action<SyncProgressInfo>? progressCallback = null, IReadOnlyList<string>? paths = null)
        { Committed = paths?.ToArray(); return Task.FromResult((true, (string?)null)); }
        public Task<(bool Success, string? Error)> PushAsync(string dir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null)
        { Pushes++; return Task.FromResult((true, (string?)null)); }
    }
    [TestMethod] public async Task PublishingRequiresSeparateCommitAndOnlyPublishesManifest()
    {
        Directory.CreateDirectory(Path.Combine(config.ResolvedRepositoryFolder, ".git"));
        File.WriteAllText(Path.Combine(config.ResolvedRepositoryFolder, "shared.jar"), "remote");
        var git = new PublisherGit(); var publisher = new PackPublisher(config, git, Service(), () => "test");
        var prepared = await publisher.PrepareAsync();
        Assert.AreEqual("1.1", prepared.Manifest.Version); Assert.AreEqual(0, git.Pushes);
        Assert.IsFalse(File.Exists(Path.Combine(config.ResolvedRepositoryFolder, PackManifestService.ManifestName)));
        await publisher.PublishAsync(prepared); Assert.AreEqual(1, git.Pushes);
        CollectionAssert.AreEqual(new[] { PackManifestService.ManifestName }, git.Committed);
        Assert.AreEqual("1.2", (await publisher.PrepareAsync()).Manifest.Version);
        git.Ahead = true; await Assert.ThrowsExceptionAsync<IOException>(() => publisher.PrepareAsync());
    }
}
