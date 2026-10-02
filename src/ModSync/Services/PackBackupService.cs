using System.Text.Json;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>Verified, copy-only safety snapshots made before visual assets are changed.</summary>
public sealed class PackBackupService(ConfigService config)
{
    private string BackupRoot => Path.Combine(config.MinecraftFolder, "modsync_backups");

    public string? BackupChanges(SyncSummary summary)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in summary.Changes.Where(c => !c.IsInternal && c.Type is ChangeType.Updated or ChangeType.Removed))
        {
            if (change.DestinationPath == null) continue;
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
        var tracking = Path.Combine(config.MinecraftFolder, ".modsync-managed-packs.json");
        if (files.Count > 0 && File.Exists(tracking)) files[tracking] = "settings/.modsync-managed-packs.json";
        return Snapshot(files, "sync");
    }

    public string SnapshotForReinstall()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, kind, enabled) in new[] {
            (config.ResolvedResourcePacksFolder, "resourcepacks", config.Config.SyncResourcePacks),
            (config.ResolvedShaderPacksFolder, "shaderpacks", config.Config.SyncShaderPacks) })
            if (enabled)
                foreach (var file in PackSyncService.SafeFiles(folder))
                    files[file] = kind + "/" + Path.GetRelativePath(folder, file).Replace('\\', '/');
        foreach (var relative in new[] { "options.txt", "config/iris.properties", "optionsshaders.txt", ".modsync-managed-packs.json" })
        {
            var file = Path.Combine(config.MinecraftFolder, relative);
            if (File.Exists(file)) files[file] = "settings/" + relative;
        }
        return Snapshot(files, "reinstall", createEmpty: true)!;
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
