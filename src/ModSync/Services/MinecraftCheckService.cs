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

                        // Direct Minecraft executables and launchers
                        if (string.Equals(processName, "MinecraftLauncher", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(processName, "Minecraft", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(processName, "PrismLauncher", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(processName, "MultiMC", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(processName, "ModrinthApp", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(processName, "CurseForge", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Warning($"Minecraft launcher process detected: {processName} (PID: {process.Id})");
                            return (true, $"Minecraft launcher: {processName}");
                        }

                        // For java / javaw, only flag if there is supporting evidence that it is Minecraft
                        if (string.Equals(processName, "javaw", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(processName, "java", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(windowTitle) &&
                                (windowTitle.Contains("MultiMC", StringComparison.OrdinalIgnoreCase) ||
                                 windowTitle.Contains("Prism", StringComparison.OrdinalIgnoreCase) ||
                                 windowTitle.Contains("Fabric", StringComparison.OrdinalIgnoreCase) ||
                                 windowTitle.Contains("Forge", StringComparison.OrdinalIgnoreCase) ||
                                 windowTitle.Contains("NeoForge", StringComparison.OrdinalIgnoreCase)))
                            {
                                _logger.Warning($"Minecraft Java process detected via window title '{windowTitle}' (PID {process.Id})");
                                return (true, $"Minecraft (Java): \"{windowTitle}\" (PID {process.Id})");
                            }

                            try
                            {
                                string? modulePath = process.MainModule?.FileName;
                                if (!string.IsNullOrWhiteSpace(modulePath) &&
                                    (modulePath.Contains(".minecraft", StringComparison.OrdinalIgnoreCase) ||
                                     modulePath.Contains("minecraft", StringComparison.OrdinalIgnoreCase) ||
                                     modulePath.Contains("prism", StringComparison.OrdinalIgnoreCase) ||
                                     modulePath.Contains("multimc", StringComparison.OrdinalIgnoreCase) ||
                                     modulePath.Contains("modrinth", StringComparison.OrdinalIgnoreCase) ||
                                     modulePath.Contains("curseforge", StringComparison.OrdinalIgnoreCase)))
                                {
                                    _logger.Warning($"Minecraft Java process detected via path: {modulePath} (PID {process.Id})");
                                    return (true, $"Minecraft Java runtime (PID {process.Id})");
                                }
                            }
                            catch
                            {
                                // Access denied reading MainModule, safe to ignore
                            }
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
