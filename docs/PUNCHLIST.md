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
- [ ] Hovering a conversation in the list pops a `Ctrl + N` tooltip — but only right after the
      window is first brought up. Goes away after the initial interaction.
- [ ] Likely a keyboard-accelerator tooltip (`KeyboardAccelerator` / `AccessKey`) leaking onto
      the list item or its container on first show. Track down the source and suppress it.

#### B2. Group-chat info back button needs multiple clicks
- [ ] Opening chat info for a **group** chat requires clicking Back at least twice to return.
- [ ] Suggests a duplicate/extra frame navigation (double `Navigate`) when opening group info, or
      a back-stack entry that isn't being collapsed. Audit the group-info open path vs. 1:1.

#### B3. Avatar bubble flickering
- [ ] Avatar bubbles still flicker intermittently. Tough to reproduce — no clear pattern found yet.
- [ ] Add logging around avatar load/assignment (e.g. async-image generation counters, container
      recycle) to capture when/why the flicker happens before attempting a fix.

#### B4. Installer doesn't close the running app during update
- [ ] Installing a new version over a running instance doesn't terminate the old app — the installer
      hangs until the app is manually closed.
- [ ] Inno Setup should detect + close the running instance (e.g. `CloseApplications`, app mutex, or
      a kill step in `publish.ps1`'s installer script) so updates apply cleanly.
- [ ] **Blocks U1 (auto-updater):** an unattended update can't hang waiting on a manual close.

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
