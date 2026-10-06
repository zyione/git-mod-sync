using System.Diagnostics;
using System.Text.Json;
using ModSync.Utils;

namespace ModSync.Services;

public sealed record RelocationLocation(string Source, string Launcher, string Instance)
{
    public string Destination => Path.Combine(Launcher, "ModSync", "ModSync.exe");
}

/// <summary>Only called after the player confirms relocation. Never moves game directories.</summary>
public static class AppRelocation
{
    public static RelocationLocation? Detect(string executable)
    {
        executable = Path.GetFullPath(executable);
        for (var directory = Directory.GetParent(executable); directory?.Parent?.Parent != null; directory = directory.Parent)
        {
            if (!directory.Parent.Name.Equals("instances", StringComparison.OrdinalIgnoreCase) ||
                !directory.Name.Replace(" ", "").Contains("BigChadGuys", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(directory.FullName, "instance.cfg"))) continue;
            string launcher = directory.Parent.Parent.FullName;
            if (!File.Exists(Path.Combine(launcher, "UltimMC.exe"))) return null;
            return new(executable, launcher, directory.FullName);
        }
        return null;
    }

    public static void ValidateDestination(string launcher)
    {
        if (!File.Exists(Path.Combine(launcher, "UltimMC.exe")) || !Directory.Exists(Path.Combine(launcher, "instances")))
            throw new IOException("Choose the UltimMC folder containing UltimMC.exe and instances.");
        CheckLinks(Path.Combine(launcher, "ModSync", "ModSync.exe"));
        for (var parent = new DirectoryInfo(Path.GetFullPath(launcher)); parent != null; parent = parent.Parent)
            if (parent.Name.Equals("instances", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(parent.FullName, "instance.cfg")))
                throw new IOException("Choose the launcher folder outside your instances.");
    }

    private static void CheckLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked folders are not supported for moving ModSync.");
    }

    public static (string Script, string Instructions) Prepare(RelocationLocation location, int processId, bool restart = true)
    {
        if (Detect(location.Source) is not { } detected || detected.Instance != location.Instance)
            throw new IOException("The original executable is no longer in the detected instance.");
        ValidateDestination(location.Launcher); CheckLinks(location.Source);
        string target = location.Destination;
        if (File.Exists(target)) throw new IOException("ModSync already exists in that folder. Open that copy instead; it has not been overwritten.");
        string folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        string id = Guid.NewGuid().ToString("N");
        string staged = Path.Combine(folder, "relocate-" + id + ".exe");
        string script = Path.Combine(folder, "relocate-" + id + ".ps1");
        string instructions = Path.Combine(folder, "relocate-" + id + ".json");
        string hash = BinaryDelta.Hash(location.Source);
        File.Copy(location.Source, staged, overwrite: false);
        if (BinaryDelta.Hash(staged) != hash) throw new IOException("The copied app could not be verified. The original is unchanged.");
        File.WriteAllText(instructions, JsonSerializer.Serialize(new { Source = location.Source, Target = target, Staged = staged, Sha256 = hash, Pid = processId, Restart = restart, Id = id }));
        File.WriteAllText(script, Script);
        return (script, instructions);
    }

    public static void Start((string Script, string Instructions) helper)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(helper.Script)! };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper.Script, "-Instructions", helper.Instructions }) start.ArgumentList.Add(argument);
        if (Process.Start(start) == null) throw new IOException("Could not start the move. The original app is unchanged.");
    }

    public const string Script = """
param([Parameter(Mandatory=$true)][string]$Instructions)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $Instructions -Raw | ConvertFrom-Json
$source = [IO.Path]::GetFullPath($plan.Source)
$target = [IO.Path]::GetFullPath($plan.Target)
$staged = [IO.Path]::GetFullPath($plan.Staged)
$folder = [IO.Path]::GetDirectoryName($target)
$ready = Join-Path $folder ('relocation-ready-' + $plan.Id)
function Read-Sha256([string]$path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
try {
    if ([IO.Path]::GetDirectoryName($staged) -ne $folder -or $source -eq $target -or [IO.Path]::GetFileName($target) -ne 'ModSync.exe') { throw 'Invalid relocation paths.' }
    foreach ($itemPath in @($source, $target, $staged)) {
        $checkPath = $itemPath
        while ($checkPath) {
            if ((Test-Path -LiteralPath $checkPath) -and ((Get-Item -LiteralPath $checkPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked relocation paths are not supported.' }
            $checkPath = [IO.Path]::GetDirectoryName($checkPath)
        }
    }
    if ($plan.Pid -gt 0) {
        $running = Get-Process -Id $plan.Pid -ErrorAction SilentlyContinue
        if ($running -and -not $running.WaitForExit(30000)) { throw 'Close the original ModSync and try again.' }
    }
    if ((Read-Sha256 $staged) -ne $plan.Sha256) { throw 'Copied app verification failed.' }
    # File.Move refuses to replace an existing installation, including one created after confirmation.
    [IO.File]::Move($staged, $target)
    if ($plan.Restart) {
        if ($plan.Id -notmatch '^[a-f0-9]{32}$') { throw 'Invalid relocation identifier.' }
        Remove-Item -LiteralPath $ready -ErrorAction SilentlyContinue
        $started = Start-Process -FilePath $target -ArgumentList @('--relocated', $plan.Id) -WorkingDirectory $folder -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not [IO.File]::Exists($ready)) {
            if ($started.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'The relocated app did not confirm startup. The original has been kept.' }
            Start-Sleep -Milliseconds 200
        }
    }
    if ((Read-Sha256 $source) -ne $plan.Sha256) { throw 'The original app changed. It has been kept.' }
    Remove-Item -LiteralPath $source
    @{ Success = $true; Message = 'ModSync moved. Review old shortcuts and launch-check setup.' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'relocation-result.json')
} catch {
    @{ Success = $false; Message = $_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'relocation-result.json')
    if ($plan.Restart) {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(('ModSync could not finish moving. Your original copy was kept.' + [Environment]::NewLine + $_.Exception.Message), 'ModSync') | Out-Null
    }
} finally {
    if ($plan.Id -match '^[a-f0-9]{32}$') { Remove-Item -LiteralPath $ready -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $staged -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Instructions -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSCommandPath -ErrorAction SilentlyContinue
}
""";
}
