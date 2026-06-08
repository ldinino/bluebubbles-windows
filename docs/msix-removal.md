# MSIX removal (de-packaging cleanup)

**Date:** 2026-06-08
**Branch/commit:** `cleanup/remove-msix-rot` @ `003ed2c`

## Why

This app was migrated long ago from MSIX packaging to an **unpackaged, self-contained**
build wrapped by an **Inno Setup** installer (`publish.ps1` → `installer/BlueBubbles.iss`).
MSIX code-signing problems had broken toast-notification activation, so packaged identity
was abandoned.

But MSIX **scaffolding lingered** in the build config, an orphaned `Package.appxmanifest`,
dead tile assets, test certificates, and — most importantly — a build package that made
**debug `dotnet run` register a *packaged* identity** while production shipped unpackaged.
That divergence, plus instruction docs that still taught `winapp`/MSIX workflows, kept the
project getting mistaken for an MSIX project. This change removes the rot at the root so
debug and production are both unpackaged and nothing on disk re-seeds the confusion.

## What was removed

### Build config — `BlueBubbles.Windows/BlueBubbles.Windows.csproj`
- `<EnableMsixTooling>true</EnableMsixTooling>`
- The `Msix` `ProjectCapability` ItemGroup and the `HasPackageAndPublishMenu` PropertyGroup
  (both VS "Package and Publish" UI enablers).
- The `SyncManifestVersion` MSBuild target (only rewrote `Package.appxmanifest`).
- **`Microsoft.Windows.SDK.BuildTools.WinApp` PackageReference** — the package that hooked
  `dotnet run` to register a packaged loose-layout/AUMID identity via the `winapp` CLI.
  Removing it makes `dotnet run` launch the exe **unpackaged**, matching the installed build.
- All 19 manifest-only tile/logo `<Content>` includes (`SplashScreen`, `Square*`, `Wide*`,
  `StoreLogo`, `LockScreenLogo`).

### Deleted files
- `BlueBubbles.Windows/Package.appxmanifest` (COM/toast/startupTask manifest — see "unpackaged
  toast activation" below).
- 19 tile PNGs under `BlueBubbles.Windows/Assets/` (`StoreLogo.png`, `Square*`, `Wide310x150*`,
  `SplashScreen*`, `LockScreenLogo*`). **`AppIcon.ico` and `AppIcon-*.png` were kept** — those
  are the real app icon.
- `signing/BlueBubbles_Test.pfx` + `signing/BlueBubbles_Test.cer` (old MSIX test certs).
- `.TRASH/AppPackages/` and `.TRASH/(DO NOT USE) package.ps1` (old `.msix` + MSIX build script).
  *(These were git-ignored, so they only existed locally.)*

### Other wiring
- `BlueBubbles.Windows/Properties/launchSettings.json` — dropped the `MsixPackage` profile;
  only `BlueBubbles.Windows (Unpackaged)` remains.
- `build-and-run.ps1` — removed the `-Packaged` switch, its Developer-Mode pre-flight, and the
  packaged launch branch.
- `win-x86.pubxml` / `win-arm64.pubxml` — added explicit `WindowsPackageType=None` +
  `WindowsAppSDKSelfContained=true` to match `win-x64.pubxml`.
- `.gitignore` (root + `BlueBubbles.Windows/`) — removed the now-moot MSIX/Appx/pfx ignore lines.

### Docs
- `BlueBubbles.Windows/AGENTS.md` (git-ignored), `PLAN.md` (git-ignored), `INSTALL.md`, and the
  `.github/instructions/*.instructions.md` files — replaced `winapp`/MSIX-register guidance with
  the unpackaged `build-and-run.ps1` / `publish.ps1` flow.

## Why this is safe (what is load-bearing and untouched)

- **Unpackaged toast activation does NOT need the manifest.** `AppNotificationManager.Default.Register()`
  registers the COM activator in the **registry at runtime** for unpackaged apps; the manifest's
  `com:Extension`/`windows.toastNotificationActivation` entries were only consumed by a *packaged*
  build. The notification + single-instance code (`Program.cs`, `App.xaml.cs`,
  `Services/NotificationService.cs`, `AppInstance`) is unchanged.
- The vendored `Runtime/Microsoft.WindowsAppRuntime.Insights.Resource.dll` (copied next to the exe,
  required so `Register()` doesn't throw in the self-contained build) is **kept**.
- Already-correct unpackaged equivalents are untouched: `CredentialService` (DPAPI),
  `StartupTaskService` (`HKCU\...\Run`), `SettingsService` (file-based JSON), `AppInfo`
  (assembly version), `app.manifest` (native side-by-side manifest), `AppIcon.ico`.

## Verification done

- `./build-and-run.ps1 -BuildOnly` → clean build, **0 warnings / 0 errors, 310/310 tests pass**.
- `./publish.ps1` → self-contained publish + Inno Setup compile succeeded, produced
  `dist/BlueBubbles-Setup-<version>-x64.exe`.

### NOT verified automatically (check manually if anything misbehaves)
- **Live toast click / inline-reply → opens the correct thread.** Needs a running BlueBubbles
  server + the GUI. This is the exact regression MSIX signing originally caused, so it's the
  first thing to test. The activation code is untouched, so it should be unaffected.

## How to roll back

The whole change is one commit on `cleanup/remove-msix-rot`:

```powershell
# Revert the cleanup commit (keeps history)
git revert <commit-hash>

# …or hard-reset the branch back one commit if not yet shared
git reset --hard HEAD~1
```

If only **one** piece broke, the likely suspects and targeted restores:

| Symptom | Likely cause | Fix |
|---|---|---|
| `dotnet run` no longer launches the app | removal of `Microsoft.Windows.SDK.BuildTools.WinApp` | re-add the PackageReference, OR run via `./build-and-run.ps1` (the supported path) |
| Toast click/reply doesn't route back | unrelated to the manifest (runtime registration), but if it regressed, confirm `Register()` isn't throwing and the Insights resource DLL is next to the exe | see `csproj` Insights DLL comment |
| Build can't find an asset | a tile PNG was referenced somewhere we missed | restore the specific PNG from git history (`git checkout <commit>~1 -- <path>`) |
