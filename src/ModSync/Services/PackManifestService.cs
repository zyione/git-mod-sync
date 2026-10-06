using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

public sealed record ManifestReview(PackManifest Manifest, SyncSummary Summary, string Configuration, SyncScope Scope);
public sealed class InstalledPackState
{
    public string? Policy { get; set; }
    public string? Version { get; set; }
    public string? ManifestHash { get; set; }
    public List<PackFile> Managed { get; set; } = new();
}

/// <summary>Checks transfer only a bounded manifest. Content downloads are a separate approved operation.</summary>
public sealed class PackManifestService(ConfigService config, ModIgnoreService ignores, HttpClient http, Func<string?> token)
{
    public const string ManifestName = "modsync-manifest.json";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private string Data => Path.GetDirectoryName(config.ConfigFilePath)!;
    private string StatePath => Path.Combine(Data, "pack-state", config.InstanceStorageKey + ".json");
    private string Journal => Path.Combine(Data, "pack-state", config.InstanceStorageKey + ".pending.json");
    private string ContentPath(PackFile file) => Path.Combine(Data, "content", file.Sha256.ToLowerInvariant());
    private string Policy => JsonSerializer.Serialize(new { config.Config.Repository, config.Config.Branch, config.Config.ResourcePackRepository,
        config.Config.ResourcePackBranch, config.Config.ShaderPackRepository, config.Config.ShaderPackBranch, config.Config.SyncResourcePacks,
        config.Config.SyncShaderPacks, config.Config.SyncFabricLoader, config.Config.EnforceActiveShader, config.Config.IgnoredMods });
    public InstalledPackState State
    {
        get
        {
            var state = File.Exists(StatePath) ? JsonSerializer.Deserialize<InstalledPackState>(File.ReadAllText(StatePath), Json)
                ?? throw new IOException("Installed pack state is unreadable.") : new();
            if (state.Policy != Policy) { state.Version = null; state.ManifestHash = null; }
            return state;
        }
    }
    public bool HasPendingUpdate => File.Exists(Journal);
    public static string Digest(PackManifest manifest) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json))));

    public static string RawUrl(string repository, string revision, string path, bool authenticated = false)
    {
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com")
            throw new IOException("Pack manifests currently require HTTPS GitHub repositories.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2) throw new IOException("Invalid GitHub repository URL.");
        string name = parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1];
        string escapedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return authenticated ? $"https://api.github.com/repos/{parts[0]}/{name}/contents/{escapedPath}?ref={Uri.EscapeDataString(revision)}"
            : $"https://raw.githubusercontent.com/{parts[0]}/{name}/{Uri.EscapeDataString(revision)}/{escapedPath}";
    }
    private HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ModSync-PackManifest/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        string? credential = token();
        if (!string.IsNullOrWhiteSpace(credential) && request.RequestUri!.Host == "api.github.com") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }
    public async Task<PackManifest> FetchAsync(CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(RawUrl(config.Config.Repository, config.Config.Branch, ManifestName, !string.IsNullOrWhiteSpace(token())));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("No modpack version is published yet. The pack owner must use Modpack actions → Publish modpack version. No content was downloaded.");
        response.EnsureSuccessStatusCode();
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Pack manifest is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
        { if (data.Length + read > limit) throw new IOException("Pack manifest is too large."); data.Write(buffer, 0, read); }
        var manifest = JsonSerializer.Deserialize<PackManifest>(data.ToArray(), Json) ?? throw new IOException("Empty pack manifest.");
        Validate(manifest); return manifest;
    }
    public void Validate(PackManifest manifest)
    {
        if (manifest.Schema != 1 || !PackVersion.Valid(manifest.Version) || manifest.Files == null || manifest.Repositories == null || manifest.Files.Count > 20000)
            throw new IOException("Unsupported or invalid pack manifest.");
        if (!Regex.IsMatch(manifest.Minecraft ?? "", @"^[0-9]+\.[0-9]+(?:\.[0-9]+)?$") ||
            manifest.Fabric != null && !Regex.IsMatch(manifest.Fabric, @"^[0-9]+\.[0-9]+\.[0-9]+$")) throw new IOException("Invalid Minecraft or Fabric version.");
        if (config.Config.SyncFabricLoader && manifest.Fabric == null) throw new IOException("The published pack must declare its Fabric version when loader checking is enabled.");
        var repositories = new HashSet<string>();
        foreach (var repository in manifest.Repositories)
        {
            if (!repositories.Add(repository.Category) || repository.Category is not ("mods" or "resourcepacks" or "shaderpacks") ||
                !Regex.IsMatch(repository.Commit ?? "", "^[a-fA-F0-9]{40}$")) throw new IOException("Invalid repository snapshot.");
            var expected = RepositoryFor(repository.Category);
            if (repository.Url.TrimEnd('/') != expected.Url.TrimEnd('/') || repository.Branch != expected.Branch)
                throw new IOException("This published pack uses different repositories. Review repository settings before updating.");
            _ = RawUrl(repository.Url, repository.Commit!, "test");
        }
        if (!repositories.Contains("mods") || config.Config.SyncResourcePacks && !repositories.Contains("resourcepacks") ||
            config.Config.SyncShaderPacks && !repositories.Contains("shaderpacks")) throw new IOException("The manifest does not cover all enabled categories.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!repositories.Contains(file.Category) || !names.Add(file.Category + "/" + file.Path) ||
                file.Size < 0 || file.Size > 100L * 1024 * 1024 || !Regex.IsMatch(file.Sha256 ?? "", "^[a-fA-F0-9]{64}$")) throw new IOException("Invalid file entry or unsupported file size (maximum 100 MiB) in pack manifest.");
            SafeRelative(file.Path); SafeRelative(file.RepositoryPath);
            if (file.Category == "mods" && !file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                file.Category == "shaderpacks" && (!file.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.Path.Contains('/')) ||
                file.Category == "resourcepacks" && !file.Path.Contains('/') && !file.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Manifest contains an unsupported asset path.");
        }
        foreach (var path in names)
            for (int slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                if (names.Contains(path[..slash])) throw new IOException("Manifest contains conflicting file paths.");
        var packs = manifest.Files.Where(f => f.Category == "resourcepacks").Select(f => f.Path.Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (manifest.ResourcePackOrder != null && (manifest.ResourcePackOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.ResourcePackOrder.Count ||
            manifest.ResourcePackOrder.Any(p => !packs.Contains(PackSyncService.ValidatePackName(p))))) throw new IOException("Published resource pack order names missing or duplicate packs.");
        if (manifest.ActiveShader != null && !manifest.Files.Any(f => f.Category == "shaderpacks" && f.Path == PackSyncService.ValidatePackName(manifest.ActiveShader))) throw new IOException("Published active shader is missing.");
        if (JsonSerializer.SerializeToUtf8Bytes(manifest, Json).Length > 4 * 1024 * 1024) throw new IOException("Pack manifest exceeds the size limit.");
    }
    public static void SafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) || path.Split('/').Any(p =>
            p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))) throw new IOException("Unsafe manifest path.");
    }
    public (string Url, string Branch, string Folder) RepositoryFor(string category) => category switch
    {
        "mods" => (config.Config.Repository, config.Config.Branch, config.ResolvedRepositoryFolder),
        "resourcepacks" => (string.IsNullOrWhiteSpace(config.Config.ResourcePackRepository) ? config.Config.Repository : config.Config.ResourcePackRepository,
            string.IsNullOrWhiteSpace(config.Config.ResourcePackRepository) ? config.Config.Branch : config.Config.ResourcePackBranch, config.ResolvedResourcePackRepositoryFolder),
        "shaderpacks" => (string.IsNullOrWhiteSpace(config.Config.ShaderPackRepository) ? config.Config.Repository : config.Config.ShaderPackRepository,
            string.IsNullOrWhiteSpace(config.Config.ShaderPackRepository) ? config.Config.Branch : config.Config.ShaderPackBranch, config.ResolvedShaderPackRepositoryFolder),
        _ => throw new IOException("Unknown content category.")
    };
    private bool Enabled(string category, SyncScope scope) => category switch
    {
        "mods" => scope.HasFlag(SyncScope.Mods), "resourcepacks" => config.Config.SyncResourcePacks && scope.HasFlag(SyncScope.ResourcePacks),
        "shaderpacks" => config.Config.SyncShaderPacks && scope.HasFlag(SyncScope.Shaders), _ => false
    };
    private string Destination(PackFile file)
    {
        SafeRelative(file.Path);
        string root = file.Category switch { "mods" => config.ResolvedModsFolder, "resourcepacks" => config.ResolvedResourcePacksFolder, "shaderpacks" => config.ResolvedShaderPacksFolder, _ => throw new IOException("Unknown category.") };
        string result = Path.GetFullPath(Path.Combine(root, file.Path));
        for (string? path = result; path != null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked game folders are not supported.");
        return result;
    }
    private bool Ignored(PackFile file, string destination) => file.Category == "mods" && (ignores.IsIgnored(file.Path, destination) ||
        file.FabricId != null && ignores.GetEffectivePatterns().Contains("fabric-id:" + file.FabricId, StringComparer.Ordinal));
    public ManifestReview Plan(PackManifest manifest, SyncScope scope = SyncScope.All, bool full = false, CancellationToken cancellation = default)
    {
        Validate(manifest);
        var state = State; var summary = new SyncSummary();
        var cache = new LocalFingerprintCache(Path.Combine(Data, "fingerprints", config.InstanceStorageKey + ".json"));
        foreach (var file in manifest.Files.Where(f => Enabled(f.Category, scope)))
        {
            cancellation.ThrowIfCancellationRequested();
            string destination = Destination(file);
            bool ignored = Ignored(file, destination);
            var local = File.Exists(destination) ? new ModFileItem { FullPath = destination, RelativePath = file.Path, SizeBytes = new FileInfo(destination).Length,
                Sha256Hash = ignored ? "" : cache.Get(destination, file.Sha256.ToUpperInvariant(), full) } : null;
            summary.Changes.Add(new ModChange { RelativePath = file.Category == "mods" ? file.Path : file.Category + "/" + file.Path,
                DestinationPath = destination, SourceItem = new ModFileItem { FullPath = ContentPath(file), RelativePath = file.Path, SizeBytes = file.Size, Sha256Hash = file.Sha256.ToLowerInvariant() },
                TargetItem = local, Type = ignored ? ChangeType.Ignored : local == null ? ChangeType.Added :
                    local.Sha256Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) ? ChangeType.Unchanged : ChangeType.Updated });
        }
        var desired = manifest.Files.Select(f => f.Category + "/" + f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in state.Managed.Where(f => Enabled(f.Category, scope) && !desired.Contains(f.Category + "/" + f.Path)))
        {
            cancellation.ThrowIfCancellationRequested(); string destination = Destination(old);
            if (!File.Exists(destination) || Ignored(old, destination)) continue;
            summary.Changes.Add(new ModChange { RelativePath = old.Category == "mods" ? old.Path : old.Category + "/" + old.Path,
                DestinationPath = destination, TargetItem = new ModFileItem { FullPath = destination, RelativePath = old.Path, SizeBytes = new FileInfo(destination).Length,
                    Sha256Hash = cache.Get(destination, old.Sha256.ToUpperInvariant(), full) }, Type = ChangeType.Removed });
        }
        PlanSettings(manifest, state, summary, scope); cache.Save();
        return new(manifest, summary, JsonSerializer.Serialize(config.Config), scope);
    }
    private void PlanSettings(PackManifest manifest, InstalledPackState state, SyncSummary summary, SyncScope scope)
    {
        void Text(string relative, string content, string label)
        {
            string path = Path.Combine(config.MinecraftFolder, relative);
            for (string? parent = path; parent != null; parent = Path.GetDirectoryName(parent))
                if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked settings are not supported.");
            string? old = File.Exists(path) ? File.ReadAllText(path) : null;
            if (old != content) summary.Changes.Add(new ModChange { RelativePath = label, DestinationPath = path, OriginalContent = old, NewContent = content, Type = old == null ? ChangeType.Added : ChangeType.Updated });
        }
        if (config.Config.SyncResourcePacks && (scope.HasFlag(SyncScope.ResourcePacks) || scope == SyncScope.ResourcePackOrder) && manifest.ResourcePackOrder != null)
        {
            var names = manifest.Files.Where(f => f.Category == "resourcepacks").Select(f => f.Path.Split('/')[0]).Distinct().ToArray();
            if (scope == SyncScope.ResourcePackOrder && names.Any(n => !File.Exists(Path.Combine(config.ResolvedResourcePacksFolder, n)) && !Directory.Exists(Path.Combine(config.ResolvedResourcePacksFolder, n))))
                throw new IOException("Install resource packs before applying their order.");
            string options = Path.Combine(config.MinecraftFolder, "options.txt");
            Text("options.txt", PackSyncService.ApplyPackOrder(File.Exists(options) ? File.ReadAllText(options) : "", manifest.ResourcePackOrder, names,
                state.Managed.Where(f => f.Category == "resourcepacks").Select(f => f.Path.Split('/')[0])), "options.txt (resource pack order)");
        }
        if (Enabled("shaderpacks", scope) && config.Config.EnforceActiveShader && manifest.ActiveShader != null)
        {
            string shader = PackSyncService.ValidatePackName(manifest.ActiveShader);
            if (!manifest.Files.Any(f => f.Category == "shaderpacks" && f.Path == shader)) throw new IOException("Published active shader is missing.");
            string iris = Path.Combine(config.MinecraftFolder, "config", "iris.properties");
            bool usesIris = File.Exists(iris) || !File.Exists(Path.Combine(config.MinecraftFolder, "optionsshaders.txt")) || manifest.Files.Any(f => f.Category == "mods" && f.Path.Contains("iris", StringComparison.OrdinalIgnoreCase)) ||
                Directory.Exists(config.ResolvedModsFolder) && Directory.EnumerateFiles(config.ResolvedModsFolder, "*.jar").Any(p => Path.GetFileName(p).Contains("iris", StringComparison.OrdinalIgnoreCase));
            string relative = usesIris ? "config/iris.properties" : "optionsshaders.txt";
            string path = Path.Combine(config.MinecraftFolder, relative);
            string content = PackSyncService.ReplaceSetting(File.Exists(path) ? File.ReadAllText(path) : "", "shaderPack", PackSyncService.EscapeProperty(shader), '=');
            if (usesIris) content = PackSyncService.ReplaceSetting(content, "enableShaders", "true", '=');
            Text(relative, content, relative + " (active shader)");
        }
    }
    private static bool Cached(ModFileItem file) => File.Exists(file.FullPath) && new FileInfo(file.FullPath).Length == file.SizeBytes && HashUtils.ComputeSha256(file.FullPath) == file.Sha256Hash;
    public long DownloadBytes(ManifestReview review) => review.Summary.Changes.Where(c => c.NewContent == null && c.Type is ChangeType.Added or ChangeType.Updated)
        .GroupBy(c => c.SourceItem!.Sha256Hash).Sum(g => Cached(g.First().SourceItem!) ? 0 : g.First().SourceItem!.SizeBytes);

    public async Task DownloadAsync(ManifestReview review, Action<SyncProgressInfo>? progress, CancellationToken cancellation)
    {
        long required = DownloadBytes(review) + review.Summary.Changes.Where(c => c.Type is ChangeType.Updated or ChangeType.Removed).Sum(c => c.TargetItem?.SizeBytes ?? 0);
        long largest = review.Summary.Changes.Max(c => (long?)c.SourceItem?.SizeBytes) ?? 0;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Data))!);
        if (drive.AvailableFreeSpace < required + largest) throw new IOException("Not enough disk space for downloads, backups, and temporary files.");
        foreach (var change in review.Summary.Changes.Where(c => c.NewContent == null && c.Type is ChangeType.Added or ChangeType.Updated).GroupBy(c => c.SourceItem!.FullPath).Select(g => g.First()))
        {
            cancellation.ThrowIfCancellationRequested(); var source = change.SourceItem!;
            if (Cached(source)) continue;
            var file = review.Manifest.Files.First(f => ContentPath(f) == source.FullPath);
            var repository = review.Manifest.Repositories.Single(r => r.Category == file.Category);
            Directory.CreateDirectory(Path.GetDirectoryName(source.FullPath)!);
            string temporary = source.FullPath + ".part";
            using var request = Request(RawUrl(repository.Url, repository.Commit, file.RepositoryPath, !string.IsNullOrWhiteSpace(token())));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            try
            {
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] bytes = new byte[81920]; long received = 0; int count;
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation); idle.CancelAfter(TimeSpan.FromSeconds(30));
                        count = await input.ReadAsync(bytes, idle.Token); if (count == 0) break;
                        received += count; if (received > file.Size) throw new IOException("Download exceeded its published size.");
                        await output.WriteAsync(bytes.AsMemory(0, count), cancellation);
                        progress?.Invoke(SyncProgressInfo.Determinate("Downloading approved files…", 100.0 * received / Math.Max(1, file.Size), file.Path));
                    }
                }
                if (new FileInfo(temporary).Length != file.Size || !HashUtils.ComputeSha256(temporary).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Downloaded file failed checksum verification: " + file.Path);
                File.Move(temporary, source.FullPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public void BeginApply(ManifestReview review)
    {
        if (review.Configuration != JsonSerializer.Serialize(config.Config) || ModSyncService.PlanFingerprint(review.Summary) != ModSyncService.PlanFingerprint(Plan(review.Manifest, review.Scope, full: true).Summary))
            throw new IOException("Files or settings changed since review. Check again before applying.");
        Directory.CreateDirectory(Path.GetDirectoryName(Journal)!);
        File.WriteAllText(Journal, JsonSerializer.Serialize(review, Json));
        // Retain ownership for newly added files even if a later operation fails.
        var state = State; state.Version = null; state.ManifestHash = null;
        state.Managed = state.Managed.Concat(review.Manifest.Files.Where(f => Enabled(f.Category, review.Scope))).GroupBy(f => f.Category + "/" + f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        SaveState(state);
    }
    public void MarkVerified(PackManifest manifest)
    {
        var review = Plan(manifest, SyncScope.All, full: true);
        if (review.Summary.HasChanges) throw new IOException("The complete enabled modpack is not yet synchronized.");
        var state = State;
        state.Version = manifest.Version; state.ManifestHash = Digest(manifest); state.Policy = Policy;
        state.Managed = state.Managed.Where(f => !Enabled(f.Category, SyncScope.All)).Concat(manifest.Files.Where(f => Enabled(f.Category, SyncScope.All))).ToList();
        SaveState(state); if (File.Exists(Journal)) File.Delete(Journal);
    }
    private void SaveState(InstalledPackState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        string temp = StatePath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(state, Json)); File.Move(temp, StatePath, true);
    }
}
