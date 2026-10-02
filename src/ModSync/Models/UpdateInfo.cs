namespace ModSync.Models;

/// <summary>
/// Information about available application updates fetched from GitHub Releases.
/// </summary>
public class UpdateInfo
{
    public bool IsUpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "1.0.0";
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseTitle { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long AssetSizeBytes { get; set; }
    public string ReleaseHtmlUrl { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string AssetSha256 { get; set; } = "";
    public UpdateAsset? DeltaAsset { get; set; }

    public string FormattedSize => AssetSizeBytes > 0 ? Utils.PathUtils.FormatFileSize(AssetSizeBytes) : string.Empty;
}

public class UpdateAsset
{
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}
