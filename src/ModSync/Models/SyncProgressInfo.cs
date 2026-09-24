namespace ModSync.Models;

/// <summary>
/// Detailed progress notification for active operations.
/// Supports high-level phase, fine-grained details, percentage (determinate or indeterminate),
/// transfer speed, and ETA.
/// </summary>
public class SyncProgressInfo
{
    /// <summary>
    /// High-level operation title or phase (e.g. "Downloading updates...", "Syncing mods...", "Backing up mods...").
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Fine-grained item details (e.g. "sodium-fabric-0.5.8.jar (12 of 24)" or "Receiving objects: 120/154").
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Progress percentage from 0 to 100, or null if the task is indeterminate.
    /// </summary>
    public double? Percentage { get; set; }

    /// <summary>
    /// Transfer speed and/or estimated time remaining (e.g. "3.4 MB/s • ETA 4s").
    /// </summary>
    public string? SpeedOrEta { get; set; }

    public static SyncProgressInfo Indeterminate(string status, string? details = null)
    {
        return new SyncProgressInfo
        {
            Status = status,
            Details = details,
            Percentage = null,
            SpeedOrEta = null
        };
    }

    public static SyncProgressInfo Determinate(string status, double percentage, string? details = null, string? speedOrEta = null)
    {
        return new SyncProgressInfo
        {
            Status = status,
            Percentage = Math.Clamp(percentage, 0.0, 100.0),
            Details = details,
            SpeedOrEta = speedOrEta
        };
    }
}
