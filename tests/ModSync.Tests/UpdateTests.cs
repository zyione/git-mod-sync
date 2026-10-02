using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class UpdateTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "ModSync_UpdateTests_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string FilePath(string name) => Path.Combine(_root, name);
    private static byte[] RandomBytes(int length) { var bytes = new byte[length]; new Random(42).NextBytes(bytes); return bytes; }
    private static UpdateAsset Asset(byte[] bytes, string name = "asset") => new() { Url = "https://example.com/" + name,
        Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    private static HttpResponseMessage Response(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new ByteArrayContent(bytes) };
    private UpdateService Service(HttpClient http) { var logger = new LoggingService(); var config = new ConfigService(logger); return new(config, new AuthenticationService(logger), logger, http, FilePath("cache")); }

    [TestMethod]
    public void DeltaReconstructsExactlyAfterInsertionsAndDeletions()
    {
        var basis = RandomBytes(2_000_000);
        var updated = basis[..200_000].Concat(new byte[] { 1, 2, 3, 4, 5 }).Concat(basis[210_000..]).ToArray();
        File.WriteAllBytes(FilePath("old"), basis); File.WriteAllBytes(FilePath("new"), updated);
        BinaryDelta.Create(FilePath("old"), FilePath("new"), FilePath("patch"));
        BinaryDelta.Apply(FilePath("old"), FilePath("patch"), FilePath("output"));
        CollectionAssert.AreEqual(updated, File.ReadAllBytes(FilePath("output")));
        Assert.IsTrue(new FileInfo(FilePath("patch")).Length < updated.Length / 5);
    }

    [TestMethod]
    public void DeltaRejectsWrongBaseAndTruncationWithoutChangingOriginal()
    {
        File.WriteAllBytes(FilePath("old"), RandomBytes(100_000));
        File.WriteAllBytes(FilePath("new"), RandomBytes(110_000));
        BinaryDelta.Create(FilePath("old"), FilePath("new"), FilePath("patch"));
        var before = BinaryDelta.Hash(FilePath("old"));
        Assert.ThrowsException<InvalidDataException>(() => BinaryDelta.Apply(FilePath("new"), FilePath("patch"), FilePath("output")));
        var patch = File.ReadAllBytes(FilePath("patch"));
        File.WriteAllBytes(FilePath("patch"), patch[..(patch.Length - 8)]);
        Assert.ThrowsException<EndOfStreamException>(() => BinaryDelta.Apply(FilePath("old"), FilePath("patch"), FilePath("output")));
        Assert.IsFalse(File.Exists(FilePath("output")));
        Assert.AreEqual(before, BinaryDelta.Hash(FilePath("old")));
    }

    [TestMethod]
    public void DeltaRejectsUnsafeOutputAndOversizedHeaders()
    {
        File.WriteAllBytes(FilePath("old"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(FilePath("new"), new byte[] { 1, 2, 4 });
        BinaryDelta.Create(FilePath("old"), FilePath("new"), FilePath("patch"));
        Assert.ThrowsException<InvalidDataException>(() => BinaryDelta.Apply(FilePath("old"), FilePath("patch"), FilePath("old")));
        var bytes = File.ReadAllBytes(FilePath("patch"));
        BitConverter.GetBytes(BinaryDelta.MaxOutputBytes + 1).CopyTo(bytes, 68);
        File.WriteAllBytes(FilePath("patch"), bytes);
        Assert.ThrowsException<InvalidDataException>(() => BinaryDelta.Apply(FilePath("old"), FilePath("patch"), FilePath("output")));
    }

    [TestMethod]
    public async Task DownloadResumesInterruptedContentAndVerifiesIt()
    {
        var bytes = RandomBytes(100_000); int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1) return Response(bytes[..50_000]);
            Assert.AreEqual(50_000L, request.Headers.Range!.Ranges.Single().From);
            var response = Response(bytes[50_000..], HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(50_000, 99_999, 100_000);
            return response;
        }));
        await new VerifiedDownload(http).DownloadAsync(Asset(bytes), FilePath("download"));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(FilePath("download")));
        Assert.AreEqual(2, calls);
        await new VerifiedDownload(http).DownloadAsync(Asset(bytes), FilePath("download"));
        Assert.AreEqual(2, calls, "An already verified download must be reused.");
    }

    [TestMethod]
    public async Task DownloadRestartsWhenServerIgnoresRange()
    {
        var bytes = RandomBytes(1000);
        File.WriteAllBytes(FilePath("download.part"), bytes[..100]);
        using var http = new HttpClient(new Handler(request => { Assert.IsNotNull(request.Headers.Range); return Response(bytes); }));
        await new VerifiedDownload(http).DownloadAsync(Asset(bytes), FilePath("download"));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(FilePath("download")));
    }

    [TestMethod]
    public async Task DownloadRejectsBadChecksumAndRetainsNoInvalidPartial()
    {
        var bytes = RandomBytes(1000);
        using var http = new HttpClient(new Handler(_ => Response(new byte[1000])));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => new VerifiedDownload(http).DownloadAsync(Asset(bytes), FilePath("download")));
        Assert.IsFalse(File.Exists(FilePath("download")));
        Assert.IsFalse(File.Exists(FilePath("download.part")));
    }

    [TestMethod]
    public async Task DownloadCancellationPreservesResumablePartial()
    {
        var bytes = RandomBytes(1000);
        File.WriteAllBytes(FilePath("download.part"), bytes[..100]);
        using var source = new CancellationTokenSource(); source.Cancel();
        using var http = new HttpClient(new Handler(_ => throw new AssertFailedException("Canceled downloads must not request content.")));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => new VerifiedDownload(http).DownloadAsync(Asset(bytes), FilePath("download"), cancellation: source.Token));
        Assert.AreEqual(100, new FileInfo(FilePath("download.part")).Length);
    }

    private static string ReleaseJson(string version, bool checksum = true) => JsonSerializer.Serialize(new {
        tag_name = "v" + version, assets = new[] {
            new { name = "Other.exe", browser_download_url = "https://example.com/wrong", size = 10, digest = "sha256:" + new string('a', 64) },
            new { name = "ModSync.exe", browser_download_url = "https://example.com/full", size = 100, digest = checksum ? "sha256:" + new string('b', 64) : "" },
            new { name = "ModSync-from-v1.0.12.delta", browser_download_url = "https://example.com/delta", size = 10, digest = "sha256:" + new string('c', 64) }
        }
    });

    [TestMethod]
    public void ReleaseParsingSelectsExactExecutableAndMatchingPatch()
    {
        var parsed = UpdateService.ParseRelease(ReleaseJson("1.1.0"), "1.0.12");
        Assert.IsTrue(parsed.IsUpdateAvailable);
        Assert.AreEqual("https://example.com/full", parsed.DownloadUrl);
        Assert.AreEqual("https://example.com/delta", parsed.DeltaAsset!.Url);
        Assert.IsFalse(UpdateService.ParseRelease(ReleaseJson("1.0.11"), "1.0.12").IsUpdateAvailable);
        Assert.IsFalse(UpdateService.ParseRelease(ReleaseJson("1.1.0", false), "1.0.12").IsUpdateAvailable);
        Assert.IsFalse(UpdateService.ParseRelease(ReleaseJson("bad-version"), "1.0.12").IsUpdateAvailable);
    }

    [TestMethod]
    public async Task UpdateChecksReuseCacheAndSendConditionalRequests()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls > 1)
            {
                Assert.AreEqual("\"release-etag\"", request.Headers.IfNoneMatch.Single().ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson("2.0.0")) };
            response.Headers.ETag = new EntityTagHeaderValue("\"release-etag\"");
            return response;
        }));
        var service = Service(http);
        Assert.IsTrue((await service.CheckForUpdatesAsync()).IsUpdateAvailable);
        Assert.IsTrue((await service.CheckForUpdatesAsync(force: false)).IsUpdateAvailable);
        Assert.AreEqual(1, calls);
        Assert.IsTrue((await service.CheckForUpdatesAsync(force: true)).IsUpdateAvailable);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task UpdatePreparationUsesDeltaAndFallsBackForWrongBase()
    {
        var full = File.ReadAllBytes(typeof(UpdateService).Assembly.Location);
        var old = full.ToArray(); old[^1] ^= 1;
        File.WriteAllBytes(FilePath("installed.exe"), old); File.WriteAllBytes(FilePath("full.exe"), full);
        BinaryDelta.Create(FilePath("installed.exe"), FilePath("full.exe"), FilePath("patch"));
        var patch = File.ReadAllBytes(FilePath("patch"));
        int fullRequests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("delta")) return Response(patch);
            fullRequests++; return Response(full);
        }));
        var service = Service(http);
        var target = Asset(full, "full");
        var update = new UpdateInfo { LatestVersion = service.CurrentVersion, DownloadUrl = target.Url,
            AssetSizeBytes = target.Size, AssetSha256 = target.Sha256, DeltaAsset = Asset(patch, "delta") };
        var staged = await service.PrepareUpdateAsync(update, FilePath("installed.exe"));
        Assert.AreEqual(target.Sha256, BinaryDelta.Hash(staged));
        Assert.AreEqual(0, fullRequests);
        old[0] ^= 1; File.WriteAllBytes(FilePath("installed.exe"), old);
        staged = await service.PrepareUpdateAsync(update, FilePath("installed.exe"));
        Assert.AreEqual(target.Sha256, BinaryDelta.Hash(staged));
        Assert.AreEqual(1, fullRequests);
    }

    [TestMethod]
    public async Task ExternalInstallerReplacesAtomicallyAndKeepsBackup()
    {
        File.WriteAllText(FilePath("ModSync.exe"), "old");
        File.WriteAllText(FilePath("ModSync.update.exe"), "new");
        var helper = UpdateInstaller.Prepare(FilePath("ModSync.exe"), FilePath("ModSync.update.exe"), 0,
            BinaryDelta.Hash(FilePath("ModSync.update.exe")), restart: false);
        await RunHelper(helper.Script, helper.Instructions);
        Assert.AreEqual("new", File.ReadAllText(FilePath("ModSync.exe")), File.ReadAllText(FilePath("ModSync.exe.update-result.json")));
        Assert.AreEqual("old", File.ReadAllText(FilePath("ModSync.exe.previous")));
        Assert.IsTrue(JsonDocument.Parse(File.ReadAllText(FilePath("ModSync.exe.update-result.json"))).RootElement.GetProperty("Success").GetBoolean());
    }

    [TestMethod]
    public async Task ExternalInstallerRejectsChangedStageAndPreservesInstalledExe()
    {
        File.WriteAllText(FilePath("ModSync.exe"), "old");
        File.WriteAllText(FilePath("ModSync.update.exe"), "new");
        var helper = UpdateInstaller.Prepare(FilePath("ModSync.exe"), FilePath("ModSync.update.exe"), 0,
            BinaryDelta.Hash(FilePath("ModSync.update.exe")), restart: false);
        File.WriteAllText(FilePath("ModSync.update.exe"), "tampered");
        await RunHelper(helper.Script, helper.Instructions);
        Assert.AreEqual("old", File.ReadAllText(FilePath("ModSync.exe")));
        Assert.IsFalse(JsonDocument.Parse(File.ReadAllText(FilePath("ModSync.exe.update-result.json"))).RootElement.GetProperty("Success").GetBoolean());
    }

    private static async Task RunHelper(string script, string instructions)
    {
        var start = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Instructions", instructions }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_response(request));
    }
}
