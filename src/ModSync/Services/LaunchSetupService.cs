using System.Diagnostics;
using System.Text.Json;
using ModSync.Utils;

namespace ModSync.Services;

public sealed record LaunchSetupStatus(bool Supported, bool Installed, bool Healthy, string Message);

/// <summary>Only called to write after the player approves setup. All backups live beside ModSync.</summary>
public sealed class LaunchSetupService(string appDirectory, Func<bool>? launcherRunning = null)
{
    private static readonly string[] Keys = ["OverrideCommands", "OverrideLaunchCmd", "PreLaunchCommand", "WrapperCommand", "PostExitCommand"];
    public sealed class Installation
    {
        public Dictionary<string, string?> Original { get; set; } = new();
        public Dictionary<string, string?> Installed { get; set; } = new();
        public string PreviousCommand { get; set; } = "";
    }
    private string StateFolder(string game) => Path.Combine(appDirectory, "launch-hooks", PathUtils.PortableInstanceKey(game, appDirectory));
    public static string? InstanceConfig(string game)
    {
        string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(game))!, "instance.cfg");
        return File.Exists(file) ? file : null;
    }
    public static string? FindLauncher(string app)
    {
        foreach (string candidate in new[] { app, Path.GetDirectoryName(Path.GetFullPath(app).TrimEnd('\\', '/'))! })
            if (File.Exists(Path.Combine(candidate, "UltimMC.exe"))) return candidate;
        return null;
    }
    public static string LauncherConfig(string root) => File.Exists(Path.Combine(root, "ultimmc.cfg"))
        ? Path.Combine(root, "ultimmc.cfg") : Path.Combine(root, "multimc.cfg");

    public LaunchSetupStatus Inspect(string game)
    {
        if (InstanceConfig(game) == null || FindLauncher(appDirectory) == null)
            return new(false, false, false, "Place ModSync in UltimMC\\ModSync and choose an UltimMC instance.");
        string stateFile = Path.Combine(StateFolder(game), "installation.json");
        if (!File.Exists(stateFile)) return new(true, false, false, "Check for updates before Minecraft starts.");
        try
        {
            var state = ReadState(game);
            var cfg = new UltimMcSettings(File.ReadAllText(InstanceConfig(game)!));
            bool healthy = state.Installed.All(p => cfg.Get(p.Key) == p.Value) &&
                File.Exists(Path.Combine(StateFolder(game), "prelaunch.ps1")) &&
                File.ReadAllText(Path.Combine(StateFolder(game), "prelaunch.ps1")) == Script(state.PreviousCommand);
            return new(true, true, healthy, healthy ? "Checks this instance whenever you press Launch." : "Launch check needs repair.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return new(true, true, false, "Setup could not be read. " + ex.Message); }
    }

    public void Enable(string game)
    {
        RequireClosed();
        string configPath = InstanceConfig(game) ?? throw new IOException("Choose an UltimMC instance first.");
        string root = FindLauncher(appDirectory) ?? throw new IOException("Place ModSync in UltimMC\\ModSync first.");
        string original = File.ReadAllText(configPath);
        var config = new UltimMcSettings(original);
        string globalPath = LauncherConfig(root);
        var global = new UltimMcSettings(File.Exists(globalPath) ? File.ReadAllText(globalPath) : "");
        string folder = StateFolder(game);
        Installation state;
        if (File.Exists(Path.Combine(folder, "installation.json")))
        {
            state = ReadState(game);
            // An external edit is not permission to discard a new user command.
            if (Keys.Any(k => config.Get(k) != state.Installed.GetValueOrDefault(k) && config.Get(k) != state.Original.GetValueOrDefault(k)))
                throw new IOException("Launcher commands were edited outside ModSync. Keep those edits or restore the saved instance.cfg backup before repairing.");
        }
        else
        {
            bool overrides = string.Equals(config.Get("OverrideCommands") ?? config.Get("OverrideLaunchCmd"), "true", StringComparison.OrdinalIgnoreCase);
            string Effective(string key) => (overrides ? config.Get(key) : global.Get(key)) ?? "";
            string previous = Effective("PreLaunchCommand");
            if (previous.Contains("ModSync.exe", StringComparison.OrdinalIgnoreCase) || previous.Contains("ultimmc-prelaunch.ps1", StringComparison.OrdinalIgnoreCase) || previous.Contains("launch-hooks/", StringComparison.OrdinalIgnoreCase) || previous.Contains("launch-hooks\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException("An existing ModSync launch command is already configured. Remove that old command in UltimMC, close the launcher, then enable this check to avoid running it twice.");
            state = new Installation { Original = Keys.ToDictionary(k => k, config.Get), PreviousCommand = previous };
            string relative = Path.GetRelativePath(Path.GetDirectoryName(configPath)!, Path.Combine(folder, "prelaunch.ps1")).Replace('\\', '/');
            if (relative.Contains('$') || relative.Contains('"')) throw new IOException("The ModSync folder name cannot contain a dollar sign or quote for launcher setup.");
            state.Installed = new() {
                ["OverrideCommands"] = "true", ["OverrideLaunchCmd"] = null,
                ["PreLaunchCommand"] = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"$INST_DIR/" + relative + "\"",
                ["WrapperCommand"] = Effective("WrapperCommand"), ["PostExitCommand"] = Effective("PostExitCommand") };
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "instance.cfg.backup"), original);
            Atomic(Path.Combine(folder, "installation.json"), JsonSerializer.Serialize(state));
        }
        Atomic(Path.Combine(folder, "prelaunch.ps1"), Script(state.PreviousCommand));
        RequireClosed();
        if (File.ReadAllText(configPath) != original) throw new IOException("UltimMC settings changed during setup. Try again after closing UltimMC.");
        Atomic(configPath, config.With(state.Installed));
    }

    public void Disable(string game)
    {
        RequireClosed();
        string path = InstanceConfig(game) ?? throw new IOException("Instance is missing.");
        var state = ReadState(game);
        string before = File.ReadAllText(path);
        var config = new UltimMcSettings(before);
        if (state.Installed.Any(p => config.Get(p.Key) != p.Value))
            throw new IOException("Launcher commands changed outside ModSync. Restore the saved backup or review the commands in UltimMC before disabling.");
        RequireClosed();
        if (File.ReadAllText(path) != before) throw new IOException("Launcher settings changed. Please retry.");
        Atomic(path, config.With(state.Original));
        File.Delete(Path.Combine(StateFolder(game), "installation.json"));
    }
    private Installation ReadState(string game) => JsonSerializer.Deserialize<Installation>(File.ReadAllText(Path.Combine(StateFolder(game), "installation.json")))
        ?? throw new IOException("Launch setup is empty.");
    private void RequireClosed()
    {
        bool running;
        if (launcherRunning != null) running = launcherRunning();
        else { var processes = Process.GetProcessesByName("UltimMC"); running = processes.Length > 0; foreach (var p in processes) p.Dispose(); }
        if (running) throw new IOException("Close UltimMC before changing launch setup, then try again. This prevents the launcher from overwriting your changes.");
    }
    private static void Atomic(string path, string text)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string Script(string previous)
    {
        using var stream = typeof(LaunchSetupService).Assembly.GetManifestResourceStream("ModSync.ManagedPrelaunch.ps1")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("__PREVIOUS_COMMAND__", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(previous)));
    }
}
