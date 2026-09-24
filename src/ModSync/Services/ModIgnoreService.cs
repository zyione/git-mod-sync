using System.Text.RegularExpressions;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>
/// Service that manages ignored and excluded mod rules.
/// Prevents personal local mods from being deleted during sync,
/// and prevents unwanted repository mods from being downloaded or pushed.
/// Supports exact names, wildcard patterns (* and ?), and .modignore file rules.
/// </summary>
public class ModIgnoreService
{
    private readonly ConfigService _configService;
    private readonly LoggingService _logger;

    public ModIgnoreService(ConfigService configService, LoggingService logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the combined list of active ignore patterns from config.json
    /// and any local .modignore / modsync.ignore files.
    /// </summary>
    public List<string> GetEffectivePatterns()
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. From config.json
        if (_configService.Config.IgnoredMods != null)
        {
            foreach (var p in _configService.Config.IgnoredMods)
            {
                string trimmed = p.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#"))
                {
                    patterns.Add(trimmed);
                }
            }
        }

        // 2. From modsFolder/.modignore or appDir/.modignore if exists
        try
        {
            string modsDir = _configService.ResolvedModsFolder;
            string modIgnoreFileInMods = Path.Combine(modsDir, ".modignore");
            string modIgnoreFileInApp = Path.Combine(PathUtils.GetAppDirectory(), ".modignore");

            LoadPatternsFromFile(modIgnoreFileInMods, patterns);
            LoadPatternsFromFile(modIgnoreFileInApp, patterns);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not check .modignore file: {ex.Message}");
        }

        return patterns.ToList();
    }

    private static void LoadPatternsFromFile(string filePath, HashSet<string> patterns)
    {
        if (File.Exists(filePath))
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                string trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("//"))
                {
                    patterns.Add(trimmed);
                }
            }
        }
    }

    /// <summary>
    /// Checks whether a mod file path matches any active ignore rule.
    /// </summary>
    public bool IsIgnored(string relativeOrFileName)
    {
        var patterns = GetEffectivePatterns();
        return IsIgnored(relativeOrFileName, patterns);
    }

    /// <summary>
    /// Evaluates if a mod path matches any pattern in the provided list.
    /// </summary>
    public static bool IsIgnored(string relativeOrFileName, IEnumerable<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(relativeOrFileName))
            return false;

        string normalizedPath = relativeOrFileName.Trim().Replace('\\', '/');
        string fileName = Path.GetFileName(normalizedPath);

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            string p = pattern.Trim().Replace('\\', '/');
            if (p.StartsWith("#") || p.StartsWith("//"))
                continue;

            // 1. Exact match on filename or path
            if (string.Equals(fileName, p, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedPath, p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. Exact match without extension (e.g. pattern "optifine" matches "optifine.jar")
            if (!p.Contains('.') && (
                string.Equals(fileName, p + ".jar", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith(p + "-", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // 3. Wildcard / Glob pattern matching (* and ?)
            if (p.Contains('*') || p.Contains('?'))
            {
                try
                {
                    var regex = WildcardToRegex(p);
                    if (regex.IsMatch(fileName) || regex.IsMatch(normalizedPath))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Fallback to simple contains if regex fails
                    string search = p.Replace("*", "").Replace("?", "");
                    if (!string.IsNullOrEmpty(search) &&
                        (fileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                         normalizedPath.Contains(search, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Adds an ignore pattern to config.json and saves.
    /// </summary>
    public bool AddPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string clean = pattern.Trim();

        _configService.Config.IgnoredMods ??= new List<string>();

        if (!_configService.Config.IgnoredMods.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            _configService.Config.IgnoredMods.Add(clean);
            _logger.Info($"Added mod ignore pattern: '{clean}'");
            return _configService.Save();
        }

        return true;
    }

    /// <summary>
    /// Removes an ignore pattern from config.json and saves.
    /// </summary>
    public bool RemovePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || _configService.Config.IgnoredMods == null)
            return false;

        string clean = pattern.Trim();
        int removed = _configService.Config.IgnoredMods.RemoveAll(p => string.Equals(p.Trim(), clean, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            _logger.Info($"Removed mod ignore pattern: '{clean}'");
            return _configService.Save();
        }

        return false;
    }

    private static Regex WildcardToRegex(string pattern)
    {
        // Replace * with .* and ? with .
        string escaped = Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
