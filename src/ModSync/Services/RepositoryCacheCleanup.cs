using ModSync.Models;

namespace ModSync.Services;

/// <summary>Player workflows use installed files and manifests; only authoring needs temporary Git clones.</summary>
public sealed class RepositoryCacheCleanup(ConfigService config, IGitService git)
{
    public async Task ReleaseAsync(Action<string>? report = null)
    {
        if (config.Config.KeepRepositoryCopies) return;
        string root = Path.GetFullPath(Path.GetDirectoryName(config.ConfigFilePath)!);
        if (ConfigService.IsMinecraftDirectory(root) || !StorageMaintenance.IsUnlinked(root)) return;
        var selected = new RepositorySyncService(config, git).Selected(SyncScope.All);
        // Include disabled categories left over from older versions, but never scan arbitrary folders.
        var caches = selected.Select(r => r.Folder).Concat(new[] {
            config.ResolvedRepositoryFolder, config.ResolvedRepositoryFolder.TrimEnd('\\', '/') + "-resourcepacks",
            config.ResolvedRepositoryFolder.TrimEnd('\\', '/') + "-shaderpacks" }).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var cache in caches)
        {
            string folder = Path.GetFullPath(cache);
            if (!folder.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(Path.Combine(folder, ".git"))) continue;
            bool Overlaps(string path)
            {
                string local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                return folder.Equals(local, StringComparison.OrdinalIgnoreCase) ||
                    folder.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    local.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            if (new[] { config.MinecraftFolder, config.ResolvedModsFolder, config.ResolvedResourcePacksFolder, config.ResolvedShaderPacksFolder }.Any(Overlaps)) continue;
            // Never remove caches overlapping settings, hooks, recovery data, or an instance.
            if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(folder).Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(Path.Combine(folder, "saves")) || File.Exists(Path.Combine(folder, "options.txt")) ||
                new[] { "backups", "launch-hooks", "pack-state", "pending-updates", "content", "tools", "logs", "fingerprints", "instances", ".updates" }
                    .Contains(Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                if (!StorageMaintenance.IsUnlinked(folder) || !StorageMaintenance.IsUnlinked(Path.Combine(folder, ".git"))) continue;
                if (!await git.CanDiscardCacheAsync(folder))
                {
                    report?.Invoke("Repository files retained because their upload/cleanup safety could not be verified: " + folder);
                    continue;
                }
                StorageMaintenance.DeleteTree(root, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { report?.Invoke("Repository cleanup will retry: " + ex.Message); }
        }
    }
}
