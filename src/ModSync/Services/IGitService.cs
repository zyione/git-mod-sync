using ModSync.Models;

namespace ModSync.Services;

/// <summary>
/// Abstraction for Git operations to ensure ModSync never requires the user to type Git commands.
/// Fully supports real-time progress callbacks for streaming download/upload percentages, details, and speeds.
/// </summary>
public interface IGitService
{
    Task<(string Commit, bool Clean)> SnapshotAsync(string folder) => throw new NotSupportedException("Snapshot publishing is unavailable.");
    async Task<GitStatusInfo> CheckRepositoryAsync(string folder, string url, string branch, bool refresh, string? token = null, Action<SyncProgressInfo>? progress = null)
    {
        (bool Success, string? Error) result = (true, null);
        if (!Directory.Exists(Path.Combine(folder, ".git"))) result = await CloneAsync(url, folder, branch, token, progress);
        else if (refresh) result = await PullOrResetToRemoteAsync(folder, branch, token, progress);
        if (!result.Success) throw new IOException(result.Error);
        return await GetStatusAsync(folder, url, branch, token, progress);
    }
    /// <summary>
    /// Checks if Git is ready to use (either system Git or provisioned portable Git).
    /// </summary>
    Task<bool> EnsureGitAvailableAsync(Action<SyncProgressInfo>? progressCallback = null);

    /// <summary>
    /// Clones the specified repository into the target directory with real-time transfer progress.
    /// </summary>
    Task<(bool Success, string? Error)> CloneAsync(string repositoryUrl, string targetDir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null);

    /// <summary>
    /// Fetches the latest commits from the remote repository with real-time transfer progress.
    /// </summary>
    Task<(bool Success, string? Error)> FetchAsync(string repoDir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null);

    /// <summary>
    /// Pulls or resets the internal repository so it exactly matches origin/{branch}.
    /// </summary>
    Task<(bool Success, string? Error)> PullOrResetToRemoteAsync(string repoDir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null);

    /// <summary>
    /// Retrieves current Git status comparing local branch with remote.
    /// </summary>
    Task<GitStatusInfo> GetStatusAsync(string repoDir, string repositoryUrl, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null);

    /// <summary>
    /// Stages and commits the selected paths, or all changes when paths are omitted.
    /// </summary>
    Task<(bool Success, string? Error)> StageAndCommitAsync(string repoDir, string commitMessage, Action<SyncProgressInfo>? progressCallback = null, IReadOnlyList<string>? paths = null);

    /// <summary>
    /// Pushes the local branch commits to the remote repository with real-time upload progress.
    /// </summary>
    Task<(bool Success, string? Error)> PushAsync(string repoDir, string branch, string? token = null, Action<SyncProgressInfo>? progressCallback = null);

    /// <summary>
    /// Verifies that an existing cloned repository's origin matches the expected repository URL.
    /// If it does not match (e.g. user switched repositories), the folder is safely cleared for re-cloning.
    /// </summary>
    Task<bool> VerifyOrResetRemoteAsync(string repoDir, string expectedUrl);
}
