# Punchlist

> Completed work lives in `docs/PUNCHLIST-ARCHIVE.md` — moved out to keep this file focused on
> what's left.

---

## Open bugs

#### B1. New/updated messages don't reach the conversation list until a restart or "fetch latest"
- [x] Messages persisted fine; the *event contract* was broken. Six causes fixed: silent bare catch in
  `IncomingMessageProcessor.ProcessAsync`; `ChatsService.HandleNewMessageAsync` no-opping on an unknown
  chat; `ProcessUpdatedMessageAsync` raising no event; `ConversationListViewModel` never subscribing to
  `MessagesPersisted`; `EnsureChatExistsAsync` not refreshing the in-memory list; silent participant-fetch
  failure. `UpdateMessageAsync` now returns the owning chat GUID.
- Branch `fix/sync-ui-propagation`, PR 3. Verified: clean `build-and-run.ps1` (build 0 errors, 396/396
  tests, app launched and responding, PID 12256). Negative controls run by me — all four new tests fail
  on unmutated-away fixes. A mutation of the owning-chat lookup (`ChatId ± 1`) originally **survived**;
  covered by `UpdateMessage_ReturnsOwningChatGuid_NotJustTheFirstChat` (commit `4fa277d`).
- [ ] **Not verified end-to-end** — nobody has watched a live inbound message land in the list with the
  macOS server running. Symptom fix is inferred, not observed.
- Deliberately unchanged: reactions still raise no persist event (nothing list-visible changes).

#### B2. Attachment images render wrong / not at all — **CLOSED**, see `docs/PUNCHLIST-ARCHIVE.md`
- [x] Fixed and confirmed live on 2026-08-11: four inbound images, each toast -> auto-download ->
  decode -> done, including an adversarial run against a deleted-and-recreated thread. No
  `HasAttachments=true but 0 attachment rows` and no re-append Warn in the whole session.

#### B2e. Remove or gate the `[attach-diag]` instrumentation
- [ ] B2 is closed, so the diagnostics have served their purpose. `AppendMessageBubbles` still runs
  an O(items) LINQ scan per appended message, unconditionally, purely to feed a Warn.
- [ ] Keep the `VerboseLogging -> AppLog.MinLevel` restore and the About toggle (B2a) — those are the
  reason B2 was diagnosable at all. Only the `[attach-diag]` lines are in scope.

#### B2f. Every attachment image is decoded twice, and the first decode is always discarded
- [ ] Measured in the 2026-08-11 log, **7 of 7** attachments bound through the normal path (3 at
  startup, 4 inbound): `decode start ... gen=2`, then `decode start ... gen=3`, then
  `decode STRANDED ... gen=2`, then `decode done ... gen=3`. The first decode is always wasted —
  double file I/O and double bitmap work per image. Avatars show the same shape at startup
  (`gen=1` MISS -> decode, `gen=2` MISS -> decode, `gen=1 ... STALE -> drop`), so the trigger is a
  systemic double-bind, not attachment-specific.
- [ ] The one exception was an outgoing `at_0_`-prefixed (optimistic-send) attachment, which decoded
  once. That asymmetry is the lead.
- [ ] Performance only — the generation guard is doing its job and the correct image wins every time.
  Note the first bind already reports `gen=2`, i.e. the holder is bound twice before any decode
  starts; find who binds it and why, before changing anything.

#### B2b. No UI-layer logic in this codebase is testable
- [ ] `BlueBubbles.Windows.Tests` references only `BlueBubbles.Core`, so `ChatViewModel`,
  `MessageBubbleViewModel`, `AttachmentHolder` and `ImageLoader` are unreachable from any test. This
  is what turned B2 from a fix into a research task, and it will do the same to the next UI bug.
  Decide whether to add a `net8.0-windows` test project or accept human-only verification for the
  view layer.

#### B2c. `GetMessagesByGuidsAsync` omits `.Include(m => m.Attachments)`
- [ ] `LoadMessagesAsync` and `LoadMessagesAfterAsync` include it; this one doesn't. Harmless today
  (reconcile and reply snippets don't read attachments) but it is exactly the query a B2 fix would
  reuse, and it would silently return zero attachments.

#### B2d. `ChatBubble.Unloaded` nulls `_currentVm` but not `_renderedContentForVm`
- [ ] The two fields are meant to move together; no misrender case was constructed. Low confidence
  that this is real, recorded so it isn't rediscovered.

#### B3. OTP toast sometimes has no "Copy code" button
- [ ] We already emit 5 buttons (Send + 4 reactions) = the Windows maximum, so Windows has no room to
  inject its own "Copy 123456". Independent of B1/B2. Not implemented.

#### B4. `ConversationListViewModel.OnChatUpdated` is `async void` with an unthrottled full reload
- [ ] Runs `LoadChatsAsync()` on every group-name/participant socket event with no debounce, and an
  exception in it is unobserved. Found while reviewing B1; deliberately left out of that PR's scope.

#### B5. Tests write to the real `%LOCALAPPDATA%\BlueBubbles\logs`
- [ ] Pre-existing: `AppLog` is a static singleton, so the suite pollutes production logs. Harmless,
  untidy.

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
