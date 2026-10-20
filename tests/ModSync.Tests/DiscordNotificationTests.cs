using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModSync.Models;
using ModSync.Services;

namespace ModSync.Tests;

[TestClass]
public class DiscordNotificationTests
{
    private const string Webhook = "https://discord.com/api/webhooks/123456789/test_token";
    [TestMethod]
    public void SharedDestinationIsLoadedFromBuildResourceWithoutPlayerConfiguration()
    {
        using var stream = typeof(DiscordNotificationService).Assembly.GetManifestResourceStream("ModSync.DiscordWebhook");
        Assert.IsNotNull(stream, "Every build must contain its own destination resource, even when disabled.");
        using var reader = new System.IO.StreamReader(stream);
        string value = reader.ReadToEnd().Trim();
        Assert.AreEqual(!string.IsNullOrWhiteSpace(value), DiscordNotificationService.IsConfigured);
        if (value.Length > 0) Assert.IsTrue(DiscordNotificationService.ValidWebhook(value));
        // Never include the actual resource value in assertion messages or test output.
    }
    private sealed class Receiver : HttpMessageHandler
    {
        public int Calls;
        public string? Body;
        public Uri? Url;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public bool Offline;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Url = request.RequestUri; Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Offline) throw new HttpRequestException(Webhook);
            return new(Status);
        }
    }

    [TestMethod]
    public async Task OneCombinedMessageListsCategoriesAndDifferentSyncInstructions()
    {
        using var receiver = new Receiver(); using var client = new HttpClient(receiver);
        var changes = new PushNotificationItem[] { new("Mods", "sodium.jar", ChangeType.Added), new("Mods", "lithium.jar", ChangeType.Updated),
            new("Resource packs", "Textures.zip", ChangeType.Added), new("Shader packs", "Shader.zip", ChangeType.Removed) };
        Assert.IsNull(await new DiscordNotificationService(client, () => Webhook).NotifyAsync(changes, true));
        Assert.AreEqual(1, receiver.Calls);
        Assert.AreEqual("?wait=true", receiver.Url!.Query);
        using var json = JsonDocument.Parse(receiver.Body!);
        Assert.AreEqual(0, json.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        var embed = json.RootElement.GetProperty("embeds")[0];
        StringAssert.Contains(embed.GetProperty("description").GetString()!, "before playing");
        StringAssert.Contains(embed.GetProperty("description").GetString()!, "Want these resource pack changes?");
        StringAssert.Contains(embed.GetProperty("description").GetString()!, "Want these shader changes?");
        Assert.AreEqual(3, embed.GetProperty("fields").GetArrayLength());
        StringAssert.Contains(embed.GetProperty("fields")[0].GetProperty("name").GetString()!, "2 changed");
    }

    [TestMethod]
    public async Task DisabledOrEmptyBatchNeverContactsDiscord()
    {
        using var receiver = new Receiver(); using var client = new HttpClient(receiver);
        Assert.IsNull(await new DiscordNotificationService(client, () => null).NotifyAsync([new("Mods", "a.jar", ChangeType.Added)], true));
        Assert.IsNull(await new DiscordNotificationService(client, () => Webhook).NotifyAsync([], true));
        Assert.AreEqual(0, receiver.Calls);
    }

    [TestMethod]
    public async Task FailuresReturnWarningsWithoutExposingWebhookOrRetrying()
    {
        using var receiver = new Receiver { Status = HttpStatusCode.TooManyRequests }; using var client = new HttpClient(receiver);
        var notifier = new DiscordNotificationService(client, () => Webhook);
        var changes = new PushNotificationItem[] { new("Mods", "a.jar", ChangeType.Added) };
        StringAssert.Contains((await notifier.NotifyAsync(changes, true))!, "HTTP 429");
        receiver.Offline = true;
        string warning = (await notifier.NotifyAsync(changes, true))!;
        Assert.IsFalse(warning.Contains("test_token")); Assert.AreEqual(2, receiver.Calls);
    }

    [TestMethod]
    public void UnpublishedPartialUploadDoesNotTellPlayersToSyncUnavailableChanges()
    {
        var json = JsonSerializer.SerializeToElement(DiscordNotificationService.CreatePayload([new("Mods", "a.jar", ChangeType.Added)], false, true));
        string text = json.GetProperty("embeds")[0].GetProperty("description").GetString()!;
        StringAssert.Contains(text, "Only successful uploads"); StringAssert.Contains(text, "before syncing");
        Assert.IsFalse(text.Contains("before playing"));
    }

    [TestMethod]
    public void FolderPacksAreGroupedAndLargePushesStayWithinDiscordLimits()
    {
        var changes = Enumerable.Range(0, 400).Select(i => new PushNotificationItem("Resource packs", $"{i:D3}-@everyone-**{new string('x', 170)}.zip", ChangeType.Added)).ToList();
        changes.Add(new("Resource packs", changes[0].Name, ChangeType.Updated));
        var payload = JsonSerializer.SerializeToElement(DiscordNotificationService.CreatePayload(changes, true, false));
        var field = payload.GetProperty("embeds")[0].GetProperty("fields")[0];
        StringAssert.Contains(field.GetProperty("name").GetString()!, "400 changed");
        string text = field.GetProperty("value").GetString()!;
        Assert.IsTrue(text.Length <= 1024); StringAssert.Contains(text, "more"); Assert.IsFalse(text.Contains("@everyone"));
    }

    [TestMethod]
    public void GitChangesIncludeRetryFilesAndPackSettingsButIgnoreManifestAndReadme()
    {
        var changes = DiscordNotificationService.FromGit([
            new("mods/pending.jar", ChangeType.Added), new("resourcepacks/Folder/assets/a.png", ChangeType.Updated),
            new("resourcepacks/Folder/pack.mcmeta", ChangeType.Updated), new("shaderpacks/Shader.zip", ChangeType.Removed),
            new("resourcepack-order.txt", ChangeType.Updated), new("README.md", ChangeType.Updated), new("modpack-manifest.json", ChangeType.Updated)], SyncScope.All);
        Assert.AreEqual(4, changes.Count); Assert.IsTrue(changes.Any(c => c.Name == "pending.jar"));
        Assert.AreEqual(1, changes.Count(c => c.Name == "Folder"));
        Assert.AreEqual(ChangeType.Updated, DiscordNotificationService.FromGit([
            new("resourcepacks/Folder/assets/deleted.png", ChangeType.Removed)], SyncScope.ResourcePacks).Single().Type);
        Assert.AreEqual(ChangeType.Added, DiscordNotificationService.FromGit([
            new("resourcepacks/Folder/pack.mcmeta", ChangeType.Added), new("resourcepacks/Folder/assets/new.png", ChangeType.Added)], SyncScope.ResourcePacks).Single().Type);
        Assert.AreEqual("Resource packs", DiscordNotificationService.FromGit([new("Root.zip", ChangeType.Added)], SyncScope.ResourcePacks).Single().Category);
    }

    [TestMethod]
    public async Task RejectsNonDiscordEndpointsBeforeSending()
    {
        foreach (string url in new[] { "http://discord.com/api/webhooks/1/token", "https://example.com/api/webhooks/1/token",
            Webhook + "?redirect=1", Webhook + "#fragment", "https://discord.com.evil.test/api/webhooks/1/token" })
            Assert.IsFalse(DiscordNotificationService.ValidWebhook(url), url);
        Assert.IsTrue(DiscordNotificationService.ValidWebhook(Webhook));
        using var receiver = new Receiver(); using var client = new HttpClient(receiver);
        Assert.IsNotNull(await new DiscordNotificationService(client, () => "https://example.com").NotifyAsync([new("Mods", "a.jar", ChangeType.Added)], true));
        Assert.AreEqual(0, receiver.Calls);
    }
}
