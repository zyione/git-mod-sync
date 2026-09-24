using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>
/// Service responsible for checking GitHub Releases for newer ModSync.exe builds,
/// downloading update binaries with live streaming progress, and executing a safe,
/// self-contained in-place executable replacement.
/// </summary>
public class UpdateService
{
    private readonly ConfigService _configService;
    private readonly AuthenticationService _authService;
    private readonly LoggingService _logger;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    public UpdateService(ConfigService configService, AuthenticationService authService, LoggingService logger)
    {
        _configService = configService;
        _authService = authService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current running application version.
    /// </summary>
    public string CurrentVersion
    {
        get
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var ver = asm.GetName().Version;
                if (ver != null)
                {
                    return $"{ver.Major}.{ver.Minor}.{ver.Build}";
                }
            }
            catch { }

            return "1.0.0";
        }
    }

    /// <summary>
    /// Checks GitHub Releases to see if a newer version of ModSync.exe is available.
    /// </summary>
    public async Task<UpdateInfo> CheckForUpdatesAsync()
    {
        string currentVerStr = CurrentVersion;
        var info = new UpdateInfo
        {
            CurrentVersion = currentVerStr,
            IsUpdateAvailable = false
        };

        try
        {
            string repoUrl = _configService.Config.AppUpdateRepository;
            if (string.IsNullOrWhiteSpace(repoUrl))
            {
                repoUrl = "https://github.com/zyione/git-mod-sync";
            }

            var (owner, repo) = ParseGitHubOwnerAndRepo(repoUrl);
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            {
                info.ErrorMessage = $"Invalid update repository URL: {repoUrl}";
                return info;
            }

            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("User-Agent", "ModSync-App-Updater");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            string? token = _authService.GetStoredToken();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // No releases published yet
                    _logger.Info($"No releases published yet on {owner}/{repo}");
                    info.LatestVersion = currentVerStr;
                    return info;
                }

                info.ErrorMessage = $"GitHub release check returned: {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger.Warning(info.ErrorMessage);
                return info;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
            string releaseTitle = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName;
            string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;
            string htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? string.Empty : string.Empty;

            DateTimeOffset? publishedAt = null;
            if (root.TryGetProperty("published_at", out var pubProp) && pubProp.TryGetDateTimeOffset(out var dt))
            {
                publishedAt = dt;
            }

            string cleanTag = tagName.TrimStart('v', 'V').Trim();
            info.LatestVersion = cleanTag;
            info.ReleaseTitle = releaseTitle;
            info.ReleaseNotes = body;
            info.ReleaseHtmlUrl = htmlUrl;
            info.PublishedAt = publishedAt;

            // Find executable asset (.exe)
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var aName) ? aName.GetString() ?? "" : "";
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        info.DownloadUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() ?? "" : "";
                        info.AssetSizeBytes = asset.TryGetProperty("size", out var szProp) ? szProp.GetInt64() : 0;
                        break;
                    }
                }
            }

            // Compare versions
            info.IsUpdateAvailable = IsNewerVersion(cleanTag, currentVerStr);
            if (info.IsUpdateAvailable)
            {
                _logger.Info($"Update detected! Current: {currentVerStr}, Latest: {cleanTag} (Asset: {info.DownloadUrl})");
            }
            else
            {
                _logger.Info($"ModSync is up to date (Version {currentVerStr}).");
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to check for ModSync updates", ex);
            info.ErrorMessage = $"Update check failed: {ex.Message}";
            return info;
        }
    }

    /// <summary>
    /// Downloads the updated executable and triggers atomic self-replacement and restart.
    /// </summary>
    public async Task<(bool Success, string? Error)> DownloadAndApplyUpdateAsync(
        string downloadUrl,
        Action<SyncProgressInfo>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return (false, "Invalid download URL for update asset.");
        }

        string appDir = PathUtils.GetAppDirectory();
        string currentExePath = Environment.ProcessPath ?? Path.Combine(appDir, "ModSync.exe");
        string newExePath = Path.Combine(appDir, "ModSync.update.exe");
        string updaterBatPath = Path.Combine(appDir, "update_modsync.bat");

        try
        {
            _logger.Info($"Starting ModSync update download from: {downloadUrl}");
            progressCallback?.Invoke(SyncProgressInfo.Indeterminate("Connecting...", "Initiating download from GitHub..."));

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.Add("User-Agent", "ModSync-App-Updater");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(newExePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long bytesReadTotal = 0;
            var stopwatch = Stopwatch.StartNew();
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                bytesReadTotal += bytesRead;

                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                double speed = elapsedSec > 0.2 ? bytesReadTotal / elapsedSec : 0;
                double? pct = totalBytes.HasValue && totalBytes.Value > 0
                    ? (double)bytesReadTotal / totalBytes.Value * 100.0
                    : null;

                string speedEta = speed > 1024
                    ? (totalBytes.HasValue && speed > 0
                        ? $"{PathUtils.FormatSpeed(speed)} • {PathUtils.FormatEta((totalBytes.Value - bytesReadTotal) / speed)}"
                        : PathUtils.FormatSpeed(speed))
                    : $"{PathUtils.FormatFileSize(bytesReadTotal)} downloaded";

                progressCallback?.Invoke(new SyncProgressInfo
                {
                    Status = "Downloading update...",
                    Percentage = pct,
                    Details = totalBytes.HasValue
                        ? $"{PathUtils.FormatFileSize(bytesReadTotal)} of {PathUtils.FormatFileSize(totalBytes.Value)}"
                        : $"{PathUtils.FormatFileSize(bytesReadTotal)} downloaded",
                    SpeedOrEta = speedEta
                });
            }

            fileStream.Close();
            _logger.Info($"Downloaded update ({PathUtils.FormatFileSize(bytesReadTotal)}) to {newExePath}");

            // Verify file
            if (!File.Exists(newExePath) || new FileInfo(newExePath).Length == 0)
            {
                return (false, "Downloaded update file is empty or missing.");
            }

            progressCallback?.Invoke(SyncProgressInfo.Determinate("Restarting...", 100, "Applying update and restarting ModSync..."));

            // Write helper batch script that waits for current PID to exit, copies new exe over current exe, and restarts
            int currentPid = Environment.ProcessId;
            string scriptContent = $@"@echo off
chcp 65001 >nul
set ""PID={currentPid}""
set ""TARGET={currentExePath}""
set ""UPDATE={newExePath}""

:wait_loop
timeout /t 1 /nobreak >nul
tasklist /fi ""PID eq %PID%"" 2>nul | findstr /i ""%PID%"" >nul
if not errorlevel 1 goto wait_loop

:copy_loop
copy /y ""%UPDATE%"" ""%TARGET%"" >nul 2>&1
if errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto copy_loop
)

del /f /q ""%UPDATE%"" >nul 2>&1
start """" ""%TARGET%""
(goto) 2>nul & del ""%~f0""
";

            File.WriteAllText(updaterBatPath, scriptContent);
            _logger.Info($"Generated updater script at {updaterBatPath}. Launching restart sequence.");

            // Launch updater script detached
            var psi = new ProcessStartInfo
            {
                FileName = updaterBatPath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = appDir
            };

            Process.Start(psi);

            // Clean shutdown of current instance
            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to apply application update", ex);
            try { if (File.Exists(newExePath)) File.Delete(newExePath); } catch { }
            try { if (File.Exists(updaterBatPath)) File.Delete(updaterBatPath); } catch { }
            return (false, $"Update failed: {ex.Message}");
        }
    }

    private static (string Owner, string Repo) ParseGitHubOwnerAndRepo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return ("", "");

        string clean = url.Trim().TrimEnd('/');
        if (clean.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^4];
        }

        // e.g. https://github.com/owner/repo or github.com/owner/repo
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return (parts[^2], parts[^1]);
        }

        return ("", "");
    }

    private static bool IsNewerVersion(string latestStr, string currentStr)
    {
        if (string.IsNullOrWhiteSpace(latestStr)) return false;

        // Try standard Version parse
        if (Version.TryParse(NormalizeVersionString(latestStr), out var latestVer) &&
            Version.TryParse(NormalizeVersionString(currentStr), out var currentVer))
        {
            return latestVer > currentVer;
        }

        // Fallback string compare
        return !string.Equals(latestStr, currentStr, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersionString(string v)
    {
        string clean = v.Trim().TrimStart('v', 'V');
        var parts = clean.Split('.');
        if (parts.Length == 1) return $"{parts[0]}.0.0";
        if (parts.Length == 2) return $"{parts[0]}.{parts[1]}.0";
        return clean;
    }
}
