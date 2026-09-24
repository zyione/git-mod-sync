# ==============================================================================
# ModSync 1-Click Release Publisher
# ==============================================================================
# Usage:
#   .\scripts\release.ps1
#   .\scripts\release.ps1 -Version "1.1.0" -Notes "Major update"
# ==============================================================================

param (
    [string]$Version = "",
    [string]$Notes = ""
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "             ModSync 1-Click Release Publisher            " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Resolve repository root
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path "$ScriptDir\.."
Set-Location $RepoRoot

# 2. Locate dotnet executable
$LocalDotnetDir = "$env:LocalAppData\Microsoft\dotnet"
$LocalDotnetExe = "$LocalDotnetDir\dotnet.exe"
if (Test-Path $LocalDotnetExe) {
    $env:PATH = "$LocalDotnetDir;$env:PATH"
    $DotnetCmd = $LocalDotnetExe
} elseif (Get-Command "dotnet" -ErrorAction SilentlyContinue) {
    $DotnetCmd = "dotnet"
} else {
    Write-Error ".NET SDK was not found on PATH or in $LocalDotnetExe. Please install .NET 8 SDK."
    exit 1
}

# 3. Read current version from src/ModSync/ModSync.csproj
$CsprojPath = "$RepoRoot\src\ModSync\ModSync.csproj"
if (-not (Test-Path $CsprojPath)) {
    Write-Error "Could not locate ModSync.csproj at $CsprojPath"
    exit 1
}

$CsprojContent = Get-Content $CsprojPath -Raw
$CurrentVersion = "1.0.0"
if ($CsprojContent -match '<Version>([^<]+)</Version>') {
    $CurrentVersion = $matches[1].Trim()
}

Write-Host "Current Version: " -NoNewline -ForegroundColor Gray
Write-Host $CurrentVersion -ForegroundColor Yellow

# Calculate suggested next patch version
$VersionParts = $CurrentVersion.Split('.')
$SuggestedVersion = $CurrentVersion
if ($VersionParts.Length -ge 3 -and [int]::TryParse($VersionParts[2], [ref]$null)) {
    $Patch = [int]$VersionParts[2] + 1
    $SuggestedVersion = "$($VersionParts[0]).$($VersionParts[1]).$Patch"
} elseif ($VersionParts.Length -eq 2) {
    $SuggestedVersion = "$CurrentVersion.1"
} else {
    $SuggestedVersion = "1.0.1"
}

# Prompt for version if not supplied
$NewVersion = $Version.Trim()
if ([string]::IsNullOrWhiteSpace($NewVersion)) {
    Write-Host "Enter new release version " -NoNewline
    Write-Host "[default: $SuggestedVersion]" -ForegroundColor Green -NoNewline
    $InputVersion = Read-Host ": "
    if ([string]::IsNullOrWhiteSpace($InputVersion)) {
        $NewVersion = $SuggestedVersion
    } else {
        $NewVersion = $InputVersion.Trim().TrimStart('v')
    }
} else {
    $NewVersion = $NewVersion.TrimStart('v')
}

# Prompt for release notes if not supplied
$ReleaseNotes = $Notes.Trim()
if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    Write-Host "Enter brief release notes (optional): " -NoNewline -ForegroundColor Gray
    $InputNotes = Read-Host
    if ([string]::IsNullOrWhiteSpace($InputNotes)) {
        $ReleaseNotes = "ModSync release v$NewVersion"
    } else {
        $ReleaseNotes = $InputNotes.Trim()
    }
}

Write-Host ""
Write-Host "--> Releasing version: " -NoNewline -ForegroundColor Cyan
Write-Host "v$NewVersion" -ForegroundColor Yellow
Write-Host "--> Release notes: " -NoNewline -ForegroundColor Cyan
Write-Host "$ReleaseNotes" -ForegroundColor Gray
Write-Host ""

# 4. Stop any local running ModSync.exe in publish/ so file is not locked
$RunningModSync = Get-Process -Name "ModSync" -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -like "*$RepoRoot\publish\*" } catch { $false }
}
if ($RunningModSync) {
    Write-Host "--> Closing running publish/ModSync.exe..." -ForegroundColor Yellow
    $RunningModSync | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

# 5. Update Version in ModSync.csproj
Write-Host "--> Updating version in ModSync.csproj..." -ForegroundColor Cyan
$UpdatedCsproj = [regex]::Replace($CsprojContent, '<Version>[^<]+</Version>', "<Version>$NewVersion</Version>")
$UpdatedCsproj = [regex]::Replace($UpdatedCsproj, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$NewVersion.0</AssemblyVersion>")
$UpdatedCsproj = [regex]::Replace($UpdatedCsproj, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$NewVersion.0</FileVersion>")
Set-Content -Path $CsprojPath -Value $UpdatedCsproj -Encoding UTF8

# 6. Run unit tests
Write-Host "--> Running test suite..." -ForegroundColor Cyan
& $DotnetCmd test "$RepoRoot\ModSync.sln" -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "Unit tests failed! Aborting release."
    exit 1
}
Write-Host "    Tests passed successfully." -ForegroundColor Green

# 7. Compile & Publish local single-file executable
Write-Host "--> Compiling single-file executable (publish/ModSync.exe)..." -ForegroundColor Cyan
& $DotnetCmd publish "$RepoRoot\src\ModSync\ModSync.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$RepoRoot\publish" `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed! Aborting release."
    exit 1
}

if (-not (Test-Path "$RepoRoot\publish\ModSync.exe")) {
    Write-Error "Output executable publish\ModSync.exe was not created."
    exit 1
}

$ExeSizeMb = [math]::Round(((Get-Item "$RepoRoot\publish\ModSync.exe").Length / 1MB), 2)
Write-Host "    Executable built successfully ($ExeSizeMb MB)." -ForegroundColor Green

# 8. Git Commit, Tag, and Push
Write-Host "--> Creating Git commit & tag..." -ForegroundColor Cyan
git add "$RepoRoot\src\ModSync\ModSync.csproj"
git commit -m "release: v$NewVersion - $ReleaseNotes" --allow-empty

# Create git tag
$TagName = "v$NewVersion"
$ExistingTag = git tag -l $TagName
if ($ExistingTag) {
    git tag -d $TagName | Out-Null
}
git tag -a $TagName -m "$ReleaseNotes"

Write-Host "--> Pushing commits and tag to GitHub..." -ForegroundColor Cyan
git push origin main --tags

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   SUCCESS! Tag $TagName pushed to GitHub!                " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "GitHub Actions is now automatically building & publishing:" -ForegroundColor Cyan
Write-Host "https://github.com/zyione/git-mod-sync/releases/tag/$TagName" -ForegroundColor Yellow
Write-Host ""
Write-Host "Once the workflow finishes (1-2 mins), any ModSync client" -ForegroundColor Gray
Write-Host "will instantly receive the update notification!" -ForegroundColor Gray
Write-Host ""
