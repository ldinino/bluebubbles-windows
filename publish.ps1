<#
.SYNOPSIS
    Build a free, double-click installer for BlueBubbles (Windows) — no certificate, no MSIX.

.DESCRIPTION
    Publishes the app *unpackaged and self-contained* (the .NET + Windows App SDK runtimes are
    bundled, so the target machine needs nothing pre-installed), then wraps it in a single
    per-user Setup.exe with Inno Setup. The installer needs no admin rights and creates Start
    menu / desktop shortcuts and an uninstaller.

    Because the .exe is unsigned, the very first launch on another machine shows a one-time
    Microsoft Defender SmartScreen prompt ("More info" > "Run anyway"). That warning fades as the
    app builds reputation; eliminating it entirely requires code signing (see INSTALL.md).

    If Inno Setup (ISCC.exe) isn't found, the script still produces a portable .zip and explains
    how to get the installer (winget install JRSoftware.InnoSetup).

.PARAMETER Platform
    x64 (default) or arm64. Match the target machine.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER SkipPublish
    Reuse an existing publish folder (just rebuild the installer).
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root     = $PSScriptRoot
$proj     = Join-Path $root 'BlueBubbles.Windows\BlueBubbles.Windows.csproj'
$iss      = Join-Path $root 'installer\BlueBubbles.iss'
$distDir  = Join-Path $root 'dist'
$tfm      = 'net8.0-windows10.0.26100.0'
$rid      = "win-$Platform"
$pubDir   = Join-Path $root "BlueBubbles.Windows\bin\$Configuration\$tfm\$rid\publish"

function Write-Step([string]$m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok  ([string]$m) { Write-Host "    $m"   -ForegroundColor Green }
function Write-Fail([string]$m) { Write-Host "    $m"   -ForegroundColor Red }

# --- Version (single source of truth: csproj <Version>) ---
$version = (Select-Xml -Path $proj -XPath '/Project/PropertyGroup/Version' |
            Select-Object -First 1).Node.InnerText
if (-not $version) { Write-Fail 'Could not read <Version> from the csproj.'; exit 1 }
Write-Ok "Version: $version  Platform: $Platform"

# --- 1. Publish (unpackaged, self-contained) ---
if (-not $SkipPublish) {
    Write-Step "Publishing unpackaged self-contained app ($Configuration / $Platform)..."

    # Wipe the intermediate (obj) and build (bin) trees before publishing. The WinUI3 XAML
    # compiler's incremental build is unreliable: an edited .xaml often is NOT recompiled to its
    # embedded .xbf, so `dotnet publish` relinks the assembly with STALE compiled XAML while C#
    # changes flow through fine. The result is a release where UI/layout/animation fixes silently
    # don't ship even though other changes do. A from-scratch build is the only reliable guard.
    # (Both the platform-qualified `obj\x64\...` and the platform-neutral `obj\...` trees are
    # removed, since different invocations land in different schemes.)
    $projDir = Split-Path $proj -Parent
    foreach ($d in @('obj', 'bin')) {
        $path = Join-Path $projDir $d
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }

    & dotnet publish $proj -c $Configuration "-p:Platform=$Platform"
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Publish failed.'; exit $LASTEXITCODE }
}
if (-not (Test-Path (Join-Path $pubDir 'BlueBubbles.Windows.exe'))) {
    Write-Fail "Publish output not found at $pubDir"; exit 1
}
Write-Ok 'Publish complete.'

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# --- 2. Build the installer with Inno Setup (or fall back to a zip) ---
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source }

if ($iscc) {
    Write-Step 'Building installer (Inno Setup)...'
    & $iscc `
        "/DMyVersion=$version" `
        "/DMyArch=$Platform" `
        "/DMySourceDir=$pubDir" `
        "/DMyOutputDir=$distDir" `
        $iss
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Installer build failed.'; exit $LASTEXITCODE }

    $setup = Join-Path $distDir "BlueBubbles-Setup-$version-$Platform.exe"
    Write-Host ''
    Write-Ok 'Done.'
    Write-Host 'Installer : ' -NoNewline; Write-Host $setup -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Ship the Setup.exe (e.g. attach to a GitHub Release). Testers double-click it;' -ForegroundColor Cyan
    Write-Host 'no admin, no certificate. First launch shows a one-time SmartScreen "More info >' -ForegroundColor Cyan
    Write-Host 'Run anyway" prompt because the build is unsigned. See INSTALL.md.' -ForegroundColor Cyan
}
else {
    Write-Step 'Inno Setup not found — producing a portable .zip instead.'
    $zip = Join-Path $distDir "BlueBubbles-$version-$Platform-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $pubDir '*') -DestinationPath $zip
    Write-Host ''
    Write-Ok 'Done (portable zip).'
    Write-Host 'Portable : ' -NoNewline; Write-Host $zip -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'For a real double-click installer, install Inno Setup and re-run:' -ForegroundColor Cyan
    Write-Host '    winget install JRSoftware.InnoSetup' -ForegroundColor White
    Write-Host '    .\publish.ps1 -SkipPublish' -ForegroundColor White
}
