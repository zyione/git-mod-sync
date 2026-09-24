using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>
/// High-reliability Git execution service.
/// Automatically detects system Git, or provisions portable MinGit if Git is not installed,
/// completely isolating the user from command-line Git.
/// </summary>
public class GitService : IGitService
{
    private readonly LoggingService _logger;
    private string? _cachedGitBinaryPath;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(3) };

    public GitService(LoggingService logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds or provisions a working Git binary.
    /// </summary>
    public async Task<bool> EnsureGitAvailableAsync(Action<string>? progressCallback = null)
    {
        if (!string.IsNullOrEmpty(_cachedGitBinaryPath) && File.Exists(_cachedGitBinaryPath))
            return true;

        string? found = FindGitBinary();
        if (found != null)
        {
            _cachedGitBinaryPath = found;
            _logger.Info($"Found Git binary at: {_cachedGitBinaryPath}");
            return true;
        }

        // Auto-provision portable MinGit
        _logger.Warning("No system Git found. Starting automatic MinGit provisioning...");
        progressCallback?.Invoke("Git is not installed on this system. Downloading portable Git (MinGit)...");

        bool provisioned = await ProvisionPortableGitAsync(progressCallback);
        if (provisioned)
        {
            string portablePath = GetPortableGitPath();
            if (File.Exists(portablePath))
            {
                _cachedGitBinaryPath = portablePath;
                _logger.Info($"Portable Git successfully provisioned at: {_cachedGitBinaryPath}");
                return true;
            }
        }

        _logger.Error("Failed to find or provision Git.");
        return false;
    }

    private string? FindGitBinary()
    {
        // 1. Check local portable Git
        string portable = GetPortableGitPath();
        if (File.Exists(portable)) return portable;

        // 2. Check PATH
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "git",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (File.Exists(line.Trim())) return line.Trim();
                }
            }
        }
        catch { }

        // 3. Check well-known install locations
        string[] standardLocations =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe")
        };

        foreach (var path in standardLocations)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private string GetPortableGitPath()
    {
        return Path.Combine(PathUtils.GetAppDirectory(), "tools", "git", "cmd", "git.exe");
    }

    private async Task<bool> ProvisionPortableGitAsync(Action<string>? progressCallback)
    {
        try
        {
            string toolsDir = Path.Combine(PathUtils.GetAppDirectory(), "tools");
            string gitDir = Path.Combine(toolsDir, "git");
            string zipFile = Path.Combine(toolsDir, "mingit.zip");

            PathUtils.EnsureDirectoryExists(toolsDir);

            // MinGit official release URL (x64)
            string minGitUrl = "https://github.com/git-for-windows/git/releases/download/v2.44.0.windows.1/MinGit-2.44.0-64-bit.zip";

            progressCallback?.Invoke("Downloading portable Git (~25MB)...");
            _logger.Info($"Downloading MinGit from {minGitUrl}");

            using (var response = await HttpClient.GetAsync(minGitUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;

                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(zipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                byte[] buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int percent = (int)((totalRead * 100) / totalBytes.Value);
                        progressCallback?.Invoke($"Downloading portable Git: {percent}%");
                    }
                }
            }

            progressCallback?.Invoke("Extracting portable Git files...");
            _logger.Info("Extracting MinGit archive...");

            if (Directory.Exists(gitDir)) Directory.Delete(gitDir, true);
            ZipFile.ExtractToDirectory(zipFile, gitDir);

            try { File.Delete(zipFile); } catch { }

            progressCallback?.Invoke("Portable Git setup complete.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to provision portable MinGit", ex);
            return false;
        }
    }

    public async Task<(bool Success, string? Error)> CloneAsync(string repositoryUrl, string targetDir, string branch, string? token = null, Action<string>? progressCallback = null)
    {
        if (!await EnsureGitAvailableAsync(progressCallback))
            return (false, "Git executable could not be found or initialized.");

        try
        {
            progressCallback?.Invoke("Connecting to GitHub and cloning repository...");
            string authUrl = BuildAuthenticatedUrl(repositoryUrl, token);

            // Parent directory of target
            string parentDir = Path.GetDirectoryName(targetDir) ?? PathUtils.GetAppDirectory();
            PathUtils.EnsureDirectoryExists(parentDir);

            if (Directory.Exists(targetDir))
            {
                // If target exists but is not a valid git repo, clear it
                if (!Directory.Exists(Path.Combine(targetDir, ".git")))
                {
                    Directory.Delete(targetDir, true);
                }
            }

            string args = $"clone --branch {branch} --single-branch \"{authUrl}\" \"{targetDir}\"";
            var result = await RunGitCommandAsync(parentDir, args);

            if (result.ExitCode != 0)
            {
                return (false, FriendlyGitError(result.StdErr, repositoryUrl));
            }

            _logger.Info($"Successfully cloned {repositoryUrl} (branch {branch}) to {targetDir}");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Clone operation failed", ex);
            return (false, $"Clone failed: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> FetchAsync(string repoDir, string branch, string? token = null, Action<string>? progressCallback = null)
    {
        if (!await EnsureGitAvailableAsync(progressCallback))
            return (false, "Git executable could not be found.");

        progressCallback?.Invoke("Fetching latest updates from GitHub...");

        string args = $"fetch origin {branch}";
        var result = await RunGitCommandWithTokenAsync(repoDir, args, token);

        if (result.ExitCode != 0)
        {
            return (false, FriendlyGitError(result.StdErr));
        }

        _logger.Info($"Fetch complete for branch {branch}");
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> PullOrResetToRemoteAsync(string repoDir, string branch, string? token = null)
    {
        if (!await EnsureGitAvailableAsync())
            return (false, "Git executable could not be found.");

        // Fetch first
        var fetchResult = await FetchAsync(repoDir, branch, token);
        if (!fetchResult.Success)
            return fetchResult;

        // Reset hard to origin/branch to ensure internal repo precisely matches remote authoritative state
        var checkoutResult = await RunGitCommandAsync(repoDir, $"checkout {branch}");
        if (checkoutResult.ExitCode != 0)
        {
            // If branch does not exist locally yet, create and track
            await RunGitCommandAsync(repoDir, $"checkout -B {branch} origin/{branch}");
        }

        var resetResult = await RunGitCommandAsync(repoDir, $"reset --hard origin/{branch}");
        if (resetResult.ExitCode != 0)
        {
            return (false, FriendlyGitError(resetResult.StdErr));
        }

        // Clean untracked non-repo files inside internal repository
        await RunGitCommandAsync(repoDir, "clean -fd");

        _logger.Info($"Repository successfully synchronized to origin/{branch}");
        return (true, null);
    }

    public async Task<GitStatusInfo> GetStatusAsync(string repoDir, string repositoryUrl, string branch, string? token = null)
    {
        var status = new GitStatusInfo
        {
            RepositoryUrl = repositoryUrl,
            Branch = branch
        };

        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            status.IsCloned = false;
            status.StatusMessage = "Repository not cloned yet. Select [1] Sync Mods to download.";
            return status;
        }

        status.IsCloned = true;

        if (!await EnsureGitAvailableAsync())
        {
            status.StatusMessage = "Git runtime unavailable.";
            return status;
        }

        try
        {
            // Local commit details
            var localCommitResult = await RunGitCommandAsync(repoDir, "rev-parse --short HEAD");
            if (localCommitResult.ExitCode == 0)
            {
                status.LocalCommitHash = localCommitResult.StdOut.Trim();
            }

            var localDetailsResult = await RunGitCommandAsync(repoDir, "log -1 --format=%s%x00%aI HEAD");
            if (localDetailsResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(localDetailsResult.StdOut))
            {
                var parts = localDetailsResult.StdOut.Trim().Split('\0');
                if (parts.Length > 0) status.LocalCommitMessage = parts[0];
                if (parts.Length > 1 && DateTimeOffset.TryParse(parts[1], out var d)) status.LocalCommitDate = d;
            }

            // Fetch remote to check current status
            var fetch = await FetchAsync(repoDir, branch, token);
            if (fetch.Success)
            {
                status.IsConnected = true;

                // Remote commit details
                var remoteCommitResult = await RunGitCommandAsync(repoDir, $"rev-parse --short origin/{branch}");
                if (remoteCommitResult.ExitCode == 0)
                {
                    status.RemoteCommitHash = remoteCommitResult.StdOut.Trim();
                }

                var remoteDetailsResult = await RunGitCommandAsync(repoDir, $"log -1 --format=%s%x00%aI origin/{branch}");
                if (remoteDetailsResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(remoteDetailsResult.StdOut))
                {
                    var parts = remoteDetailsResult.StdOut.Trim().Split('\0');
                    if (parts.Length > 0) status.RemoteCommitMessage = parts[0];
                    if (parts.Length > 1 && DateTimeOffset.TryParse(parts[1], out var d)) status.RemoteCommitDate = d;
                }

                // Counts ahead/behind
                var countResult = await RunGitCommandAsync(repoDir, $"rev-list --left-right --count HEAD...origin/{branch}");
                if (countResult.ExitCode == 0)
                {
                    var counts = countResult.StdOut.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (counts.Length >= 2 && int.TryParse(counts[0], out int ahead) && int.TryParse(counts[1], out int behind))
                    {
                        status.AheadCount = ahead;
                        status.BehindCount = behind;
                    }
                }

                if (status.BehindCount > 0)
                    status.StatusMessage = $"Updates available ({status.BehindCount} new commit{(status.BehindCount > 1 ? "s" : "")} on remote).";
                else if (status.AheadCount > 0)
                    status.StatusMessage = $"Ahead of remote by {status.AheadCount} local commit{(status.AheadCount > 1 ? "s" : "")}.";
                else
                    status.StatusMessage = "Up to date with GitHub.";
            }
            else
            {
                status.IsConnected = false;
                status.StatusMessage = "Cannot reach remote repository (offline or invalid repo/branch).";
                status.ErrorMessage = fetch.Error;
            }
        }
        catch (Exception ex)
        {
            status.StatusMessage = "Error reading status.";
            status.ErrorMessage = ex.Message;
            _logger.Error("Failed to get Git status", ex);
        }

        return status;
    }

    public async Task<(bool Success, string? Error)> StageAndCommitAsync(string repoDir, string commitMessage)
    {
        if (!await EnsureGitAvailableAsync())
            return (false, "Git executable could not be found.");

        try
        {
            // Configure local committer name/email if not configured
            await RunGitCommandAsync(repoDir, "config user.name \"ModSync Admin\"");
            await RunGitCommandAsync(repoDir, "config user.email \"modsync@local\"");

            // git add -A
            var addResult = await RunGitCommandAsync(repoDir, "add -A");
            if (addResult.ExitCode != 0)
            {
                return (false, $"Failed to stage files: {addResult.StdErr}");
            }

            // Check if there is anything to commit
            var statusResult = await RunGitCommandAsync(repoDir, "status --porcelain");
            if (string.IsNullOrWhiteSpace(statusResult.StdOut))
            {
                return (true, null); // Nothing to commit
            }

            // git commit -m "..."
            string safeMsg = commitMessage.Replace("\"", "\\\"");
            var commitResult = await RunGitCommandAsync(repoDir, $"commit -m \"{safeMsg}\"");
            if (commitResult.ExitCode != 0)
            {
                return (false, $"Failed to commit: {commitResult.StdErr}");
            }

            _logger.Info($"Created commit: {commitMessage}");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Commit failed", ex);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> PushAsync(string repoDir, string branch, string? token = null)
    {
        if (!await EnsureGitAvailableAsync())
            return (false, "Git executable could not be found.");

        try
        {
            _logger.Info($"Pushing branch '{branch}' to origin...");

            // Safety check: Never force push!
            // First check if remote has changes
            var fetch = await FetchAsync(repoDir, branch, token);
            if (!fetch.Success)
            {
                return (false, $"Unable to check remote branch before push: {fetch.Error}");
            }

            // Check if behind
            var countResult = await RunGitCommandAsync(repoDir, $"rev-list --left-right --count HEAD...origin/{branch}");
            if (countResult.ExitCode == 0)
            {
                var counts = countResult.StdOut.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (counts.Length >= 2 && int.TryParse(counts[1], out int behind) && behind > 0)
                {
                    _logger.Warning($"Push aborted: Remote contains {behind} newer commits.");
                    return (false, "Remote repository contains newer changes.\n\nPlease sync first before pushing.\nNo files were uploaded.");
                }
            }

            // Push with credentials
            string args = $"push origin {branch}";
            var pushResult = await RunGitCommandWithTokenAsync(repoDir, args, token);

            if (pushResult.ExitCode != 0)
            {
                string err = pushResult.StdErr;
                _logger.Error($"Git push failed: {err}");

                if (err.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "Remote repository contains newer changes.\n\nPlease sync first before pushing.\nNo files were uploaded.");
                }

                if (err.Contains("Permission to", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("403", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "GitHub Permission Denied: Your account does not have write access to this repository.\n\nPlease ask the repository owner to add you as a Collaborator with Write permissions.");
                }

                return (false, FriendlyGitError(err));
            }

            _logger.Info("Push completed successfully.");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Push exception", ex);
            return (false, ex.Message);
        }
    }

    public async Task<bool> VerifyOrResetRemoteAsync(string repoDir, string expectedUrl)
    {
        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
            return true;

        if (!await EnsureGitAvailableAsync())
            return false;

        try
        {
            var res = await RunGitCommandAsync(repoDir, "remote get-url origin");
            if (res.ExitCode == 0)
            {
                string currentOrigin = res.StdOut.Trim();
                if (!UrlsMatch(currentOrigin, expectedUrl))
                {
                    _logger.Warning($"Internal repository remote origin '{currentOrigin}' does not match expected '{expectedUrl}'. Clearing for fresh clone.");
                    Directory.Delete(repoDir, true);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not verify remote origin in {repoDir}: {ex.Message}");
        }

        return true;
    }

    private static bool UrlsMatch(string url1, string url2)
    {
        static string Clean(string u)
        {
            string s = u.Trim().ToLowerInvariant();
            if (s.EndsWith(".git")) s = s[..^4];
            return s.TrimEnd('/');
        }
        return Clean(url1) == Clean(url2);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGitCommandWithTokenAsync(string workingDir, string gitArgs, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return await RunGitCommandAsync(workingDir, gitArgs);
        }

        // Use git -c http.extraHeader to securely pass authorization header without modifying remote URL on disk
        string authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        string fullArgs = $"-c http.extraHeader=\"Authorization: Basic {authHeader}\" {gitArgs}";

        return await RunGitCommandAsync(workingDir, fullArgs);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGitCommandAsync(string workingDir, string arguments)
    {
        string gitExe = _cachedGitBinaryPath ?? "git.exe";

        var psi = new ProcessStartInfo
        {
            FileName = gitExe,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Prevent hanging on terminal credentials prompt
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync();

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string BuildAuthenticatedUrl(string url, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return url;

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string hostAndPath = url["https://".Length..];
            return $"https://oauth2:{token}@{hostAndPath}";
        }

        return url;
    }

    private static string FriendlyGitError(string rawStderr, string? repoUrl = null)
    {
        if (string.IsNullOrWhiteSpace(rawStderr)) return "An unknown Git error occurred.";

        if (rawStderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            rawStderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            rawStderr.Contains("Invalid username or password", StringComparison.OrdinalIgnoreCase) ||
            rawStderr.Contains("401", StringComparison.OrdinalIgnoreCase))
        {
            return "GitHub authentication failed.\n\nYour login or token may have expired or is missing.\nSelect [4] GitHub Login to sign in again.";
        }

        if (rawStderr.Contains("Repository not found", StringComparison.OrdinalIgnoreCase) ||
            rawStderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return $"The GitHub repository was not found or is private.\n{(repoUrl != null ? $"URL: {repoUrl}\n" : "")}Please check your repository URL in config.json and make sure you have access.";
        }

        if (rawStderr.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            rawStderr.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase) ||
            rawStderr.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase))
        {
            return "Unable to connect to GitHub. Please check your internet connection.";
        }

        if (rawStderr.Contains("Remote branch") && rawStderr.Contains("not found"))
        {
            return "The configured Git branch was not found in the remote repository.\nPlease check the 'branch' setting in config.json.";
        }

        return rawStderr.Trim();
    }
}
