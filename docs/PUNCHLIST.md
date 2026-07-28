# Punchlist

> Completed work lives in `docs/PUNCHLIST-ARCHIVE.md` — moved out to keep this file focused on
> what's left.

---

## Open bugs

*(none)*

---

## F — Feature backlog  *(feature → future minor)*

#### F2. Audio message support
- [ ] Record and send audio messages (voice memos) from the composer, matching iMessage's
      tap-and-hold-to-record audio bubble.
- [ ] Inbound audio attachments already play back (`AttachmentHolder`/`AttachmentViewModel`); this
      is about *recording and sending* a new audio attachment.

#### F4. Scheduled send enhancements (not v1)
- [ ] Recurring schedules (wire format already supports `{type:"recurring", interval,
      intervalType}`).
- [ ] Scheduling a reply (`selectedMessageGuid` is plumbed through `ScheduledMessageService`, no
      UI yet).

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

### T1. (Backlog) Unit-test coverage gaps
14 services have no dedicated test file (SocketService, NotificationService, FirebaseService,
AttachmentCacheService, LinkPreviewService, ScheduledMessageService, …). Mostly hard-to-test
network/UI-thread code; add targeted seams opportunistically when one of them next regresses.

> **Not doing:** code-signing (Azure Trusted Signing / SmartScreen prompt). Explicitly out of scope.
>
> **Not doing:** FaceTime / incoming-call support. Answering never yields media (the server hands
> back a `facetime.apple.com` browser link), and because the server's helper hooks the *system-wide*
> `TUCallCenter`, relayed cellular calls are indistinguishable from FaceTime Audio on the wire — so
> "Answer" can silently pick a call up on the Mac and then fail. Revisit only if upstream forwards
> the `is_conversation` flag their helper already computes but the server drops.

---

## Release plan

**Future minor** — audio message support (F2), scheduled-send enhancements (F4), client updater
(U1).

No version bump: repo hygiene (H2). Stretch goal (no schedule): arm64 (S1).
