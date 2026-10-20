using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ModSync.Models;

namespace ModSync.Services;

/// <summary>One announcement per explicit ModSync push. Webhook secrets never enter config or logs.</summary>
public sealed class DiscordNotificationService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _client;
    private readonly Func<string?> _webhook;

    public DiscordNotificationService() : this(Client, () => SharedWebhook.Value) { }
    public DiscordNotificationService(HttpClient client, Func<string?> webhook) { _client = client; _webhook = webhook; }

    private static readonly Lazy<string?> SharedWebhook = new(() =>
    {
        using var stream = typeof(DiscordNotificationService).Assembly.GetManifestResourceStream("ModSync.DiscordWebhook");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        string value = reader.ReadToEnd().Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    });
    public static bool IsConfigured => ValidWebhook(SharedWebhook.Value ?? "");

    public static bool ValidWebhook(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "discord.com" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        Regex.IsMatch(uri.AbsolutePath, @"^/api/(?:v[0-9]+/)?webhooks/[0-9]+/[A-Za-z0-9_-]+$");

    public static IReadOnlyList<PushNotificationItem> FromPlan(SyncSummary plan) => plan.Changes
        .Where(c => c.Type is ChangeType.Added or ChangeType.Updated or ChangeType.Removed)
        .Select(c => Classify(c.IsInternal && c.DestinationPath != null ? Path.GetFileName(c.DestinationPath) : c.RelativePath,
            c.Type, SyncScope.All)).OfType<PushNotificationItem>().Distinct().ToArray();

    public static IReadOnlyList<PushNotificationItem> FromGit(IReadOnlyList<GitFileChange> changes, SyncScope scope)
    {
        var result = new List<PushNotificationItem>();
        var classified = changes.Select(c => (Change: c, Notice: Classify(c.Path, c.Type, scope))).Where(c => c.Notice != null);
        foreach (var group in classified.GroupBy(c => (c.Notice!.Category, c.Notice.Name)))
        {
            var first = group.First().Notice!;
            if (first.Category == "Resource packs" && group.Any(c => c.Change.Path.EndsWith("/pack.mcmeta", StringComparison.OrdinalIgnoreCase) ||
                c.Change.Path.Contains("/assets/", StringComparison.OrdinalIgnoreCase)))
            {
                // Removing one texture is an update to the folder pack, not removal of the pack.
                var metadata = group.FirstOrDefault(c => c.Change.Path.EndsWith("/pack.mcmeta", StringComparison.OrdinalIgnoreCase));
                result.Add(first with { Type = metadata.Notice?.Type ?? ChangeType.Updated });
            }
            else result.AddRange(group.Select(c => c.Notice!).Distinct());
        }
        return result;
    }

    private static PushNotificationItem? Classify(string path, ChangeType type, SyncScope scope)
    {
        path = path.Replace('\\', '/');
        if (path == "resourcepack-order.txt") return new("Resource packs", "Shared pack order", type);
        if (path == "active-shader.txt") return new("Shader packs", "Active shader selection", type);
        if (path.StartsWith("resourcepacks/", StringComparison.OrdinalIgnoreCase))
            return new("Resource packs", path[14..].Split('/')[0], type);
        if (path.StartsWith("shaderpacks/", StringComparison.OrdinalIgnoreCase))
            return new("Shader packs", path[12..].Split('/')[0], type);
        if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && scope.HasFlag(SyncScope.Mods))
            return new("Mods", path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) ? path[5..] : path, type);
        if (scope is SyncScope.ResourcePacks or SyncScope.ResourcePackOrder or SyncScope.Shaders &&
            (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.Contains('/')) && !path.StartsWith('.'))
            return new(scope == SyncScope.Shaders ? "Shader packs" : "Resource packs", path.Split('/')[0], type);
        return null;
    }

    public static object CreatePayload(IReadOnlyList<PushNotificationItem> changes, bool published, bool partial)
    {
        var fields = new List<object>();
        foreach (string category in new[] { "Mods", "Resource packs", "Shader packs" })
        {
            // Folder packs may change many files. List the pack once, keeping all change types.
            var items = changes.Where(c => c.Category == category).GroupBy(c => c.Name).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            if (items.Length == 0) continue;
            var lines = new List<string>();
            foreach (var item in items)
            {
                string label = string.Join("/", item.Select(c => c.Type.ToString().ToLowerInvariant()).Distinct());
                string line = $"{label}: {Safe(item.Key, 160)}";
                if (lines.Sum(l => l.Length + 1) + line.Length > 800) break;
                lines.Add(line);
            }
            if (lines.Count < items.Length) lines.Add($"…and {items.Length - lines.Count} more. Check ModSync for the full list.");
            fields.Add(new { name = $"{category} · {items.Length} changed", value = string.Join("\n", lines), inline = false });
        }
        string description = partial ? "Some repositories were uploaded. Only successful uploads are listed below.\n\n" : "";
        if (!published)
            description += "Files were pushed, but a new modpack version still needs to be published. Wait for the pack owner to publish it before syncing these changes.";
        else
        {
            if (changes.Any(c => c.Category == "Mods")) description += "Remember to sync Mods in ModSync before playing.\n";
            if (changes.Any(c => c.Category == "Resource packs")) description += "Want these resource pack changes? Sync Resource Packs in ModSync.\n";
            if (changes.Any(c => c.Category == "Shader packs")) description += "Want these shader changes? Sync Shaders in ModSync.\n";
        }
        return new { allowed_mentions = new { parse = Array.Empty<string>() }, embeds = new[] {
            new { title = "ModSync · Modpack changes pushed", description, color = published ? 0x57A86B : 0xD9A441, fields } } };
    }

    private static string Safe(string value, int limit)
    {
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());
        if (value.Length > limit) value = value[..limit] + "…";
        return Regex.Replace(value.Replace("@", "@\u200b"), @"([\\`*_~|<>\[\]()])", @"\$1");
    }

    // A notification problem must never turn a successful Git upload into a failed push.
    public async Task<string?> NotifyAsync(IReadOnlyList<PushNotificationItem> changes, bool published, bool partial = false)
    {
        if (changes.Count == 0) return null;
        try
        {
            string? webhook = _webhook();
            if (string.IsNullOrWhiteSpace(webhook)) return null;
            if (!ValidWebhook(webhook)) return "Discord announcement was not sent: the shared webhook in this build is invalid. Ask the pack owner for an updated build.";
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook + "?wait=true")
                { Content = JsonContent.Create(CreatePayload(changes, published, partial)) };
            request.Headers.UserAgent.ParseAdd("ModSync/1.0");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            if (!response.IsSuccessStatusCode)
                return $"Files were uploaded, but Discord could not receive the announcement (HTTP {(int)response.StatusCode}). Ask the pack owner to check the shared webhook.";
            return null;
        }
        catch
        {
            // Exception messages can contain the webhook token, so never return or log them.
            return "Files were uploaded, but the Discord announcement could not be confirmed. Check your connection or ask the pack owner to check the shared webhook. It will not be resent automatically.";
        }
    }
}
