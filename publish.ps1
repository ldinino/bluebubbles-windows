<#
.SYNOPSIS
    Build a free, double-click installer for BlueBubbles (Windows) - no certificate, no MSIX.

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
. (Join-Path $root 'build-common.ps1')   # Write-* helpers + Clear-BuildOutputs

$proj     = Join-Path $root 'BlueBubbles.Windows\BlueBubbles.Windows.csproj'
$iss      = Join-Path $root 'installer\BlueBubbles.iss'
$distDir  = Join-Path $root 'dist'
$tfm      = 'net8.0-windows10.0.26100.0'
$rid      = "win-$Platform"
$binDir   = Join-Path $root "BlueBubbles.Windows\bin"
# Expected publish dir. NOTE: whether MSBuild inserts a "$Platform" segment
# (bin\Release\... vs bin\x64\Release\...) depends on whether the win-<plat>.pubxml
# profile gets imported, which differs between a dev box and a clean CI runner. We
# resolve the *actual* publish dir after publishing (below) rather than trust this path.
$pubDir   = Join-Path $binDir "$Configuration\$tfm\$rid\publish"

# --- Version (single source of truth: csproj <Version>) ---
$version = (Select-Xml -Path $proj -XPath '/Project/PropertyGroup/Version' |
            Select-Object -First 1).Node.InnerText
if (-not $version) { Write-Fail 'Could not read <Version> from the csproj.'; exit 1 }
Write-Ok "Version: $version  Platform: $Platform"

# --- 1. Publish (unpackaged, self-contained) ---
if (-not $SkipPublish) {
    Write-Step "Publishing unpackaged self-contained app ($Configuration / $Platform)..."

    # Wipe obj/bin across the project graph before publishing. The WinUI3 XAML compiler's
    # incremental build is unreliable: an edited .xaml often is NOT recompiled to its embedded
    # .xbf, so `dotnet publish` relinks the assembly with STALE compiled XAML while C# changes
    # flow through fine - a release where UI/layout/animation fixes silently don't ship. A
    # from-scratch build is the only reliable guard. Clear-BuildOutputs (build-common.ps1) also
    # kills any running app and shuts the .NET build servers down first, so the delete doesn't
    # fail with "directory is not empty" on locked handles. Same clean as build-and-run.ps1.
    Clear-BuildOutputs @(
        (Join-Path $root 'BlueBubbles.Windows'),
        (Join-Path $root 'BlueBubbles.Core')
    )

    & dotnet publish $proj -c $Configuration "-p:Platform=$Platform"
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Publish failed.'; exit $LASTEXITCODE }
}
# Resolve the real publish dir: if the expected path has no .exe, find the publish
# folder for this RID anywhere under bin (covers the bin\$Platform\... layout a clean
# runner produces when the publish profile isn't imported).
if (-not (Test-Path (Join-Path $pubDir 'BlueBubbles.Windows.exe'))) {
    $found = Get-ChildItem -Path $binDir -Recurse -Filter 'BlueBubbles.Windows.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*\$rid\publish" } |
        Select-Object -First 1
    if ($found) {
        $pubDir = $found.DirectoryName
        Write-Warn "Publish output resolved to non-default path: $pubDir"
    }
}
if (-not (Test-Path (Join-Path $pubDir 'BlueBubbles.Windows.exe'))) {
    Write-Fail "Publish output not found under $binDir (looked for *\$rid\publish\BlueBubbles.Windows.exe)"; exit 1
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
    Write-Step 'Inno Setup not found - producing a portable .zip instead.'
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
