<#
.SYNOPSIS
    Build, test, and run BlueBubbles WinUI3 from FRESH code.

.DESCRIPTION
    Restores packages, builds the solution, runs unit tests, and launches the app.
    By default it does a FULL CLEAN build so what you run is guaranteed to be your
    current source - no stale compiled XAML, no leftovers. Use -Fast only when you
    know you changed C# (not XAML) and want a quick incremental build.

    The clean + lock-release logic is shared with publish.ps1 (build-common.ps1),
    so a local debug build and the shipped installer come from the same fresh tree.

.PARAMETER Configuration
    Build configuration. Default: Debug.

.PARAMETER Fast
    Skip the full clean and build incrementally. FASTER but NOT guaranteed fresh:
    the WinUI3 XAML compiler's incremental build does NOT reliably recompile an
    edited .xaml into its embedded .xbf, so an incremental build can silently run
    with STALE XAML (UI/layout/animation edits don't take). Only use -Fast for
    C#-only changes. Omit it and you always get a clean, trustworthy build.

.PARAMETER SkipTests
    Skip the test step.

.PARAMETER BuildOnly
    Build (and optionally test) without launching the app.

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

    [switch]$Fast,
    [switch]$SkipTests,
    [switch]$BuildOnly,
    [switch]$Packaged
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
. (Join-Path $root 'build-common.ps1')   # Write-* helpers + Clear-BuildOutputs

$appProj   = Join-Path $root 'BlueBubbles.Windows\BlueBubbles.Windows.csproj'
$testProj  = Join-Path $root 'BlueBubbles.Windows.Tests\BlueBubbles.Windows.Tests.csproj'

# Every project whose obj/bin must be wiped for a build to be genuinely fresh.
$projectDirs = @(
    (Join-Path $root 'BlueBubbles.Windows'),
    (Join-Path $root 'BlueBubbles.Core'),
    (Join-Path $root 'BlueBubbles.Windows.Tests')
)

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

# --- Clean (default) or warn (-Fast) ---
if ($Fast) {
    Write-Step 'Fast incremental build (-Fast).'
    Write-Warn 'NOT guaranteed fresh: edited XAML may not recompile. Use a full build (omit -Fast) before trusting UI changes.'
} else {
    Clear-BuildOutputs $projectDirs
}

# --- Restore ---
# Restore both the app and the test project: a full clean wipes the test project's
# obj (its project.assets.json), so the test step can no longer assume it's restored.
Write-Step 'Restoring NuGet packages...'
& dotnet restore $appProj
if ($LASTEXITCODE -ne 0) { Write-Fail 'Restore failed (app).'; exit $LASTEXITCODE }
& dotnet restore $testProj
if ($LASTEXITCODE -ne 0) { Write-Fail 'Restore failed (tests).'; exit $LASTEXITCODE }
Write-Ok 'Restore succeeded.'

# --- Build ---
Write-Step "Building ($Configuration)..."
& dotnet build $appProj -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { Write-Fail 'Build failed.'; exit $LASTEXITCODE }
Write-Ok 'Build succeeded.'

# --- Test ---
if (-not $SkipTests) {
    Write-Step 'Running tests...'
    & dotnet test $testProj -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Tests failed.'; exit $LASTEXITCODE }
    Write-Ok 'All tests passed.'
} else {
    Write-Step 'Skipping tests (-SkipTests).'
}

# --- Run ---
# Default to the *unpackaged* profile so debug matches how publish.ps1 ships the
# app (no package identity). Identity-dependent paths - toast activation,
# Package.Current, etc. - then behave the same in debug as in the installed build.
# Use -Packaged to launch under the MSIX debug identity instead.
if (-not $BuildOnly) {
    if ($Packaged) {
        $launchProfile = 'BlueBubbles.Windows (Package)'
        Write-Step 'Launching BlueBubbles (MSIX packaged)...'
    } else {
        $launchProfile = 'BlueBubbles.Windows (Unpackaged)'
        Write-Step 'Launching BlueBubbles (unpackaged - matches publish.ps1)...'
    }
    & dotnet run --project $appProj -c $Configuration --launch-profile $launchProfile --no-build
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Launch failed.'; exit $LASTEXITCODE }
} else {
    Write-Step 'Build-only mode - skipping launch.'
}

Write-Host ''
Write-Ok 'Done.'
