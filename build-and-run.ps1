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
    Run a clean build instead of an incremental one.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipTests,
    [switch]$BuildOnly,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$appProj   = Join-Path $root 'BlueBubbles.Windows\BlueBubbles.Windows.csproj'
$testProj  = Join-Path $root 'BlueBubbles.Windows.Tests\BlueBubbles.Windows.Tests.csproj'

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"  -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "    $msg"  -ForegroundColor Red }

# --- Pre-flight: Developer Mode ---
$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devMode -ne 1) {
    Write-Fail 'Windows Developer Mode is not enabled.'
    Write-Fail 'Enable it in Settings > Privacy & Security > For developers.'
    exit 1
}
Write-Ok 'Developer Mode is enabled.'

# --- Restore ---
Write-Step 'Restoring NuGet packages...'
dotnet restore $appProj
if ($LASTEXITCODE -ne 0) { Write-Fail 'Restore failed.'; exit $LASTEXITCODE }
Write-Ok 'Restore succeeded.'

# --- Build ---
$buildArgs = @('build', $appProj, '-c', $Configuration)
if ($Clean) { $buildArgs += '--no-incremental' }
Write-Step "Building ($Configuration)..."
& dotnet @buildArgs
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
if (-not $BuildOnly) {
    Write-Step 'Launching BlueBubbles (MSIX packaged)...'
    dotnet run --project $appProj -c $Configuration --launch-profile 'BlueBubbles.Windows (Package)' --no-build
    if ($LASTEXITCODE -ne 0) { Write-Fail 'Launch failed.'; exit $LASTEXITCODE }
} else {
    Write-Step 'Build-only mode — skipping launch.'
}

Write-Host ''
Write-Ok 'Done.'
