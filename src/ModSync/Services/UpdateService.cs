using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

public class UpdateService
{
    private readonly ConfigService _configService;
    private readonly AuthenticationService _authService;
    private readonly LoggingService _logger;
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public UpdateService(ConfigService configService, AuthenticationService authService, LoggingService logger,
        HttpClient? http = null, string? cacheDirectory = null)
    {
        _configService = configService;
        _authService = authService;
        _logger = logger;
        _http = http ?? SharedHttp;
        _cacheDirectory = cacheDirectory ?? Path.Combine(PathUtils.GetAppDirectory(), ".updates");
    }

    public string CurrentVersion => Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0].TrimStart('v', 'V')
        ?? "1.0.0";

    public sealed class ReleaseCache
    {
        public string Repository { get; set; } = "";
        public string Json { get; set; } = "";
        public string? ETag { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    public async Task<UpdateInfo> CheckForUpdatesAsync(bool force = true, CancellationToken cancellation = default)
    {
        var info = new UpdateInfo { CurrentVersion = CurrentVersion };
        try
        {
            var (owner, repo) = PathUtils.ParseGitHubOwnerAndRepo(_configService.Config.AppUpdateRepository);
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) throw new InvalidDataException("Invalid update repository.");
            string apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            string cachePath = Path.Combine(_cacheDirectory, "release-cache.json");
            ReleaseCache? cache = null;
            try { if (File.Exists(cachePath)) cache = JsonSerializer.Deserialize<ReleaseCache>(File.ReadAllText(cachePath)); }
            catch (Exception ex) when (ex is IOException or JsonException) { _logger.Warning("Ignoring invalid update cache."); }
            if (cache?.Repository != apiUrl) cache = null;
            if (!force && cache != null && DateTimeOffset.UtcNow - cache.CheckedAt < TimeSpan.FromMinutes(15))
                return ParseRelease(cache.Json, CurrentVersion);
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("User-Agent", "ModSync-App-Updater");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (cache?.ETag != null && EntityTagHeaderValue.TryParse(cache.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
            string? token = _authService.GetStoredToken();
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound) return info;
            string json;
            if (response.StatusCode == HttpStatusCode.NotModified && cache != null) json = cache.Json;
            else
            {
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellation);
            }
            info = ParseRelease(json, CurrentVersion);
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                File.WriteAllText(cachePath, JsonSerializer.Serialize(new ReleaseCache { Repository = apiUrl,
                    Json = json, ETag = response.Headers.ETag?.ToString() ?? cache?.ETag, CheckedAt = DateTimeOffset.UtcNow }));
            }
            catch (IOException ex) { _logger.Warning($"Could not cache update metadata: {ex.Message}"); }
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error("Failed to check for updates", ex);
            info.ErrorMessage = $"Update check failed: {ex.Message}";
            return info;
        }
    }

    public static UpdateInfo ParseRelease(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static string String(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        var info = new UpdateInfo { CurrentVersion = currentVersion,
            LatestVersion = String(root, "tag_name").TrimStart('v', 'V'), ReleaseTitle = String(root, "name"),
            ReleaseNotes = String(root, "body"), ReleaseHtmlUrl = String(root, "html_url") };
        if (root.TryGetProperty("published_at", out var date) && date.TryGetDateTimeOffset(out var published)) info.PublishedAt = published;
        if (!Version.TryParse(info.LatestVersion, out var latest) || !Version.TryParse(currentVersion, out var current) || latest <= current)
            return info;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return info;
        if (!root.TryGetProperty("assets", out var assets)) return info;
        foreach (var asset in assets.EnumerateArray())
        {
            var metadata = new UpdateAsset { Url = String(asset, "browser_download_url"),
                Size = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                Sha256 = String(asset, "digest").Replace("sha256:", "", StringComparison.Ordinal).ToLowerInvariant() };
            string name = String(asset, "name");
            if (name == "ModSync.exe")
            {
                info.DownloadUrl = metadata.Url;
                info.AssetSizeBytes = metadata.Size;
                info.AssetSha256 = metadata.Sha256;
            }
            else if (name == $"ModSync-from-v{currentVersion}.delta") info.DeltaAsset = metadata;
        }
        try
        {
            VerifiedDownload.ValidateAsset(new UpdateAsset { Url = info.DownloadUrl, Size = info.AssetSizeBytes, Sha256 = info.AssetSha256 });
            info.IsUpdateAvailable = true;
        }
        catch (InvalidDataException ex) { info.ErrorMessage = ex.Message; }
        return info;
    }

    /// <summary>Builds a verified staged executable without modifying or restarting the current application.</summary>
    public async Task<string> PrepareUpdateAsync(UpdateInfo update, string currentExe,
        Action<SyncProgressInfo>? progress = null, CancellationToken cancellation = default)
    {
        var full = new UpdateAsset { Url = update.DownloadUrl, Size = update.AssetSizeBytes, Sha256 = update.AssetSha256 };
        VerifiedDownload.ValidateAsset(full);
        string directory = Path.GetDirectoryName(Path.GetFullPath(currentExe))!;
        Directory.CreateDirectory(_cacheDirectory);
        string staged = Path.Combine(directory, "ModSync.update.exe");
        if (File.Exists(staged) && (File.GetAttributes(staged) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked update staging files are not supported.");
        var downloader = new VerifiedDownload(_http);
        bool reconstructed = false;
        if (update.DeltaAsset is { } delta && delta.Size < full.Size)
        {
            try
            {
                VerifiedDownload.ValidateAsset(delta);
                string patch = Path.Combine(_cacheDirectory, delta.Sha256 + ".delta");
                await downloader.DownloadAsync(delta, patch, progress, cancellation);
                progress?.Invoke(SyncProgressInfo.Indeterminate("Preparing update...", "Verifying the smaller download..."));
                await Task.Run(() => BinaryDelta.Apply(currentExe, patch, staged), cancellation);
                reconstructed = new FileInfo(staged).Length == full.Size && BinaryDelta.Hash(staged).Equals(full.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!reconstructed) throw new InvalidDataException("Patch target differs from the release executable.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning($"Delta update unavailable; using full download: {ex.Message}");
                progress?.Invoke(SyncProgressInfo.Indeterminate("Downloading update...", "Using the full download for this version..."));
            }
        }
        if (!reconstructed)
        {
            string downloaded = Path.Combine(_cacheDirectory, full.Sha256 + ".exe");
            await downloader.DownloadAsync(full, downloaded, progress, cancellation);
            File.Copy(downloaded, staged, overwrite: true);
        }
        if (new FileInfo(staged).Length != full.Size || !BinaryDelta.Hash(staged).Equals(full.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Staged update checksum failed.");
        var version = FileVersionInfo.GetVersionInfo(staged);
        if (version.FileMajorPart + "." + version.FileMinorPart + "." + version.FileBuildPart != update.LatestVersion)
            throw new InvalidDataException("Downloaded executable version does not match the release.");
        return staged;
    }

    public void PruneDownloadCache()
    {
        if (!Directory.Exists(_cacheDirectory)) return;
        foreach (var extension in new[] { ".exe", ".delta", ".part" })
        {
            var files = new DirectoryInfo(_cacheDirectory).GetFiles("*" + extension)
                .Where(x => (x.Attributes & FileAttributes.ReparsePoint) == 0).OrderByDescending(x => x.LastWriteTimeUtc);
            foreach (var file in extension == ".part" ? files.Where(x => x.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) : files.Skip(extension == ".exe" ? 2 : 3))
            {
                try { file.Delete(); } catch (IOException ex) { _logger.Warning($"Could not remove old download cache: {ex.Message}"); }
            }
        }
    }

    public async Task<(bool Success, string? Error)> DownloadAndApplyUpdateAsync(UpdateInfo update,
        Action<SyncProgressInfo>? progressCallback = null, CancellationToken cancellation = default)
    {
        if (!await _updateLock.WaitAsync(0, cancellation)) return (false, "An update is already in progress.");
        try
        {
            string current = Environment.ProcessPath ?? throw new IOException("Cannot locate the running executable.");
            string staged = await PrepareUpdateAsync(update, current, progressCallback, cancellation);
            var helper = UpdateInstaller.Prepare(current, staged, Environment.ProcessId, update.AssetSha256);
            var start = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"), UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(current)! };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper.Script, "-Instructions", helper.Instructions })
                start.ArgumentList.Add(argument);
            if (Process.Start(start) == null) throw new IOException("Could not start the update helper.");
            Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());
            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "Update paused. You can resume it later."); }
        catch (Exception ex)
        {
            _logger.Error("Update failed", ex);
            return (false, $"Update failed: {ex.Message}");
        }
        finally { _updateLock.Release(); }
    }
}
