using ModSync.Utils;

namespace ModSync.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// Thread-safe file logger writing to logs/modsync-YYYY-MM-DD.log.
/// Logs are saved relative to ModSync.exe directory.
/// </summary>
public class LoggingService
{
    private readonly string _logsDirectory;
    private readonly object _lock = new();

    public LoggingService()
    {
        _logsDirectory = Path.Combine(PathUtils.GetAppDirectory(), "logs");
        PathUtils.EnsureDirectoryExists(_logsDirectory);
    }

    private string GetCurrentLogFilePath()
    {
        string fileName = $"modsync-{DateTime.Now:yyyy-MM-dd}.log";
        return Path.Combine(_logsDirectory, fileName);
    }

    public void Log(LogLevel level, string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string entry = $"[{timestamp}] [{level.ToString().ToUpperInvariant()}] {Sanitize(message)}";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(GetCurrentLogFilePath(), entry + Environment.NewLine);
            }
            catch
            {
                // Never crash the app due to logging failure
            }
        }
    }

    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Debug(string message) => Log(LogLevel.Debug, message);

    public void Error(string message, Exception ex)
    {
        Log(LogLevel.Error, $"{message} - {ex.GetType().Name}: {ex.Message}");
        Log(LogLevel.Debug, ex.StackTrace ?? string.Empty);
    }

    /// <summary>
    /// Scrub potential tokens, passwords or sensitive query parameters from log strings.
    /// </summary>
    private static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // Basic mask for token-like substrings (e.g. ghp_...)
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            input,
            @"(ghp_[a-zA-Z0-9]{36}|github_pat_[a-zA-Z0-9_]{60,})",
            "***REDACTED***"
        );

        // Mask basic auth in URLs (https://user:pass@github.com)
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(https?://)([^:@\s]+):([^@\s]+)@",
            "$1***:***@"
        );

        return sanitized;
    }
}
