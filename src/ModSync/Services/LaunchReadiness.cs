using ModSync.Models;

namespace ModSync.Services;

public static class LaunchReadiness
{
    public static bool IsReady(SyncSummary content, bool checkFabric, FabricStatusInfo? fabric) =>
        !content.HasChanges && (!checkFabric || fabric is { IsConfigured: true, IsUpToDate: true, ErrorMessage: null });

    public static bool NeedsPersonalReminder(AppConfig config, string instanceKey, IEnumerable<string> localMods) =>
        !config.PersonalModsAcknowledged.TryGetValue(instanceKey, out var acknowledged) ||
        localMods.Except(acknowledged, StringComparer.OrdinalIgnoreCase).Any();
}
