using System.Text.RegularExpressions;

namespace ModSync.Services;

/// <summary>Removes only known portable-app artifacts, never instance content or repository data.</summary>
public static class StorageMaintenance
{
    public const string MigrationMarker = ".storage-cleanup-v1";

    public static void Run(string root, Action<string>? report = null)
    {
        root = Path.GetFullPath(root);
        if (ConfigService.IsMinecraftDirectory(root) || !IsUnlinked(root)) return;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { report?.Invoke("Storage cleanup will retry: " + ex.Message); }
        }

        // One-time upgrade policy: existing app-local backups are discarded. Future
        // explicitly requested reinstall backups are preserved on subsequent launches.
        string marker = Path.Combine(root, MigrationMarker);
        if (!File.Exists(marker)) Attempt(() =>
        {
            DeleteTree(root, Path.Combine(root, "backups"));
            File.WriteAllText(marker, "Old app-local backups removed; optional new backups are retained.");
        });

        foreach (var file in Directory.EnumerateFiles(root))
        {
            string name = Path.GetFileName(file);
            if (Regex.IsMatch(name, @"^ModSync\.before-[A-Za-z0-9._-]+\.exe$", RegexOptions.IgnoreCase))
                Attempt(() => DeleteFile(root, file));
        }
        string downloads = Path.Combine(root, ".updates");
        if (Directory.Exists(downloads) && IsUnlinked(downloads))
            foreach (var file in Directory.EnumerateFiles(downloads))
            {
                string name = Path.GetFileName(file);
                // Completed downloads have already been copied to the sibling staged EXE.
                // Keep recent partial downloads so interrupted updates can resume.
                if (Regex.IsMatch(name, @"^[a-f0-9]{64}\.(exe|delta)$", RegexOptions.IgnoreCase) ||
                    (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)))
                    Attempt(() => DeleteFile(root, file));
            }
        string logs = Path.Combine(root, "logs");
        if (Directory.Exists(logs) && IsUnlinked(logs))
            foreach (var file in Directory.EnumerateFiles(logs, "modsync-*.log"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                    Attempt(() => DeleteFile(root, file));
    }

    public static bool IsUnlinked(string path)
    {
        for (var entry = new DirectoryInfo(Path.GetFullPath(path)); entry != null; entry = entry.Parent)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }

    public static void DeleteFile(string root, string path)
    {
        EnsureWithin(root, path);
        if (!IsUnlinked(path)) throw new IOException("Linked cleanup paths are not supported.");
        if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
    }

    public static void DeleteTree(string root, string path)
    {
        EnsureWithin(root, path);
        if (!IsUnlinked(path)) throw new IOException("Linked cleanup paths are not supported.");
        if (!Directory.Exists(path)) return;
        // Validate the entire tree before deleting anything, without traversing junctions.
        var files = new List<string>();
        var directories = new List<string>();
        void Inspect(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked cleanup paths are not supported.");
                if (Directory.Exists(entry)) Inspect(entry); else files.Add(entry);
            }
            directories.Add(directory);
        }
        Inspect(path);
        foreach (var file in files) DeleteFile(root, file);
        foreach (var directory in directories) Directory.Delete(directory);
    }

    private static void EnsureWithin(string root, string path)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup destination is outside its storage folder.");
    }
}
