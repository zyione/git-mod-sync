using ModSync.Models;

namespace ModSync.Services;

public static class LaunchReadiness
{
    public static bool IsReady(SyncSummary content, bool checkFabric, FabricStatusInfo? fabric) =>
        !content.HasChanges && (!checkFabric || fabric is { IsConfigured: true, IsUpToDate: true, ErrorMessage: null });

}
