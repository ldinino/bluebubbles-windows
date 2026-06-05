<#
.SYNOPSIS
    Build, test, and run BlueBubbles WinUI3.

.DESCRIPTION
    Restores packages, builds the solution, runs unit tests, and launches the
    MSIX-packaged app via the WinApp debug identity flow (requires Developer
    Mode enabled in Windows Settings).

.PARAMETER Configuration
    Build configuration. Default: Debug.

.PARAMETER SkipTests
    Skip the test step.

.PARAMETER BuildOnly
    Build (and optionally test) without launching the app.

.PARAMETER Clean
    Wipe the obj/bin trees before building, instead of an incremental build. Use
    this whenever you've edited XAML: the WinUI3 XAML compiler's incremental build
    does NOT reliably recompile an edited .xaml into its embedded .xbf, so an
    incremental build can run with STALE compiled XAML (same failure publish.ps1
    guards against). A from-scratch build is the only reliable fix.

.PARAMETER Packaged
    Launch via the MSIX-packaged debug identity (requires Developer Mode). By
    default the app launches *unpackaged*, matching how publish.ps1 ships it, so
    identity-dependent code paths (toast activation, Package.Current, etc.) behave
    the same in debug as in the installed build.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipTests,
    [switch]$BuildOnly,
    [switch]$Clean,
    [switch]$Packaged
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$appProj   = Join-Path $root 'BlueBubbles.Windows\BlueBubbles.Windows.csproj'
$testProj  = Join-Path $root 'BlueBubbles.Windows.Tests\BlueBubbles.Windows.Tests.csproj'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "    $msg"  -ForegroundColor Red }

# --- Pre-flight: Developer Mode (only the packaged launch needs it) ---
if ($Packaged -and -not $BuildOnly) {
    $devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
        -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
    if ($devMode -ne 1) {
        Write-Fail 'Windows Developer Mode is not enabled (required for -Packaged launch).'
        Write-Fail 'Enable it in Settings > Privacy & Security > For developers.'
        exit 1
    }
    Write-Ok 'Developer Mode is enabled.'
}

# --- Restore ---
Write-Step 'Restoring NuGet packages...'
dotnet restore $appProj
if ($LASTEXITCODE -ne 0) { Write-Fail 'Restore failed.'; exit $LASTEXITCODE }
Write-Ok 'Restore succeeded.'

# --- Build ---
# On -Clean, wipe the intermediate (obj) and output (bin) trees before building.
# The WinUI3 XAML compiler's incremental build is unreliable: an edited .xaml is
# often NOT recompiled to its embedded .xbf, so an incremental build can run with
# STALE compiled XAML (UI/layout/animation fixes silently don't take). MSBuild's
# --no-incremental does not purge those stale .xbf artifacts; only deleting obj/bin
# does. This mirrors publish.ps1 so a clean debug build matches the shipped build.
if ($Clean) {
    Write-Step 'Cleaning obj/bin (XAML compiler incremental builds are unreliable)...'
    $appDir = Split-Path $appProj -Parent
    foreach ($d in @('obj', 'bin')) {
        $path = Join-Path $appDir $d
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    Write-Ok 'Cleaned.'
}
Write-Step "Building ($Configuration)..."
& dotnet build $appProj -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Fail 'Build failed.'; exit $LASTEXITCODE }
Write-Ok 'Build succeeded.'

# --- Test ---
if (-not $SkipTests) {
    Write-Step 'Running tests...'
    dotnet test $testProj -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Tests failed.'; exit $LASTEXITCODE }
    Write-Ok 'All tests passed.'
} else {
    Write-Step 'Skipping tests (-SkipTests).'
}

# --- Run ---
# Default to the *unpackaged* profile so debug matches how publish.ps1 ships the
# app (no package identity). Identity-dependent paths — toast activation,
# Package.Current, etc. — then behave the same in debug as in the installed build.
# Use -Packaged to launch under the MSIX debug identity instead.
if (-not $BuildOnly) {
    if ($Packaged) {
        $launchProfile = 'BlueBubbles.Windows (Package)'
        Write-Step 'Launching BlueBubbles (MSIX packaged)...'
    } else {
        $launchProfile = 'BlueBubbles.Windows (Unpackaged)'
        Write-Step 'Launching BlueBubbles (unpackaged — matches publish.ps1)...'
    }
    dotnet run --project $appProj -c $Configuration --launch-profile $launchProfile --no-build
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Launch failed.'; exit $LASTEXITCODE }
} else {
    Write-Step 'Build-only mode — skipping launch.'
}

Write-Host ''
Write-Ok 'Done.'
