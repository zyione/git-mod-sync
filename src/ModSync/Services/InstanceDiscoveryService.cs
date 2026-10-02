using System.IO;
using System.Text.Json;

namespace ModSync.Services;

public sealed record MinecraftInstance(string Name, string Folder)
{
    public override string ToString() => $"{Name} — {Folder}";
}

/// <summary>Bounded, read-only discovery. Never recursively scans a user's disk.</summary>
public static class InstanceDiscoveryService
{
    public static bool IsInstance(string folder) => Directory.Exists(folder) &&
        (File.Exists(Path.Combine(folder, "options.txt")) || Directory.Exists(Path.Combine(folder, "saves")) ||
         File.Exists(Path.Combine(folder, "launcher_profiles.json")) ||
         Directory.Exists(Path.Combine(folder, "mods")) && (Directory.Exists(Path.Combine(folder, "config")) || Directory.Exists(Path.Combine(folder, "resourcepacks"))));

    public static IReadOnlyList<MinecraftInstance> Discover(string current, string configured, IEnumerable<string>? roots = null, string? launcherProfilesPath = null)
    {
        var found = new Dictionary<string, MinecraftInstance>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string name)
        {
            try { path = Path.GetFullPath(path); if (IsInstance(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) found.TryAdd(path, new(name, path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        Add(current, "Current folder"); Add(configured, "Configured instance");
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool inspectProfiles = roots == null || launcherProfilesPath != null;
        roots ??= new[] { Path.Combine(roaming, ".minecraft"), Path.Combine(roaming, "PrismLauncher", "instances"),
            Path.Combine(roaming, "MultiMC", "instances"), Path.Combine(roaming, "ModrinthApp", "profiles"),
            Path.Combine(user, "curseforge", "minecraft", "Instances"), Path.Combine(local, "curseforge", "minecraft", "Instances"),
            Path.Combine(current, "instances"), Path.Combine(current, "..", "instances") };
        // Official launcher profiles may use a custom game directory outside the default root.
        try
        {
            string profiles = launcherProfilesPath ?? Path.Combine(roaming, ".minecraft", "launcher_profiles.json");
            if (inspectProfiles && File.Exists(profiles) && new FileInfo(profiles).Length < 2_000_000)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(profiles));
                if (document.RootElement.TryGetProperty("profiles", out var entries))
                    foreach (var profile in entries.EnumerateObject())
                        if (profile.Value.TryGetProperty("gameDir", out var directory) && directory.ValueKind == JsonValueKind.String)
                            Add(directory.GetString()!, profile.Value.TryGetProperty("name", out var name) ? name.GetString() ?? profile.Name : profile.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        foreach (var root in roots)
        {
            Add(root, Path.GetFileName(root));
            try
            {
                if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var child in Directory.EnumerateDirectories(root).Take(100))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    Add(child, Path.GetFileName(child));
                    Add(Path.Combine(child, ".minecraft"), Path.GetFileName(child));
                    Add(Path.Combine(child, "minecraft"), Path.GetFileName(child));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found.Values.ToArray();
    }
}
