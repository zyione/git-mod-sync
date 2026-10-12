using ModSync.Models;

namespace ModSync.Services;

/// <summary>Serializes application checks and retains a known update until it is resolved.</summary>
public sealed class AppUpdatePriority(Func<bool, Task<UpdateInfo>> check)
{
    private Task<UpdateInfo>? _pendingCheck;
    private UpdateInfo? _lastResult;
    private DateTimeOffset _checkedAt;
    public UpdateInfo? AvailableUpdate { get; private set; }

    public Task<UpdateInfo> CheckAsync(bool force = false)
    {
        if (_pendingCheck is { IsCompleted: false }) return _pendingCheck;
        if (!force && _lastResult != null && DateTimeOffset.UtcNow - _checkedAt < TimeSpan.FromMinutes(15))
            return Task.FromResult(_lastResult);
        return _pendingCheck = CheckCoreAsync(force);
    }

    private async Task<UpdateInfo> CheckCoreAsync(bool force)
    {
        var result = await check(force);
        _lastResult = result;
        _checkedAt = DateTimeOffset.UtcNow;
        if (result.IsUpdateAvailable) AvailableUpdate = result;
        else if (string.IsNullOrEmpty(result.ErrorMessage)) AvailableUpdate = null;
        return result;
    }
}
