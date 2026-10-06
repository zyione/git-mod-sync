using System.Text.Json;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

public sealed record PackPublication(PackManifest Manifest, string PublisherCommit);

/// <summary>Only explicit publishing downloads authoring caches and pushes a manifest; player checks never call this.</summary>
public sealed class PackPublisher(ConfigService config, IGitService git, PackManifestService manifests, Func<string?> token)
{
    public async Task<PackPublication> PrepareAsync(Action<SyncProgressInfo>? progress = null)
    {
        var repositories = new RepositorySyncService(config, git);
        var selected = await repositories.PrepareAsync(SyncScope.All, false, token(), progress, fetch: false);
        foreach (var repo in selected)
        {
            var status = await git.GetStatusAsync(repo.Folder, repo.Url, repo.Branch, token(), progress);
            if (!status.IsConnected || status.AheadCount > 0 || !(await git.SnapshotAsync(repo.Folder)).Clean)
                throw new IOException("Finish pending uploads and resolve repository cache changes before publishing a pack version.");
        }
        await repositories.PrepareAsync(SyncScope.All, true, token(), progress);
        var manifest = new PackManifest { Minecraft = config.Config.MinecraftVersion };
        string previous = Path.Combine(config.ResolvedRepositoryFolder, PackManifestService.ManifestName);
        if (File.Exists(previous))
        {
            var old = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(previous), PackManifestService.Json) ?? throw new IOException("Existing manifest is unreadable.");
            manifest.Version = PackVersion.Next(old.Version);
        }
        var packs = new PackSyncService(config);
        foreach (var category in new[] { "mods", "resourcepacks", "shaderpacks" })
        {
            if (category == "resourcepacks" && !config.Config.SyncResourcePacks || category == "shaderpacks" && !config.Config.SyncShaderPacks) continue;
            var repo = manifests.RepositoryFor(category);
            var snapshot = await git.SnapshotAsync(repo.Folder);
            if (!snapshot.Clean) throw new IOException("Repository cache changed while preparing publication.");
            manifest.Repositories.Add(new(category, repo.Url, repo.Branch, snapshot.Commit));
            string source = category == "mods" ? PackSyncService.RepositoryModsFolder(repo.Folder) : packs.AssetSourceFolder(category == "resourcepacks");
            IEnumerable<string> files = category == "mods" ? PackSyncService.SafeFiles(source, config.Config.SyncSubdirectories).Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                : PackSyncService.ScanPacks(source, category == "resourcepacks", hashContents: false).Values.Select(f => f.FullPath);
            foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                using (var stream = File.OpenRead(file))
                {
                    byte[] prefix = new byte[128]; int length = stream.Read(prefix, 0, prefix.Length);
                    if (System.Text.Encoding.UTF8.GetString(prefix, 0, length).StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal))
                        throw new IOException("Git LFS content is not supported by manifest publishing yet: " + Path.GetFileName(file));
                }
                manifest.Files.Add(new(category, Path.GetRelativePath(source, file).Replace('\\', '/'), Path.GetRelativePath(repo.Folder, file).Replace('\\', '/'),
                    new FileInfo(file).Length, HashUtils.ComputeSha256(file), category == "mods" ? ModIgnoreService.ReadFabricId(file) : null));
            }
        }
        string? Declaration(string folder, string name) => File.Exists(Path.Combine(folder, name)) ? File.ReadAllText(Path.Combine(folder, name)).Trim() : null;
        manifest.Fabric = Declaration(config.ResolvedRepositoryFolder, "fabric-version.txt") ?? config.Config.FabricLoaderVersion;
        manifest.Minecraft = Declaration(config.ResolvedRepositoryFolder, "minecraft-version.txt") ?? config.Config.MinecraftVersion;
        string? order = Declaration(config.ResolvedResourcePackRepositoryFolder, "resourcepack-order.txt");
        manifest.ResourcePackOrder = order?.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        manifest.ActiveShader = Declaration(config.ResolvedShaderPackRepositoryFolder, "active-shader.txt");
        manifests.Validate(manifest);
        foreach (var repository in manifest.Repositories)
        {
            var after = await git.SnapshotAsync(manifests.RepositoryFor(repository.Category).Folder);
            if (!after.Clean || after.Commit != repository.Commit) throw new IOException("Repository changed while preparing the manifest. Prepare again.");
        }
        return new(manifest, (await git.SnapshotAsync(config.ResolvedRepositoryFolder)).Commit);
    }
    public async Task PublishAsync(PackPublication publication, Action<SyncProgressInfo>? progress = null)
    {
        var snapshot = await git.SnapshotAsync(config.ResolvedRepositoryFolder);
        if (!snapshot.Clean || snapshot.Commit != publication.PublisherCommit) throw new IOException("Repository changed since review. Prepare publication again.");
        string path = Path.Combine(config.ResolvedRepositoryFolder, PackManifestService.ManifestName);
        File.WriteAllText(path, JsonSerializer.Serialize(publication.Manifest, PackManifestService.Json));
        var committed = await git.StageAndCommitAsync(config.ResolvedRepositoryFolder, "Publish modpack " + publication.Manifest.Version, progress, [PackManifestService.ManifestName]);
        if (!committed.Success) throw new IOException(committed.Error);
        var pushed = await git.PushAsync(config.ResolvedRepositoryFolder, config.Config.Branch, token(), progress);
        if (!pushed.Success) throw new IOException("Publication remains local. Retry the pending upload before publishing another version. " + pushed.Error);
    }
}
