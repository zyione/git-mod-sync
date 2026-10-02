param(
    [Parameter(Mandatory=$true)][string]$Repository,
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$PublishDirectory = './publish',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = [IO.Path]::GetFullPath($PublishDirectory)
$target = Join-Path $publish 'ModSync.exe'
$tool = Join-Path $root 'build/DeltaBuilder/DeltaBuilder.csproj'
& $Dotnet build $tool -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'DeltaBuilder compilation failed.' }
$builder = Join-Path $root 'build/DeltaBuilder/bin/Release/net8.0/DeltaBuilder.dll'
$json = & gh api "repos/$Repository/releases?per_page=20"
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect previous releases for patches.' }
$prior = @($json | ConvertFrom-Json) | Where-Object {
    -not $_.draft -and -not $_.prerelease -and $_.tag_name -match '^v\d+\.\d+\.\d+$' -and
    [version]$_.tag_name.Substring(1) -lt [version]$Version
} | Sort-Object { [version]$_.tag_name.Substring(1) } -Descending | Select-Object -First 3
$basisFolder = Join-Path $publish 'delta-bases'
New-Item -ItemType Directory -Path $basisFolder -Force | Out-Null
foreach ($release in $prior) {
    $asset = $release.assets | Where-Object name -eq 'ModSync.exe' | Select-Object -First 1
    if (-not $asset -or $asset.digest -notmatch '^sha256:[a-fA-F0-9]{64}$') {
        Write-Host "Skipping $($release.tag_name): no verifiable base executable."
        continue
    }
    $basis = Join-Path $basisFolder ($release.tag_name + '.exe')
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $basis
    if ((Get-FileHash -LiteralPath $basis -Algorithm SHA256).Hash -ne $asset.digest.Substring(7)) {
        throw "Base checksum failed for $($release.tag_name)."
    }
    $patch = Join-Path $publish ('ModSync-from-' + $release.tag_name + '.delta')
    & $Dotnet $builder create $basis $target $patch
    if ($LASTEXITCODE -ne 0) { throw 'Delta generation failed.' }
    $reconstructed = Join-Path $basisFolder ($release.tag_name + '-verified.exe')
    & $Dotnet $builder apply $basis $patch $reconstructed
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $reconstructed).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
        throw 'Delta reconstruction did not produce the exact release executable.'
    }
    if ((Get-Item -LiteralPath $patch).Length -ge (Get-Item -LiteralPath $target).Length) {
        # Exact validated sibling output, never a computed recursive deletion.
        Remove-Item -LiteralPath $patch
        Write-Host "Full download is smaller than the patch for $($release.tag_name); omitting patch."
    }
}
"$((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant())  ModSync.exe" |
    Set-Content -LiteralPath (Join-Path $publish 'SHA256SUMS.txt')
