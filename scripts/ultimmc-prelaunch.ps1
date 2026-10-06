# Install beside ModSync.exe, outside the Minecraft instance.
# UltimMC supplies INST_MC_DIR to custom commands. A nonzero exit cancels launch.
param([string]$ModSyncPath = (Join-Path $PSScriptRoot 'ModSync.exe'))
$ErrorActionPreference = 'Stop'
try {
    if (-not $env:INST_MC_DIR -or -not (Test-Path -LiteralPath $env:INST_MC_DIR -PathType Container)) {
        throw 'UltimMC did not supply a valid Minecraft game folder.'
    }
    if (-not (Test-Path -LiteralPath $ModSyncPath -PathType Leaf)) { throw 'ModSync.exe was not found beside the launch script.' }
    # The broker returns as soon as the player chooses to continue, even if the dashboard stays open.
    $gameDirectory = [IO.Path]::GetFullPath($env:INST_MC_DIR).TrimEnd('\')
    if ($gameDirectory.Contains('"')) { throw 'Invalid game folder.' }
    $broker = Start-Process -FilePath $ModSyncPath -ArgumentList ('--pre-launch "' + $gameDirectory + '"') -Verb RunAs -WindowStyle Hidden -PassThru
    $broker.WaitForExit()
    exit $broker.ExitCode
} catch {
    Write-Error -Message ('ModSync launch check stopped: ' + $_.Exception.Message) -ErrorAction Continue
    exit 1
}
