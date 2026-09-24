namespace ModSync.Models;

/// <summary>
/// Status of the internal Git repository compared against the remote branch.
/// </summary>
public class GitStatusInfo
{
    public bool IsCloned { get; set; }
    public bool IsConnected { get; set; }
    public string Branch { get; set; } = "main";
    public string RepositoryUrl { get; set; } = string.Empty;

    public string? LocalCommitHash { get; set; }
    public DateTimeOffset? LocalCommitDate { get; set; }
    public string? LocalCommitMessage { get; set; }

    public string? RemoteCommitHash { get; set; }
    public DateTimeOffset? RemoteCommitDate { get; set; }
    public string? RemoteCommitMessage { get; set; }

    public int AheadCount { get; set; }
    public int BehindCount { get; set; }

    public string StatusMessage { get; set; } = "Unknown";
    public string? ErrorMessage { get; set; }

    public bool HasUpdates => BehindCount > 0 || (LocalCommitHash != null && RemoteCommitHash != null && LocalCommitHash != RemoteCommitHash);
}
