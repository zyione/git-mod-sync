using System.Text.Json;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>Verified, copy-only safety snapshots made before visual assets are changed.</summary>
public sealed class PackBackupService(ConfigService config)
{
    private string BackupRoot => config.BackupFolder;

    public string? SnapshotLoader(string? mmcPack)
    {
        var files = new Dictionary<string, string>();
        if (mmcPack != null && File.Exists(mmcPack))
        {
            files[mmcPack] = "loader/mmc-pack.json";
            string patches = Path.Combine(Path.GetDirectoryName(mmcPack)!, "patches");
            if (Directory.Exists(patches))
                foreach (var file in Directory.GetFiles(patches, "*.json")) files[file] = "loader/patches/" + Path.GetFileName(file);
        }
        var profiles = Path.Combine(config.MinecraftFolder, "launcher_profiles.json");
        if (File.Exists(profiles)) files[profiles] = "loader/launcher_profiles.json";
        return Snapshot(files, "loader");
    }

    public string? BackupChanges(SyncSummary summary)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in summary.Changes.Where(c => !c.IsInternal && c.Type is ChangeType.Updated or ChangeType.Removed))
        {
            if (change.DestinationPath == null)
            {
                if (change.TargetItem != null) files[change.TargetItem.FullPath] = "mods/" + change.RelativePath;
                continue;
            }
            if (change.RelativePath.StartsWith("resourcepacks/", StringComparison.Ordinal) ||
                change.RelativePath.StartsWith("shaderpacks/", StringComparison.Ordinal))
                files[change.DestinationPath] = change.RelativePath;
            else if (change.NewContent != null)
                files[change.DestinationPath] = SettingsLabel(change.DestinationPath);
        }
        // Detect edits since the preview before making a backup or writing any assets.
        foreach (var change in summary.Changes.Where(c => c.DestinationPath != null && files.ContainsKey(c.DestinationPath)))
        {
            if (change.TargetItem != null && HashUtils.ComputeSha256(change.DestinationPath!) != change.TargetItem.Sha256Hash)
                throw new IOException($"'{change.RelativePath}' changed while syncing. Retry the sync.");
            if (change.NewContent != null && File.ReadAllText(change.DestinationPath!) != change.OriginalContent)
                throw new IOException($"'{change.RelativePath}' changed while syncing. Your newer settings were preserved; retry the sync.");
        }
        foreach (var change in summary.Changes.Where(c => c.DestinationPath == null && c.TargetItem != null && files.ContainsKey(c.TargetItem.FullPath)))
            if (HashUtils.ComputeSha256(change.TargetItem!.FullPath) != change.TargetItem.Sha256Hash)
                throw new IOException($"'{change.RelativePath}' changed while syncing. Check again.");
        var tracking = Path.Combine(config.MinecraftFolder, ".modsync-managed-packs.json");
        if (files.Count > 0 && File.Exists(tracking)) files[tracking] = "settings/.modsync-managed-packs.json";
        return Snapshot(files, "sync");
    }

    public string SnapshotForReinstall(SyncScope scope = SyncScope.All)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, kind, enabled) in new[] {
            (config.ResolvedResourcePacksFolder, "resourcepacks", config.Config.SyncResourcePacks && scope.HasFlag(SyncScope.ResourcePacks)),
            (config.ResolvedShaderPacksFolder, "shaderpacks", config.Config.SyncShaderPacks && scope.HasFlag(SyncScope.Shaders)) })
            if (enabled)
                foreach (var file in PackSyncService.SafeFiles(folder))
                    files[file] = kind + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/');
        var settings = new List<string>();
        if (scope.HasFlag(SyncScope.ResourcePacks) && config.Config.SyncResourcePacks) settings.Add("options.txt");
        if (scope.HasFlag(SyncScope.Shaders) && config.Config.SyncShaderPacks) settings.AddRange(new[] { "config/iris.properties", "optionsshaders.txt" });
        if (settings.Count > 0) settings.Add(".modsync-managed-packs.json");
        foreach (var relative in settings)
        {
            var file = Path.Combine(config.MinecraftFolder, relative);
            if (File.Exists(file)) files[file] = "settings/" + relative;
        }
        return Snapshot(files, scope == SyncScope.All ? "reinstall" : "reinstall_" + scope.ToString().ToLowerInvariant(), createEmpty: true)!;
    }

    private string SettingsLabel(string file) => "settings/" + Path.GetRelativePath(config.MinecraftFolder, file).Replace('\\', '/');

    private string? Snapshot(Dictionary<string, string> files, string operation, bool createEmpty = false)
    {
        if (files.Count == 0 && !createEmpty) return null;
        // Check every existing ancestor before creating the snapshot destination.
        for (var parent = new DirectoryInfo(BackupRoot); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The backup location must not contain linked directories.");
        var folder = Path.Combine(BackupRoot, $"{operation}_{DateTime.Now:yyyy-MM-dd_HHmmss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        foreach (var (source, relative) in files)
        {
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(source)!); parent != null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked backup sources are not supported.");
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked backup sources are not supported.");
            string hash = HashUtils.ComputeSha256(source);
            var target = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            if (HashUtils.ComputeSha256(target) != hash || HashUtils.ComputeSha256(source) != hash)
                throw new IOException("Backup verification failed; no pack changes were applied.");
        }
        File.WriteAllText(Path.Combine(folder, "restore-paths.json"), JsonSerializer.Serialize(
            files.ToDictionary(pair => pair.Value, pair => pair.Key), new JsonSerializerOptions { WriteIndented = true }));
        return folder;
    }
}
