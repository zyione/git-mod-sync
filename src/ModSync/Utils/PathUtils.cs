namespace ModSync.Utils;

/// <summary>
/// File and path resolution utilities.
/// Guarantees that relative paths in config are resolved against ModSync.exe's directory,
/// not the calling terminal or shortcut working directory.
/// </summary>
public static class PathUtils
{
    /// <summary>
    /// Gets the directory where the ModSync executable is located.
    /// </summary>
    public static string GetAppDirectory()
    {
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Resolves a path relative to the application executable directory.
    /// If the path is already rooted (absolute), it returns the normalized absolute path.
    /// </summary>
    public static string ResolveAppPath(string relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
            return GetAppDirectory();

        if (Path.IsPathRooted(relativeOrAbsolutePath))
            return Path.GetFullPath(relativeOrAbsolutePath);

        string combined = Path.Combine(GetAppDirectory(), relativeOrAbsolutePath);
        return Path.GetFullPath(combined);
    }

    /// <summary>
    /// Formats raw byte counts into human-readable strings (e.g. "24.5 MB", "512 KB").
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0) return "0 B";

        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;

        while (Math.Round(number / 1024m) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024m;
            counter++;
        }

        return $"{number:n1} {suffixes[counter]}";
    }

    /// <summary>
    /// Ensures that the specified directory exists, creating all parent directories if necessary.
    /// </summary>
    public static void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
}
