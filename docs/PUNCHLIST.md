# Punchlist

> **Cleared (detail in git history):** Phase-6 items 1–33, plus Debug Session 2 clusters
> **D** (diagnostics/logging), **H1** (repo hygiene), **N** (notifications), **S** (sync
> reliability), **L** (layout/animation), **AT1** (image flicker — incl. the scroll-recycle
> follow-up: decoded-bitmap LRU cache so recycled bubbles re-show inline images synchronously),
> **UN** (uninstall/reset cleanup), **A** (avatars — generic person glyph + info-bar avatar
> mirrors the list), **AT2** (in-app video playback via `MediaPlayerElement` with external
> fallback), **34** (GH Actions release workflow: `dotnet test` + `publish.ps1 -Platform x64`,
> draft `v<version>` Release with installer attached), and **35** (flaky
> `Reaction_FromOther_PersistedAndNotifies` test). Remaining open work below.

---

## B — Bugfix release (0.20.2)

#### B1. Stray `Ctrl + N` tooltip on conversation hover
- [x] Hovering a conversation in the list pops a `Ctrl + N` tooltip — but only right after the
      window is first brought up. Goes away after the initial interaction.
- [x] **Fixed:** the app-global `Ctrl+N`/`Ctrl+F`/`Esc` `KeyboardAccelerator`s on ShellPage's root
      Grid auto-generated a key-combo tooltip (default placement `Auto`) that leaked over the list on
      first show. Set `KeyboardAcceleratorPlacementMode="Hidden"` on the Grid (`ShellPage.xaml`).

#### B2. Group-chat info back button needs multiple clicks
- [x] Opening chat info for a **group** chat requires clicking Back at least twice to return.
- [x] **Fixed:** ChatPage is `NavigationCacheMode="Required"` (one reused instance), and
      `OnChatFrameNavigated` subscribed `DetailsRequested` with a bare `+=` — so every conversation
      switch stacked another handler, and an Info click then `Navigate`d to the details page once per
      handler, pushing extra back-stack entries. Added the `-=`/`+=` guard (matching
      `BackToListRequested`) and removed the now-redundant self-unsubscribe (`ShellPage.xaml.cs`).

#### B3. Avatar bubble flickering
- [x] Avatar bubbles flickered intermittently — the whole list would flash blank→photo.
- [x] **Root cause (found via the diagnostics below):** every contacts reload
      (`ContactResolverService.LoadFromVCardAsync`) cleared `_avatarCache` and re-added **freshly
      parsed** `byte[]` photos. Callers key on **reference** equality — the tile `AvatarBytes`
      binding (`[ObservableProperty]` on `byte[]`) and `AvatarControl`'s decoded-bitmap cache — so
      identical photos came back as new arrays: the binding rebound and the cache missed, forcing
      `PersonPic.ProfilePicture = null` + an async re-decode for every visible avatar at once. The
      `[Ui]` logs showed a `cache HIT` pass immediately followed by an all-`cache MISS -> clear+decode`
      pass on each reload.
- [x] **Fixed:** `LoadFromVCardAsync` now snapshots the prior photo arrays and reuses the **same
      reference** when the reloaded bytes are byte-identical (`StablePhoto`). Unchanged photos become
      a no-op (no rebind, no re-decode); only genuinely changed/added photos decode. Regression tests
      `GetAvatar_KeepsSameReference_WhenReloadedPhotoUnchanged` /
      `..._ReturnsNewReference_WhenPhotoChanged` cover it.
- [x] **Diagnostics retained:** `Debug`-level `[Ui]` avatar tracing + the **Verbose logging** toggle
      in *Settings > About > Diagnostics* (persisted; raises `AppLog.MinLevel` to `Debug`) stay in
      for future investigation. Off by default.

#### B4. Installer doesn't close the running app during update
- [x] Installing a new version over a running instance doesn't terminate the old app — the installer
      hangs until the app is manually closed.
- [x] **Fixed:** `CloseApplications=yes` relied on the Restart Manager, which can't close this
      WinUI window/tray process (it stalled on the "applications in use" page). Added a `[Code]`
      `PrepareToInstall` event that `taskkill /F /IM`s the running instance before file copy
      (mirrors the existing `[UninstallRun]` kill), so upgrades and `/VERYSILENT` runs apply cleanly
      (`installer/BlueBubbles.iss`).
- [x] **Unblocks U1 (auto-updater):** an unattended update no longer hangs on a manual close.

---

## U — Client updater  *(feature → future minor)*

#### U1. In-app updater
- [ ] Check GitHub Releases for a newer version on launch (and/or on demand).
- [ ] Download + run the unpackaged installer (ties into Inno Setup / `publish.ps1` output and the GH Actions release flow, item 34).
- [ ] Surface "update available" in the UI; respect the unpackaged-distribution constraints (no package-identity APIs).

---

## H — Repo hygiene  *(not a feature/bug — keep on the list)*

#### H2. (Later) Clean up vibe-coding markdown for public consumption
When the project reaches a good public-ready state, revisit all the internal/agent
markdown (`AGENTS.md`, `.github/instructions/*.md`, spec/plan/punchlist) and decide
what to polish + re-expose so people can clone and vibe along. Not now — future todo.

---

## Backlog — Release & CI

### S1. (Stretch goal) arm64 build
Only once the core featureset is confidently nailed down — not a near-term priority.
- [ ] **Blocked on vendored binary:** `Runtime\Microsoft.WindowsAppRuntime.Insights.Resource.dll`
      is a checked-in **x64** PE binary copied next to the exe unconditionally; an arm64
      cross-compile would ship it beside the arm64 exe and re-trigger the toast-activation
      failure fixed in ca6d3e6 — uncatchable without arm64 hardware. To enable: re-vendor the
      arm64 copy of that DLL (per-RID) and validate the installed build on a real ARM machine.

> **Not doing:** code-signing (Azure Trusted Signing / SmartScreen prompt). Explicitly out of scope.

---

## Release plan

**Future minor** — client updater (U1).

No version bump: repo hygiene (H2). Stretch goal (no schedule): arm64 (S1).
