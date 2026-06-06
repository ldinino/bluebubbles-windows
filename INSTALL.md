# Installing BlueBubbles (Windows)

BlueBubbles for Windows ships as a **free, double-click installer** — no certificate, no Store, no
MSIX. The installer bundles everything it needs (the .NET and Windows App SDK runtimes), so it runs
on a clean machine with nothing pre-installed.

## For testers / end users

1. Download **`BlueBubbles-Setup-<version>-x64.exe`** (from the GitHub Release, or wherever it was
   shared). Only an x64 build is published for now; on an ARM PC it runs under Windows' x64 emulation.
2. Double-click it.
3. **First-launch SmartScreen prompt:** because the build is not code-signed, Windows shows
   *"Microsoft Defender SmartScreen prevented an unrecognized app from starting."* Click
   **More info → Run anyway**. This is expected for unsigned open-source apps and goes away as the
   app builds reputation (or once it's signed — see below).
4. The installer is **per-user** (no admin prompt), installs to
   `%LocalAppData%\Programs\BlueBubbles`, and adds a Start-menu shortcut (and a desktop shortcut if
   you tick the box). It then offers to launch the app.

**Updating:** run a newer `Setup.exe` — it upgrades in place (closing the running app first).

**Uninstalling:** Settings → Apps → *BlueBubbles* → Uninstall, or Start-menu right-click → Uninstall.

---

## For maintainers — building the installer

```powershell
# from the repo root
.\publish.ps1                  # x64 (default)
.\publish.ps1 -Platform arm64  # for ARM machines
```

This publishes the app unpackaged + self-contained and wraps it with **Inno Setup** into
`dist\BlueBubbles-Setup-<version>-<arch>.exe`. Inno Setup is required:

```powershell
winget install JRSoftware.InnoSetup
```

If Inno Setup isn't installed, `publish.ps1` falls back to a portable `.zip` (extract and run
`BlueBubbles.Windows.exe`) and prints how to get the installer.

The version comes from `<Version>` in `BlueBubbles.Windows.csproj` (single source of truth) — bump
it there and re-run.

---

## About that SmartScreen warning (and how to remove it)

There is **no free way** to make an unsigned `.exe` install with zero warnings — that's a Windows
policy, not a packaging choice. The warning is harmless (the app is just "unrecognized," not flagged
as malware) and disappears as downloads accumulate. To remove it up front you'd add **code
signing**:

- **Azure Trusted Signing** (~$10/month) — cheapest path to a verified publisher and a clean,
  warning-free launch. Works with either this `.exe` or an MSIX.
- **An OV/EV code-signing certificate** (~$200+/yr) — the traditional route.

When you're ready for that, signing slots into `publish.ps1` after the Inno Setup step
(`signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 …` against the produced `Setup.exe`).

> A separate **MSIX** path also exists (`package.ps1` + the `Package.appxmanifest`) for Store-style
> packaged distribution. It requires signing too (self-signed for testing, or a real cert for
> public use). The unpackaged installer above is the recommended free option for GitHub.
