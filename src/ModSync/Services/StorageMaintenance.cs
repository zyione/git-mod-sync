using System.Text.RegularExpressions;
using System.Globalization;

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
        // The marker records the cutoff, not an instruction to skip future cleanup.
        // Old snapshots can reappear after restoring or copying an older app folder.
        if (File.Exists(marker) && IsUnlinked(marker))
            Attempt(() => SweepLegacyBackups(root, File.GetLastWriteTimeUtc(marker), report));

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
        Attempt(() => ClearContent(root));
    }

    private static void SweepLegacyBackups(string root, DateTime cutoffUtc, Action<string>? report)
    {
        string backups = Path.Combine(root, "backups");
        if (!Directory.Exists(backups) || !IsUnlinked(backups)) return;
        void Inspect(string parent, int depth)
        {
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                if (!IsUnlinked(directory)) continue;
                var match = Regex.Match(Path.GetFileName(directory), @"^(?:sync|loader|reinstall(?:_[a-z]+)?)_(\d{4}-\d{2}-\d{2}_\d{6})_", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    // Snapshot names use local time with one-second precision. The
                    // extra second protects new explicit backups near the cutoff.
                    if (DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeLocal, out var created) && created.ToUniversalTime().AddSeconds(1) <= cutoffUtc)
                    {
                        try { DeleteTree(root, directory); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { report?.Invoke("Old backup cleanup will retry: " + ex.Message); }
                    }
                }
                else if (depth > 0) Inspect(directory, depth - 1);
            }
        }
        Inspect(backups, 1); // Legacy flat snapshots and current per-instance snapshots.
    }

    /// <summary>Called after the current dashboard starts, so rollback files are no longer needed.</summary>
    public static void AfterHealthyStartup(string root, string executable, Action<string>? report = null)
    {
        root = Path.GetFullPath(root);
        executable = Path.GetFullPath(executable);
        if (ConfigService.IsMinecraftDirectory(root) || !IsUnlinked(root) || !File.Exists(executable) ||
            !string.Equals(Path.GetDirectoryName(executable), root, StringComparison.OrdinalIgnoreCase)) return;
        var helpers = Directory.EnumerateFiles(root).Where(file => Regex.IsMatch(Path.GetFileName(file),
            @"^(?:(?:update|relocate)-[a-f0-9]{32}\.(?:json|ps1|exe)|relocation-ready-[a-f0-9]{32}|update_modsync\.bat)$", RegexOptions.IgnoreCase)).ToArray();
        // An update helper can still be finishing after it starts the new EXE.
        // Do not remove its staging/rollback files while its instructions are recent.
        bool activeUpdate = helpers.Any(file => (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(file).Equals("update_modsync.bat", StringComparison.OrdinalIgnoreCase)) &&
            File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-10));
        var obsolete = helpers.Where(file => File.GetLastWriteTimeUtc(file) <= DateTime.UtcNow.AddMinutes(-10)).ToList();
        if (!activeUpdate) obsolete.AddRange(new[] { executable + ".previous", Path.Combine(root, "ModSync.exe.previous"), Path.Combine(root, "ModSync.update.exe") });
        foreach (var file in obsolete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (file.Equals(executable, StringComparison.OrdinalIgnoreCase)) continue;
            try { DeleteFile(root, file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { report?.Invoke("Old application file cleanup will retry: " + ex.Message); }
        }
    }

    public static void ClearContent(string root)
    {
        string content = Path.Combine(root, "content");
        if (!Directory.Exists(content) || !IsUnlinked(content)) return;
        foreach (var file in Directory.EnumerateFiles(content))
            if (Regex.IsMatch(Path.GetFileName(file), @"^[a-f0-9]{64}(\.part)?$", RegexOptions.IgnoreCase))
                DeleteFile(root, file);
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
