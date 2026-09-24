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
/// Streams real-time transfer progress, speeds, and details to callbacks.
/// </summary>
public class GitService : IGitService
{
    private readonly LoggingService _logger;
    private string? _cachedGitBinaryPath;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(3) };

    static GitService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("ModSync", "1.0"));
    }

    public GitService(LoggingService logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds or provisions a working Git binary.
    /// </summary>
    public async Task<bool> EnsureGitAvailableAsync(Action<SyncProgressInfo>? progressCallback = null)
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
        progressCallback?.Invoke(SyncProgressInfo.Indeterminate(
            "Installing portable Git runtime...",
            "Git was not detected on this system. Setting up MinGit automatically..."));

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

    private async Task<bool> ProvisionPortableGitAsync(Action<SyncProgressInfo>? progressCallback)
    {
        string toolsDir = Path.Combine(PathUtils.GetAppDirectory(), "tools");
        string gitDir = Path.Combine(toolsDir, "git");
        string zipFile = Path.Combine(toolsDir, "mingit.zip");

        PathUtils.EnsureDirectoryExists(toolsDir);

        // Build candidate list: try GitHub API for latest MinGit, followed by known stable mirrors
        var candidateUrls = new List<string>();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/git-for-windows/git/releases/latest");
            using var res = await HttpClient.SendAsync(req);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.StartsWith("MinGit-", StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith("-64-bit.zip", StringComparison.OrdinalIgnoreCase) &&
                            !name.Contains("busybox", StringComparison.OrdinalIgnoreCase))
                        {
                            string downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            if (!string.IsNullOrEmpty(downloadUrl))
                            {
                                candidateUrls.Add(downloadUrl);
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not fetch latest MinGit release metadata from GitHub API: {ex.Message}");
        }

        // Add known stable fallback URLs
        candidateUrls.Add("https://github.com/git-for-windows/git/releases/download/v2.44.0.windows.1/MinGit-2.44.0-64-bit.zip");
        candidateUrls.Add("https://github.com/git-for-windows/git/releases/download/v2.45.0.windows.1/MinGit-2.45.0-64-bit.zip");
        candidateUrls.Add("https://github.com/git-for-windows/git/releases/download/v2.43.0.windows.1/MinGit-2.43.0-64-bit.zip");

        bool downloaded = false;
        foreach (string minGitUrl in candidateUrls.Distinct())
        {
            try
            {
                progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Downloading portable Git (~25MB)...", "Connecting to download server..."));
                _logger.Info($"Attempting to download MinGit from {minGitUrl}");

                using var response = await HttpClient.GetAsync(minGitUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warning($"Failed to download MinGit from {minGitUrl}: {response.StatusCode}");
                    continue;
                }

                long? totalBytes = response.Content.Headers.ContentLength;

                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(zipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                byte[] buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;
                var stopwatch = Stopwatch.StartNew();

                while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double percent = (double)totalRead / totalBytes.Value * 100.0;
                        double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                        double bytesPerSec = elapsedSec > 0.2 ? totalRead / elapsedSec : 0;
                        double remainingSec = bytesPerSec > 1024 ? (totalBytes.Value - totalRead) / bytesPerSec : 0;

                        string speedEta = bytesPerSec > 1024
                            ? $"{PathUtils.FormatSpeed(bytesPerSec)} • {PathUtils.FormatEta(remainingSec)}"
                            : PathUtils.FormatFileSize(totalRead);

                        string details = $"Downloaded {PathUtils.FormatFileSize(totalRead)} of {PathUtils.FormatFileSize(totalBytes.Value)}";

                        progressCallback?.Invoke(SyncProgressInfo.Determinate(
                            "Downloading portable Git...",
                            percent,
                            details,
                            speedEta));
                    }
                }

                downloaded = true;
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Download error from {minGitUrl}: {ex.Message}");
                try { if (File.Exists(zipFile)) File.Delete(zipFile); } catch { }
            }
        }

        if (!downloaded || !File.Exists(zipFile))
        {
            _logger.Error("All portable Git download sources failed. Please install Git manually from https://git-scm.com/download/win");
            return false;
        }

        try
        {
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate(
                "Extracting portable Git files...",
                "Unpacking archive into tools/git..."));
            _logger.Info("Extracting MinGit archive...");

            if (Directory.Exists(gitDir)) Directory.Delete(gitDir, true);
            ZipFile.ExtractToDirectory(zipFile, gitDir);

            try { File.Delete(zipFile); } catch { }

            progressCallback?.Invoke(SyncProgressInfo.Determinate(
                "Portable Git ready",
                100,
                "Setup complete."));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to extract MinGit archive", ex);
            return false;
        }
    }

    public async Task<(bool Success, string? Error)> CloneAsync(
        string repositoryUrl,
        string targetDir,
        string branch,
        string? token = null,
        Action<SyncProgressInfo>? progressCallback = null)
    {
        if (!await EnsureGitAvailableAsync(progressCallback))
            return (false, "Git executable could not be found or initialized.\n\nPlease install Git manually from https://git-scm.com/download/win and restart ModSync.");

        try
        {
            const string defaultStatus = "Connecting to GitHub and cloning repository...";
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate(defaultStatus, "Connecting to GitHub..."));

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

            // Secure clone: pass clean repositoryUrl so git does not persist plain-text credentials to .git/config
            string args = $"clone --progress --branch {branch} --single-branch \"{repositoryUrl}\" \"{targetDir}\"";
            var result = await RunGitCommandWithTokenAsync(parentDir, args, token, line =>
            {
                var progress = ParseGitProgressLine(line, defaultStatus);
                if (progress != null) progressCallback?.Invoke(progress);
            });

            if (result.ExitCode != 0)
            {
                return (false, FriendlyGitError(result.StdErr, repositoryUrl));
            }

            progressCallback?.Invoke(SyncProgressInfo.Determinate(defaultStatus, 100, "Repository cloned successfully."));
            _logger.Info($"Successfully cloned {repositoryUrl} (branch {branch}) to {targetDir}");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Clone operation failed", ex);
            return (false, $"Clone failed: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> FetchAsync(
        string repoDir,
        string branch,
        string? token = null,
        Action<SyncProgressInfo>? progressCallback = null)
    {
        if (!await EnsureGitAvailableAsync(progressCallback))
            return (false, "Git executable could not be found.");

        const string defaultStatus = "Fetching latest updates from GitHub...";
        progressCallback?.Invoke(SyncProgressInfo.Indeterminate(defaultStatus, "Connecting to remote origin..."));

        string args = $"fetch --progress origin {branch}";
        var result = await RunGitCommandWithTokenAsync(repoDir, args, token, line =>
        {
            var progress = ParseGitProgressLine(line, defaultStatus);
            if (progress != null) progressCallback?.Invoke(progress);
        });

        if (result.ExitCode != 0)
        {
            return (false, FriendlyGitError(result.StdErr));
        }

        _logger.Info($"Fetch complete for branch {branch}");
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> PullOrResetToRemoteAsync(
        string repoDir,
        string branch,
        string? token = null,
        Action<SyncProgressInfo>? progressCallback = null)
    {
        if (!await EnsureGitAvailableAsync(progressCallback))
            return (false, "Git executable could not be found.");

        // Fetch first with progress
        var fetchResult = await FetchAsync(repoDir, branch, token, progressCallback);
        if (!fetchResult.Success)
            return fetchResult;

        progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Synchronizing branch state...", $"Updating to origin/{branch}..."));

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

    public async Task<GitStatusInfo> GetStatusAsync(
        string repoDir,
        string repositoryUrl,
        string branch,
        string? token = null,
        Action<SyncProgressInfo>? progressCallback = null)
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

        if (!await EnsureGitAvailableAsync(progressCallback))
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
            var fetch = await FetchAsync(repoDir, branch, token, progressCallback);
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

    public async Task<(bool Success, string? Error)> StageAndCommitAsync(
        string repoDir,
        string commitMessage,
        Action<SyncProgressInfo>? progressCallback = null)
    {
        if (!await EnsureGitAvailableAsync(progressCallback))
            return (false, "Git executable could not be found.");

        try
        {
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Staging mod changes...", "Configuring Git author identity..."));

            // Configure local committer name/email if not configured
            await RunGitCommandAsync(repoDir, "config user.name \"ModSync Admin\"");
            await RunGitCommandAsync(repoDir, "config user.email \"modsync@local\"");

            // git add -A
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Staging mod changes...", "Running git add -A..."));
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
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Creating Git commit...", commitMessage));
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

    public async Task<(bool Success, string? Error)> PushAsync(
        string repoDir,
        string branch,
        string? token = null,
        Action<SyncProgressInfo>? progressCallback = null)
    {
        if (!await EnsureGitAvailableAsync(progressCallback))
            return (false, "Git executable could not be found.");

        try
        {
            _logger.Info($"Pushing branch '{branch}' to origin...");
            const string defaultStatus = "Pushing mod updates to GitHub...";

            // Safety check: Never force push!
            // First check if remote has changes
            var fetch = await FetchAsync(repoDir, branch, token, progressCallback);
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

            progressCallback?.Invoke(SyncProgressInfo.Indeterminate(defaultStatus, "Uploading objects to GitHub..."));

            // Push with credentials & progress
            string args = $"push --progress origin {branch}";
            var pushResult = await RunGitCommandWithTokenAsync(repoDir, args, token, line =>
            {
                var progress = ParseGitProgressLine(line, defaultStatus);
                if (progress != null) progressCallback?.Invoke(progress);
            });

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

            progressCallback?.Invoke(SyncProgressInfo.Determinate(defaultStatus, 100, "Push completed successfully!"));
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

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGitCommandWithTokenAsync(
        string workingDir,
        string gitArgs,
        string? token,
        Action<string>? onStderrLine = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return await RunGitCommandAsync(workingDir, gitArgs, onStderrLine);
        }

        // Use git -c http.extraHeader to securely pass authorization header without modifying remote URL on disk
        string authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        string fullArgs = $"-c http.extraHeader=\"Authorization: Basic {authHeader}\" {gitArgs}";

        return await RunGitCommandAsync(workingDir, fullArgs, onStderrLine);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGitCommandAsync(
        string workingDir,
        string arguments,
        Action<string>? onStderrLine = null)
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

        proc.Start();
        proc.BeginOutputReadLine();

        // Asynchronously stream stderr chunk-by-chunk splitting on both \r and \n in real time
        var errorTask = Task.Run(async () =>
        {
            var charBuffer = new char[512];
            var lineBuffer = new StringBuilder();
            using var reader = proc.StandardError;
            int charsRead;

            while ((charsRead = await reader.ReadAsync(charBuffer, 0, charBuffer.Length)) > 0)
            {
                for (int i = 0; i < charsRead; i++)
                {
                    char c = charBuffer[i];
                    if (c == '\r' || c == '\n')
                    {
                        if (lineBuffer.Length > 0)
                        {
                            string line = lineBuffer.ToString();
                            lineBuffer.Clear();
                            stderr.AppendLine(line);
                            onStderrLine?.Invoke(line);
                        }
                    }
                    else
                    {
                        lineBuffer.Append(c);
                    }
                }
            }

            if (lineBuffer.Length > 0)
            {
                string line = lineBuffer.ToString();
                stderr.AppendLine(line);
                onStderrLine?.Invoke(line);
            }
        });

        await Task.WhenAll(proc.WaitForExitAsync(), errorTask);

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Parses Git progress lines from stderr into rich SyncProgressInfo.
    /// Handles Counting, Compressing, Receiving, Writing, Resolving deltas, and Updating files.
    /// </summary>
    public static SyncProgressInfo? ParseGitProgressLine(string rawLine, string defaultStatus)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return null;

        string line = rawLine.Trim();
        if (line.StartsWith("remote:", StringComparison.OrdinalIgnoreCase))
        {
            line = line["remote:".Length..].Trim();
        }

        // Example match: Receiving objects:  78% (120/154), 14.20 MiB | 3.45 MiB/s
        var match = Regex.Match(line, @"([A-Za-z\s]+):\s*(\d+)%\s*\(([^)]+)\)(?:,\s*([0-9.]+\s*[KMGT]?i?B))?(?:\s*\|\s*([0-9.]+\s*[^,\r\n]+))?", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            string phase = match.Groups[1].Value.Trim();
            double pct = double.TryParse(match.Groups[2].Value, out var p) ? p : 0;
            string count = match.Groups[3].Value.Trim();
            string bytesTransferred = match.Groups[4].Success ? match.Groups[4].Value.Trim() : string.Empty;
            string speed = match.Groups[5].Success ? match.Groups[5].Value.Trim() : string.Empty;

            double overallPct;
            string detail;

            if (phase.StartsWith("Counting", StringComparison.OrdinalIgnoreCase))
            {
                overallPct = Math.Min(10.0, pct * 0.1);
                detail = $"Analyzing repository objects: {count}";
            }
            else if (phase.StartsWith("Compressing", StringComparison.OrdinalIgnoreCase))
            {
                overallPct = 10.0 + (pct * 0.05);
                detail = $"Compressing objects: {count}";
            }
            else if (phase.StartsWith("Receiving", StringComparison.OrdinalIgnoreCase))
            {
                overallPct = 15.0 + (pct * 0.70); // 15% -> 85%
                detail = string.IsNullOrEmpty(bytesTransferred)
                    ? $"Receiving objects: {count}"
                    : $"Receiving objects: {count} ({bytesTransferred})";
            }
            else if (phase.StartsWith("Writing", StringComparison.OrdinalIgnoreCase))
            {
                overallPct = 15.0 + (pct * 0.80); // 15% -> 95%
                detail = string.IsNullOrEmpty(bytesTransferred)
                    ? $"Uploading objects: {count}"
                    : $"Uploading objects: {count} ({bytesTransferred})";
            }
            else if (phase.StartsWith("Resolving", StringComparison.OrdinalIgnoreCase))
            {
                overallPct = 85.0 + (pct * 0.10); // 85% -> 95%
                detail = $"Resolving deltas: {count}";
            }
            else if (phase.StartsWith("Updating", StringComparison.OrdinalIgnoreCase))
            {
                overallPct = 95.0 + (pct * 0.05); // 95% -> 100%
                detail = $"Extracting mod files: {count}";
            }
            else
            {
                overallPct = pct;
                detail = $"{phase}: {count}";
            }

            string? speedOrEta = !string.IsNullOrEmpty(speed) ? speed : null;

            return SyncProgressInfo.Determinate(defaultStatus, overallPct, detail, speedOrEta);
        }

        // Informational lines (e.g. Enumerating objects, Cloning into...)
        if (line.Length > 3 && line.Length < 75 && !line.Contains("warning:", StringComparison.OrdinalIgnoreCase))
        {
            return SyncProgressInfo.Indeterminate(defaultStatus, line);
        }

        return null;
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
