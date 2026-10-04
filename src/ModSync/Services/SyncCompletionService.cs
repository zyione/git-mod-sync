using ModSync.Models;

namespace ModSync.Services;

public static class SyncCompletionService
{
    public static string Describe(SyncSummary summary, SyncScope scope, bool resourcePacksEnabled,
        bool shadersEnabled, bool repositoryHasOrder, string? fabricVersion = null, bool fabricFailed = false)
    {
        var changes = summary.Changes.Where(c => !c.IsInternal &&
            c.Type is ChangeType.Added or ChangeType.Updated or ChangeType.Removed).ToList();
        var lines = new List<string> { fabricFailed ? "Files checked; Fabric Loader needs attention" : changes.Count > 0 || fabricVersion != null ? "Sync complete" : "Already up to date" };
        if (scope.HasFlag(SyncScope.Mods))
            lines.Add(changes.Any(c => c.NewContent == null && !c.RelativePath.StartsWith("resourcepacks/") && !c.RelativePath.StartsWith("shaderpacks/"))
                ? "Mods updated." : "Mods already up to date.");
        if (resourcePacksEnabled && (scope.HasFlag(SyncScope.ResourcePacks) || scope.HasFlag(SyncScope.ResourcePackOrder)))
        {
            if (scope.HasFlag(SyncScope.ResourcePacks))
                lines.Add(changes.Any(c => c.RelativePath.StartsWith("resourcepacks/")) ? "Resource packs updated." : "Resource packs already up to date.");
            lines.Add(changes.Any(c => c.RelativePath == "options.txt (resource pack order)") ? "Repository pack order applied."
                : repositoryHasOrder ? "Pack order already matches the repository." : "No repository pack order provided; current order kept.");
        }
        if (shadersEnabled && scope.HasFlag(SyncScope.Shaders))
            lines.Add(changes.Any(c => c.RelativePath.StartsWith("shaderpacks/") || c.RelativePath.Contains("(active shader)"))
                ? "Shaders updated." : "Shaders already up to date.");
        if (fabricVersion != null) lines.Add($"Fabric Loader updated to {fabricVersion}.");
        if (fabricFailed) lines.Add("Fabric Loader could not be updated. Use its Update action before starting Minecraft.");
        if (!fabricFailed && (changes.Count > 0 || fabricVersion != null))
        {
            lines.Add("Start Minecraft to use these changes. Restart it if it is already running.");
            if (changes.Any(c => c.RelativePath == "options.txt (resource pack order)"))
                lines.Add("If Minecraft was running during sync, close it and apply the pack order again before starting.");
        }
        return string.Join("\n", lines);
    }
}
