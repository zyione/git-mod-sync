using ModSync.Models;

namespace ModSync.Services;

public sealed record SyncRepository(string Url, string Branch, string Folder, SyncScope Scope, int PendingCommits = 0)
{
    public string Label => Scope == SyncScope.ResourcePacks ? "Resource Packs" : Scope == SyncScope.Shaders ? "Shaders" : Scope == SyncScope.Mods ? "Mods" : "Modpack";
}

/// <summary>Refresh independent clones concurrently; never write Minecraft files here.</summary>
public sealed class RepositorySyncService(ConfigService config, IGitService git)
{
    public static bool ValidBranch(string branch) => !string.IsNullOrWhiteSpace(branch) &&
        System.Text.RegularExpressions.Regex.IsMatch(branch, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$") &&
        !branch.Contains("..") && !branch.Contains("//") && !branch.EndsWith('/') && !branch.EndsWith('.') &&
        !branch.Split('/').Any(part => part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal));

    public IReadOnlyList<SyncRepository> Selected(SyncScope scope)
    {
        var cfg = config.Config;
        var repositories = new List<SyncRepository>();
        if (scope.HasFlag(SyncScope.Mods)) repositories.Add(new(cfg.Repository, cfg.Branch, config.ResolvedRepositoryFolder, SyncScope.Mods));
        if (scope.HasFlag(SyncScope.ResourcePacks) && cfg.SyncResourcePacks)
            repositories.Add(new(string.IsNullOrWhiteSpace(cfg.ResourcePackRepository) ? cfg.Repository : cfg.ResourcePackRepository,
                string.IsNullOrWhiteSpace(cfg.ResourcePackRepository) ? cfg.Branch : cfg.ResourcePackBranch, config.ResolvedResourcePackRepositoryFolder, SyncScope.ResourcePacks));
        if (scope.HasFlag(SyncScope.Shaders) && cfg.SyncShaderPacks)
            repositories.Add(new(string.IsNullOrWhiteSpace(cfg.ShaderPackRepository) ? cfg.Repository : cfg.ShaderPackRepository,
                string.IsNullOrWhiteSpace(cfg.ShaderPackRepository) ? cfg.Branch : cfg.ShaderPackBranch, config.ResolvedShaderPackRepositoryFolder, SyncScope.Shaders));
        return repositories.GroupBy(r => r.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Scope = group.Aggregate(SyncScope.None, (scope, repo) => scope | repo.Scope) }).ToArray();
    }

    public async Task<IReadOnlyList<SyncRepository>> PrepareAsync(SyncScope scope, bool download, string? token,
        Action<SyncProgressInfo>? progress = null, bool fetch = true)
    {
        var selected = Selected(scope);
        foreach (var repo in selected)
        {
            if (string.IsNullOrWhiteSpace(repo.Url) || !ValidBranch(repo.Branch)) throw new IOException($"{repo.Label}: invalid repository or branch configuration.");
            ValidateCache(repo.Folder);
        }
        // Portable Git provisioning touches one shared installation: finish that once first.
        if (selected.Count > 0 && !await git.EnsureGitAvailableAsync(progress)) throw new IOException("Git is unavailable.");
        var results = await Task.WhenAll(selected.Select(async repo =>
        {
            try
            {
                void Report(SyncProgressInfo info) => progress?.Invoke(new SyncProgressInfo {
                    Status = download ? "Downloading repository updates…" : "Checking repositories…",
                    Details = $"{repo.Label}: {info.Details ?? info.Status}", SpeedOrEta = info.SpeedOrEta });
                bool verified = await git.VerifyOrResetRemoteAsync(repo.Folder, repo.Url);
                if (!verified && Directory.Exists(Path.Combine(repo.Folder, ".git")))
                    throw new IOException("Could not verify the repository cache.");
                (bool Success, string? Error) result;
                if (!Directory.Exists(Path.Combine(repo.Folder, ".git")))
                    result = await git.CloneAsync(repo.Url, repo.Folder, repo.Branch, token, Report);
                else if (download) result = await git.PullOrResetToRemoteAsync(repo.Folder, repo.Branch, token, Report);
                else if (fetch) result = await git.FetchAsync(repo.Folder, repo.Branch, token, Report);
                else result = (true, null);
                return result.Success ? null : $"{repo.Label}: {result.Error ?? "Repository refresh failed."}";
            }
            catch (Exception ex) { return $"{repo.Label}: {ex.Message}"; }
        }));
        var errors = results.Where(error => error != null).ToArray();
        if (errors.Length > 0) throw new IOException(string.Join("\n", errors) + "\nNo Minecraft files were changed.");
        return selected;
    }

    private void ValidateCache(string folder)
    {
        string cache = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var path in new[] { config.ResolvedModsFolder, config.ResolvedResourcePacksFolder, config.ResolvedShaderPacksFolder, Path.Combine(config.MinecraftFolder, "modsync_backups") })
        {
            var local = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (cache.Equals(local, StringComparison.OrdinalIgnoreCase) || cache.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || local.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Repository caches must be separate from Minecraft asset folders.");
        }
        var instance = Path.GetFullPath(config.MinecraftFolder).TrimEnd(Path.DirectorySeparatorChar);
        if (cache.Equals(instance, StringComparison.OrdinalIgnoreCase) || instance.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Repository caches cannot contain the Minecraft instance.");
        for (var parent = new DirectoryInfo(cache); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked repository caches are not supported.");
    }
}
