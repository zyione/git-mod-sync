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
    /// Formats transfer speed into a clean human-readable string (e.g. "3.4 MB/s").
    /// </summary>
    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "-- B/s";
        return $"{FormatFileSize((long)bytesPerSec)}/s";
    }

    /// <summary>
    /// Formats remaining duration into an ETA string (e.g. "ETA 4s", "ETA 1m 20s").
    /// </summary>
    public static string FormatEta(double secondsRemaining)
    {
        if (double.IsNaN(secondsRemaining) || double.IsInfinity(secondsRemaining) || secondsRemaining <= 0)
            return "ETA --";
        if (secondsRemaining < 1)
            return "ETA < 1s";
        if (secondsRemaining < 60)
            return $"ETA {(int)Math.Round(secondsRemaining)}s";
        if (secondsRemaining < 3600)
        {
            int mins = (int)(secondsRemaining / 60);
            int secs = (int)(secondsRemaining % 60);
            return secs > 0 ? $"ETA {mins}m {secs}s" : $"ETA {mins}m";
        }
        int hours = (int)(secondsRemaining / 3600);
        int remMins = (int)((secondsRemaining % 3600) / 60);
        return remMins > 0 ? $"ETA {hours}h {remMins}m" : $"ETA {hours}h";
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
