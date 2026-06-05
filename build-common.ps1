<#
.SYNOPSIS
    Shared build helpers for build-and-run.ps1 and publish.ps1.

.DESCRIPTION
    One home for the "clean it FOR REAL" logic both scripts rely on, so a local
    debug build and the shipped installer come from the exact same fresh tree.

    Why this exists: `Remove-Item -Recurse -Force` on a WinUI3 obj/bin tree fails
    intermittently with "directory is not empty" for two compounding reasons:

      1. Locked handles. The .NET build servers (MSBuild node-reuse, the Roslyn
         VBCSCompiler, the WinUI XAML compiler) and any running BlueBubbles.Windows.exe
         keep file handles open into obj/bin.
      2. NTFS async-delete race. Even with no locks, PowerShell deletes the children
         and then tries to remove the parent before NTFS has finished, which surfaces
         as "directory is not empty." `cmd /c rmdir /s /q` and a retry loop tolerate
         this; a plain Remove-Item does not.

    Clear-BuildOutputs releases the handles (kill the app, shut the build servers
    down) and then deletes robustly, throwing a clear, actionable error if a tree
    still can't be removed instead of leaving a half-deleted directory behind.

    Dot-source this file (`. "$PSScriptRoot\build-common.ps1"`) to get the functions.
#>

Set-StrictMode -Version Latest

function Write-Step([string]$m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok  ([string]$m) { Write-Host "    $m"   -ForegroundColor Green }
function Write-Warn([string]$m) { Write-Host "    $m"   -ForegroundColor Yellow }
function Write-Fail([string]$m) { Write-Host "    $m"   -ForegroundColor Red }

# Stop any running app instance so its .exe / dlls in bin\...\publish unlock.
function Stop-BlueBubbles {
    $procs = Get-Process -Name 'BlueBubbles.Windows' -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Warn "Stopping $($procs.Count) running BlueBubbles instance(s) to release file locks..."
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        # Give the OS a beat to close the handles before we delete.
        Start-Sleep -Milliseconds 500
    }
}

# Shut down the .NET build servers so they release their handles into obj/bin.
function Stop-DotnetBuildServers {
    try { & dotnet build-server shutdown 2>$null | Out-Null } catch { }
}

# Robustly delete a directory tree, tolerating both the NTFS async-delete race and
# transient locks. Throws (does not silently continue) if the tree survives.
function Remove-DirRobust([string]$path) {
    if (-not (Test-Path $path)) { return }

    for ($i = 0; $i -lt 5; $i++) {
        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            if (-not (Test-Path $path)) { return }
        } catch {
            # fall through to retry / fallback
        }
        Start-Sleep -Milliseconds 400
    }

    # Last resort: cmd's rmdir copes with the pending-delete race better than Remove-Item.
    & cmd /c "rmdir /s /q `"$path`"" 2>$null

    if (Test-Path $path) {
        throw "Could not delete '$path' - a file in it is still locked. Close the app and Visual Studio (anything holding obj/bin open) and re-run."
    }
}

# The whole-graph clean: release locks once, then wipe obj + bin under every given
# project directory. Pass absolute project directory paths.
function Clear-BuildOutputs([string[]]$projectDirs) {
    Write-Step 'Cleaning obj/bin (full fresh build - WinUI3 XAML incremental builds are unreliable)...'
    Stop-BlueBubbles
    Stop-DotnetBuildServers
    foreach ($dir in $projectDirs) {
        foreach ($d in @('obj', 'bin')) {
            Remove-DirRobust (Join-Path $dir $d)
        }
    }
    Write-Ok 'Cleaned.'
}
