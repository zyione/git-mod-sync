using System.IO;
using System.Text.Json;

namespace ModSync.Services;

/// <summary>Stages an external helper which replaces the EXE with a recoverable backup.</summary>
public static class UpdateInstaller
{
    public static (string Script, string Instructions) Prepare(string target, string staged, int processId, string sha256,
        bool restart = true)
    {
        target = Path.GetFullPath(target);
        staged = Path.GetFullPath(staged);
        string folder = Path.GetDirectoryName(target)!;
        if (Path.GetDirectoryName(staged) != folder || target == staged || !File.Exists(target) ||
            !BinaryDelta.Hash(staged).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged update is not a verified sibling of the current executable.");
        string suffix = Guid.NewGuid().ToString("N");
        string script = Path.Combine(folder, "update-" + suffix + ".ps1");
        string instructions = Path.Combine(folder, "update-" + suffix + ".json");
        File.WriteAllText(instructions, JsonSerializer.Serialize(new { Target = target, Staged = staged,
            Backup = target + ".previous", Pid = processId, Sha256 = sha256, Restart = restart }));
        File.WriteAllText(script, Script);
        return (script, instructions);
    }

    public const string Script = """
param([Parameter(Mandatory=$true)][string]$Instructions)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $Instructions -Raw | ConvertFrom-Json
$target = [IO.Path]::GetFullPath($plan.Target)
$staged = [IO.Path]::GetFullPath($plan.Staged)
$backup = [IO.Path]::GetFullPath($plan.Backup)
$folder = [IO.Path]::GetDirectoryName($target)
$result = $target + '.update-result.json'
$replaced = $false
function Read-Sha256([string]$path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
try {
    if ([IO.Path]::GetDirectoryName($staged) -ne $folder -or $target -eq $staged -or $backup -ne ($target + '.previous')) {
        throw 'Invalid update destination.'
    }
    foreach ($path in @($target, $staged, $backup)) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked update paths are not supported.'
        }
    }
    if ((Read-Sha256 $staged) -ne $plan.Sha256) { throw 'Update checksum failed.' }
    if ($plan.Pid -gt 0) {
        $running = Get-Process -Id $plan.Pid -ErrorAction SilentlyContinue
        if ($running -and -not $running.WaitForExit(30000)) { throw 'ModSync is still running. Close it and retry the update.' }
    }
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            [IO.File]::Replace($staged, $target, $backup, $true)
            $replaced = $true
            break
        } catch [IO.IOException] { Start-Sleep -Milliseconds 500 }
    }
    if (-not $replaced) { throw 'Could not replace ModSync. The existing version was preserved.' }
    if ((Read-Sha256 $target) -ne $plan.Sha256) { throw 'Installed update checksum failed.' }
    if ($plan.Restart) {
        try { Start-Process -FilePath $target -WorkingDirectory $folder -WindowStyle Hidden }
        catch {
            [IO.File]::Replace($backup, $target, $staged, $true)
            $replaced = $false
            throw 'The new version could not start. The previous version was restored.'
        }
    }
    @{ Success = $true; Message = 'Update installed successfully.' } | ConvertTo-Json | Set-Content -LiteralPath $result
} catch {
    if ($replaced -and [IO.File]::Exists($backup)) {
        try { [IO.File]::Replace($backup, $target, $staged, $true) } catch { }
    }
    @{ Success = $false; Message = $_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $result
    if ($plan.Restart -and [IO.File]::Exists($target)) {
        try { Start-Process -FilePath $target -WorkingDirectory $folder -WindowStyle Hidden } catch { }
    }
} finally {
    Remove-Item -LiteralPath $Instructions -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSCommandPath -ErrorAction SilentlyContinue
}
""";
}
