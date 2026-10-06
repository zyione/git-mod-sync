using System.Text.Json;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>Used only for previews of a Git-verified cache. Apply always recomputes hashes.</summary>
public sealed class RepositoryHashCache
{
    public sealed record Entry(long Length, long Modified, string Hash);
    private readonly string _file;
    private readonly IReadOnlyList<(string Folder, string Commit)> _repositories;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Func<string, string> _compute;
    public RepositoryHashCache(string file, IReadOnlyList<(string Folder, string Commit)> repositories, Func<string, string>? compute = null)
    {
        _file = file; _repositories = repositories; _compute = compute ?? HashUtils.ComputeSha256;
        try { _entries = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _entries = new(); }
    }
    public string Get(string path)
    {
        var repo = _repositories.FirstOrDefault(r => Path.GetFullPath(path).StartsWith(Path.GetFullPath(r.Folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (repo.Folder == null) return _compute(path);
        string key = PathUtils.PortableInstanceKey(repo.Folder) + "/" + repo.Commit + "/" + Path.GetRelativePath(repo.Folder, path);
        var info = new FileInfo(path);
        if (_entries.TryGetValue(key, out var entry) && entry.Length == info.Length && entry.Modified == info.LastWriteTimeUtc.Ticks &&
            entry.Hash.Length == 64 && entry.Hash.All(Uri.IsHexDigit)) return entry.Hash;
        string hash = _compute(path);
        _entries[key] = new(info.Length, info.LastWriteTimeUtc.Ticks, hash);
        return hash;
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Optional cache; correctness never depends on saving it. */ }
    }
}
