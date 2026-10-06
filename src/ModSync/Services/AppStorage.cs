using System.Text.Json;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

public static class AppStorage
{
    public static void Initialize(string? appDirectory = null, string? legacyDirectory = null)
    {
        string root = appDirectory ?? PathUtils.GetAppDirectory();
        if (ConfigService.IsMinecraftDirectory(root))
            throw new IOException("Place ModSync in UltimMC\\ModSync, outside the Minecraft instance, then open it again.");
        Directory.CreateDirectory(root);
        PathUtils.DataDirectory = root;
        string target = Path.Combine(root, "config.json");
        string oldRoot = legacyDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModSync", PathUtils.InstanceKey(root));
        string legacy = Path.Combine(oldRoot, "config.json");
        if (!File.Exists(target) && File.Exists(legacy))
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(legacy), options)
                ?? throw new IOException("The existing configuration is empty.");
            config.ModsFolder = ConfigService.ResolveIntelligentModsFolder(config.ModsFolder);
            config.InstanceStorageId ??= PathUtils.InstanceKey(Path.GetDirectoryName(config.ModsFolder)!);
            config.ModsFolder = Path.GetRelativePath(root, config.ModsFolder);
            // Repository checkouts are disposable; do not migrate them from a game directory.
            config.RepositoryFolder = "repository";
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, options));
            File.Move(temporary, target, overwrite: false);
        }
        // Keep recovery information with the portable app. Never remove the original data.
        string marker = Path.Combine(root, ".portable-migration-complete");
        if (!File.Exists(marker))
        {
            foreach (var name in new[] { "backups", "pending-updates" })
                CopyMissing(Path.Combine(oldRoot, name), Path.Combine(root, name));
            File.WriteAllText(marker, "Recovery data imported. Original files retained.");
        }
    }

    private static void CopyMissing(string source, string target)
    {
        if (!Directory.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) return;
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && !File.Exists(Path.Combine(target, Path.GetFileName(file))))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyMissing(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    public static AppConfig? ImportMatchingInstance(string game, string destination, string? legacyRoot = null)
    {
        legacyRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModSync");
        if (!Directory.Exists(legacyRoot)) return null;
        var matches = new List<(AppConfig Config, string Folder)>();
        foreach (var directory in Directory.EnumerateDirectories(legacyRoot).Take(100))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            string file = Path.Combine(directory, "config.json");
            if (!File.Exists(file)) continue;
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                // Older AppData installations stored the selected instance as an absolute path.
                if (cfg != null && Path.IsPathRooted(cfg.ModsFolder) && PathUtils.InstanceKey(Path.GetDirectoryName(cfg.ModsFolder)!) == PathUtils.InstanceKey(game))
                    matches.Add((cfg, directory));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
        }
        // Ambiguous old installations must not silently replace the player's settings.
        if (matches.Count != 1) return null;
        var match = matches[0];
        string key = match.Config.InstanceStorageId ?? PathUtils.InstanceKey(game);
        match.Config.InstanceStorageId = key;
        match.Config.RepositoryFolder = "repository";
        string marker = Path.Combine(destination, ".imported-" + key);
        if (!File.Exists(marker))
        {
            CopyMissing(Path.Combine(match.Folder, "backups", key), Path.Combine(destination, "backups", key));
            string pending = Path.Combine(match.Folder, "pending-updates", key + ".txt");
            if (File.Exists(pending))
            {
                Directory.CreateDirectory(Path.Combine(destination, "pending-updates"));
                string target = Path.Combine(destination, "pending-updates", key + ".txt");
                if (!File.Exists(target)) File.Copy(pending, target);
            }
            File.WriteAllText(marker, "Imported matching instance settings; originals retained.");
        }
        return match.Config;
    }
}
