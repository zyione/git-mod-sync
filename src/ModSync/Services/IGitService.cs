using ModSync.Models;

namespace ModSync.Services;

/// <summary>
/// Abstraction for Git operations to ensure ModSync never requires the user to type Git commands.
/// </summary>
public interface IGitService
{
    /// <summary>
    /// Checks if Git is ready to use (either system Git or provisioned portable Git).
    /// </summary>
    Task<bool> EnsureGitAvailableAsync(Action<string>? progressCallback = null);

    /// <summary>
    /// Clones the specified repository into the target directory.
    /// </summary>
    Task<(bool Success, string? Error)> CloneAsync(string repositoryUrl, string targetDir, string branch, string? token = null, Action<string>? progressCallback = null);

    /// <summary>
    /// Fetches the latest commits from the remote repository.
    /// </summary>
    Task<(bool Success, string? Error)> FetchAsync(string repoDir, string branch, string? token = null, Action<string>? progressCallback = null);

    /// <summary>
    /// Pulls or resets the internal repository so it exactly matches origin/{branch}.
    /// </summary>
    Task<(bool Success, string? Error)> PullOrResetToRemoteAsync(string repoDir, string branch, string? token = null);

    /// <summary>
    /// Retrieves current Git status comparing local branch with remote.
    /// </summary>
    Task<GitStatusInfo> GetStatusAsync(string repoDir, string repositoryUrl, string branch, string? token = null);

    /// <summary>
    /// Stages all changes and commits them with an automated commit message.
    /// </summary>
    Task<(bool Success, string? Error)> StageAndCommitAsync(string repoDir, string commitMessage);

    /// <summary>
    /// Pushes the local branch commits to the remote repository.
    /// </summary>
    Task<(bool Success, string? Error)> PushAsync(string repoDir, string branch, string? token = null);
}
