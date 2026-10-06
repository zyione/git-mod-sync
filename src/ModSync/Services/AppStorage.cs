using System.Text.Json;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

public static class AppStorage
{
    public static void Initialize()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModSync", PathUtils.InstanceKey(PathUtils.GetAppDirectory()));
        Directory.CreateDirectory(root);
        PathUtils.DataDirectory = root;
        string target = Path.Combine(root, "config.json");
        string legacy = Path.Combine(PathUtils.GetAppDirectory(), "config.json");
        if (!File.Exists(target) && File.Exists(legacy))
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(legacy), options)
                ?? throw new IOException("The existing configuration is empty.");
            config.ModsFolder = ConfigService.ResolveIntelligentModsFolder(config.ModsFolder);
            // Repository checkouts are disposable; do not migrate them from a game directory.
            config.RepositoryFolder = "repository";
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, options));
            File.Move(temporary, target, overwrite: false);
        }
    }
}
