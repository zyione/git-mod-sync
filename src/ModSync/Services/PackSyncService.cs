using System.Text.Json;
using System.Text.RegularExpressions;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>Plans visual asset and selective settings changes before any files are written.</summary>
public class PackSyncService
{
    private readonly ConfigService _config;
    public PackSyncService(ConfigService config) => _config = config;

    public static string RepositoryModsFolder(string repository)
    {
        bool structured = Directory.Exists(Path.Combine(repository, "mods")) ||
            (Directory.Exists(repository) && !Directory.GetFiles(repository, "*.jar").Any() &&
             (Directory.Exists(Path.Combine(repository, "resourcepacks")) || Directory.Exists(Path.Combine(repository, "shaderpacks"))));
        return structured ? Path.Combine(repository, "mods") : repository;
    }

    // Walk explicitly so junctions/symlinks cannot escape the managed directories.
    private static IEnumerable<string> SafeFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Linked asset directory is not supported: {directory}");
        foreach (var file in Directory.GetFiles(directory))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked asset file is not supported: {file}");
            yield return file;
        }
        foreach (var child in Directory.GetDirectories(directory))
            foreach (var file in SafeFiles(child)) yield return file;
    }

    public static Dictionary<string, ModFileItem> ScanPacks(string folder, bool resourcePacks)
    {
        var result = new Dictionary<string, ModFileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SafeFiles(folder))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var parts = relative.Split('/');
            bool included = parts.Length == 1
                ? Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                : resourcePacks && File.Exists(Path.Combine(folder, parts[0], "pack.mcmeta"));
            if (!included || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(relative, new ModFileItem { RelativePath = relative, FullPath = file,
                SizeBytes = new FileInfo(file).Length, Sha256Hash = HashUtils.ComputeSha256(file) });
        }
        return result;
    }

    public static string ValidatePackName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException($"Invalid pack name: {name}");
        return name;
    }

    public static string ReplaceSetting(string text, string key, string value, char separator)
    {
        var pattern = @"(?m)^" + Regex.Escape(key + separator) + @"[^\r\n]*";
        var line = key + separator + value;
        if (Regex.IsMatch(text, pattern)) return Regex.Replace(text, pattern, _ => line);
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        return text + (text.Length > 0 && !text.EndsWith('\n') ? newline : "") + line + newline;
    }

    public static string ApplyPackOrder(string options, IReadOnlyList<string> highestFirst,
        IEnumerable<string> repositoryNames, IEnumerable<string>? previouslyManaged = null)
    {
        var repo = repositoryNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = (previouslyManaged ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (highestFirst.Distinct(StringComparer.OrdinalIgnoreCase).Count() != highestFirst.Count)
            throw new InvalidDataException("resourcepack-order.txt contains duplicate packs.");
        foreach (var name in highestFirst)
            if (!repo.Contains(ValidatePackName(name)))
                throw new InvalidDataException($"Resource pack '{name}' is declared but missing from the repository.");
        var match = Regex.Match(options, @"(?m)^resourcePacks:([^\r\n]*)");
        var current = match.Success ? JsonSerializer.Deserialize<List<string>>(match.Groups[1].Value)
            ?? throw new InvalidDataException("resourcePacks must be a JSON array.") : new List<string> { "vanilla" };
        if (current.Any(x => x == null)) throw new InvalidDataException("resourcePacks contains a null entry.");
        var builtins = current.Where(x => !x.StartsWith("file/", StringComparison.Ordinal)).ToList();
        builtins.RemoveAll(x => x == "vanilla");
        builtins.Insert(0, "vanilla");
        var personal = current.Where(x => x.StartsWith("file/", StringComparison.Ordinal) &&
            !repo.Contains(x[5..]) && !previous.Contains(x[5..]));
        var packs = builtins.Concat(highestFirst.Reverse().Select(x => "file/" + x)).Concat(personal).Distinct();
        return ReplaceSetting(options, "resourcePacks", JsonSerializer.Serialize(packs), ':');
    }

    private static string[] ReadDeclaration(string path) => File.ReadAllLines(path)
        .Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).ToArray();

    private static void PlanText(SyncSummary summary, string path, string content, string label, bool isInternal = false)
    {
        EnsureNotLinked(path);
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        summary.Changes.Add(new ModChange { RelativePath = label, DestinationPath = path,
            NewContent = content, IsInternal = isInternal, Type = File.Exists(path) ? ChangeType.Updated : ChangeType.Added });
    }

    public SyncSummary Plan(bool push = false)
    {
        var summary = new SyncSummary();
        var cfg = _config.Config;
        var repository = _config.ResolvedRepositoryFolder;
        var manifestPath = Path.Combine(_config.MinecraftFolder, ".modsync-managed-packs.json");
        var previous = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(manifestPath)) ?? new()
            : new Dictionary<string, string[]>();
        var managed = new Dictionary<string, string[]>(previous);
        var resources = new Dictionary<string, ModFileItem>();
        var shaders = new Dictionary<string, ModFileItem>();
        foreach (var (kind, local, enabled, resource) in new[] {
            ("resourcepacks", _config.ResolvedResourcePacksFolder, cfg.SyncResourcePacks, true),
            ("shaderpacks", _config.ResolvedShaderPacksFolder, cfg.SyncShaderPacks, false) })
        {
            var remote = Path.Combine(repository, kind);
            if (!enabled || (!push && !Directory.Exists(remote))) continue;
            ValidateDestination(local, repository);
            var repoFiles = ScanPacks(remote, resource);
            var localFiles = ScanPacks(local, resource);
            if (resource) resources = repoFiles; else shaders = repoFiles;
            var source = push ? localFiles : repoFiles;
            var target = push ? repoFiles : localFiles;
            var targetFolder = push ? remote : local;
            var old = previous.GetValueOrDefault(kind, Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, file) in source)
            {
                target.TryGetValue(name, out var existing);
                summary.Changes.Add(new ModChange { RelativePath = kind + "/" + name,
                    SourceItem = file, TargetItem = existing, DestinationPath = Path.Combine(targetFolder, name),
                    Type = existing == null ? ChangeType.Added : existing.Sha256Hash == file.Sha256Hash
                        ? ChangeType.Unchanged : ChangeType.Updated });
            }
            foreach (var (name, file) in target)
                if (!source.ContainsKey(name) && (push || old.Contains(name)))
                    summary.Changes.Add(new ModChange { RelativePath = kind + "/" + name,
                        TargetItem = file, DestinationPath = file.FullPath, Type = ChangeType.Removed });
            managed[kind] = repoFiles.Keys.OrderBy(x => x).ToArray();
        }
        if (push) return summary; // declarations remain administrator-controlled repository metadata
        var orderFile = Path.Combine(repository, "resourcepack-order.txt");
        if (cfg.SyncResourcePacks && cfg.EnforcePackOrder && Directory.Exists(Path.Combine(repository, "resourcepacks")) && File.Exists(orderFile))
        {
            var options = Path.Combine(_config.MinecraftFolder, "options.txt");
            var names = resources.Keys.Select(x => x.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase);
            var old = previous.GetValueOrDefault("resourcepacks", Array.Empty<string>()).Select(x => x.Split('/')[0]);
            PlanText(summary, options, ApplyPackOrder(File.Exists(options) ? File.ReadAllText(options) : "",
                ReadDeclaration(orderFile), names, old), "options.txt (resource pack order)");
        }
        var shaderFile = Path.Combine(repository, "active-shader.txt");
        if (cfg.SyncShaderPacks && cfg.EnforceActiveShader && Directory.Exists(Path.Combine(repository, "shaderpacks")) && File.Exists(shaderFile))
        {
            var declaration = ReadDeclaration(shaderFile);
            if (declaration.Length != 1 || !shaders.ContainsKey(ValidatePackName(declaration[0])))
                throw new InvalidDataException("active-shader.txt must name one existing repository shader ZIP.");
            var iris = Path.Combine(_config.MinecraftFolder, "config", "iris.properties");
            var legacy = Path.Combine(_config.MinecraftFolder, "optionsshaders.txt");
            var repoMods = RepositoryModsFolder(repository);
            var mods = (Directory.Exists(_config.ResolvedModsFolder) ? Directory.GetFiles(_config.ResolvedModsFolder, "*.jar") : Array.Empty<string>())
                .Concat(Directory.Exists(repoMods) ? Directory.GetFiles(repoMods, "*.jar") : Array.Empty<string>());
            bool useLegacy = !File.Exists(iris) && File.Exists(legacy) && !mods.Any(x => Path.GetFileName(x).StartsWith("iris", StringComparison.OrdinalIgnoreCase));
            var path = useLegacy ? legacy : iris;
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            text = ReplaceSetting(text, "shaderPack", EscapeProperty(declaration[0]), '=');
            if (!useLegacy) text = ReplaceSetting(text, "enableShaders", "true", '=');
            PlanText(summary, path, text, useLegacy ? "optionsshaders.txt (active shader)" : "iris.properties (active shader)");
        }
        if (managed.Count > 0) PlanText(summary, manifestPath, JsonSerializer.Serialize(managed), "Pack tracking", isInternal: true);
        return summary;
    }

    private static string EscapeProperty(string value) => string.Concat(value.Select(c =>
        c > 127 ? "\\u" + ((int)c).ToString("x4") : c is '\\' or '=' or ':' or '#' or '!' or ' ' ? "\\" + c : c.ToString()));

    private void ValidateDestination(string path, string repository)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var protectedPath in new[] { repository, _config.ResolvedModsFolder, _config.MinecraftFolder })
        {
            var root = Path.GetFullPath(protectedPath).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (protectedPath != _config.MinecraftFolder && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Pack folders must be separate from mods, the instance root, and the internal repository.");
        }
        EnsureNotLinked(full);
        var resource = _config.ResolvedResourcePacksFolder.TrimEnd(Path.DirectorySeparatorChar);
        var shader = _config.ResolvedShaderPacksFolder.TrimEnd(Path.DirectorySeparatorChar);
        if (resource.Equals(shader, StringComparison.OrdinalIgnoreCase) ||
            resource.StartsWith(shader + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            shader.StartsWith(resource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resource packs and shaders must use separate folders.");
    }

    private static void EnsureNotLinked(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Linked settings file is not supported: {path}");
        for (var parent = new DirectoryInfo(path); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked destination is not supported: {parent.FullName}");
    }

    public string StatusText()
    {
        var repo = _config.ResolvedRepositoryFolder;
        static int Count(string path, bool resource) => ScanPacks(path, resource).Keys.Select(x => x.Split('/')[0]).Distinct().Count();
        var iris = Path.Combine(_config.MinecraftFolder, "config", "iris.properties");
        var legacy = Path.Combine(_config.MinecraftFolder, "optionsshaders.txt");
        var text = File.Exists(iris) ? File.ReadAllText(iris) : File.Exists(legacy) ? File.ReadAllText(legacy) : "";
        var active = Regex.Match(text, @"(?m)^shaderPack=([^\r\n]*)");
        return $"Resource packs: {Count(_config.ResolvedResourcePacksFolder, true)} local / {Count(Path.Combine(repo, "resourcepacks"), true)} repository\n" +
            $"Shaders: {Count(_config.ResolvedShaderPacksFolder, false)} local / {Count(Path.Combine(repo, "shaderpacks"), false)} repository • Active: {(active.Success ? active.Groups[1].Value : "None")}";
    }
}
