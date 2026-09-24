using System.Diagnostics;

namespace ModSync.Services;

/// <summary>
/// Best-effort detection service to check if Minecraft or its launchers are running
/// before modifying the local mods folder.
/// </summary>
public class MinecraftCheckService
{
    private readonly LoggingService _logger;

    public MinecraftCheckService(LoggingService logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks running processes to see if Minecraft or a launcher appears active.
    /// Returns (IsRunning, ProcessNameOrDetail).
    /// </summary>
    public (bool IsRunning, string? Details) CheckIfMinecraftRunning()
    {
        try
        {
            var targetProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "javaw",
                "java",
                "MinecraftLauncher",
                "Minecraft",
                "PrismLauncher",
                "MultiMC",
                "ModrinthApp",
                "CurseForge"
            };

            var processes = Process.GetProcesses();

            foreach (var process in processes)
            {
                try
                {
                    string processName = process.ProcessName;

                    if (targetProcessNames.Contains(processName))
                    {
                        string windowTitle = string.Empty;
                        try
                        {
                            windowTitle = process.MainWindowTitle;
                        }
                        catch { }

                        // If window title explicitly mentions Minecraft, high confidence
                        if (!string.IsNullOrWhiteSpace(windowTitle) &&
                            windowTitle.Contains("Minecraft", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Warning($"Minecraft detected running via window title: '{windowTitle}' (PID: {process.Id})");
                            return (true, $"Process '{processName}' with window: \"{windowTitle}\"");
                        }

                        // Direct Minecraft executables
                        if (string.Equals(processName, "MinecraftLauncher", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(processName, "Minecraft", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Warning($"Minecraft launcher process detected: {processName} (PID: {process.Id})");
                            return (true, $"Minecraft process: {processName}");
                        }

                        // For javaw running in background or without title, best-effort check
                        if (string.Equals(processName, "javaw", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Info($"javaw process detected (PID: {process.Id}, Title: '{windowTitle}')");
                            return (true, $"Java runtime: {processName} (PID {process.Id})");
                        }
                    }
                }
                catch
                {
                    // Ignore processes that cannot be accessed due to permissions
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error checking for Minecraft processes", ex);
        }

        return (false, null);
    }
}
