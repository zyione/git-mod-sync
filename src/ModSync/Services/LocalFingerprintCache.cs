using System.Text.Json;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>Fast checks reuse hashes only when both local metadata and the published expectation match.</summary>
public sealed class LocalFingerprintCache
{
    public sealed record Entry(long Size, long Modified, string Expected, string Actual);
    private readonly string _path;
    private readonly Func<string, string> _hash;
    private readonly Dictionary<string, Entry> _entries;
    public LocalFingerprintCache(string path, Func<string, string>? hash = null)
    {
        _path = path; _hash = hash ?? HashUtils.ComputeSha256;
        try { _entries = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { _entries = new(); }
    }
    public string Get(string file, string expected, bool full)
    {
        var info = new FileInfo(file);
        if (!full && _entries.TryGetValue(file, out var entry) && entry != null && entry.Size == info.Length &&
            entry.Modified == info.LastWriteTimeUtc.Ticks && entry.Expected == expected && entry.Actual is { Length: 64 }) return entry.Actual;
        long size = info.Length, modified = info.LastWriteTimeUtc.Ticks;
        string actual = _hash(file);
        info.Refresh();
        if (size != info.Length || modified != info.LastWriteTimeUtc.Ticks) throw new IOException("File changed while being checked: " + Path.GetFileName(file));
        _entries[file] = new(info.Length, info.LastWriteTimeUtc.Ticks, expected, actual);
        return actual;
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_entries)); File.Move(temporary, _path, true);
    }
}
