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

#### B2e. Remove the `[attach-diag]` instrumentation — **DONE**
- [x] Removed in `b609d4a`, merged `2cdd29e`. Deletion-only: 3 files, 3 insertions / 32 deletions;
  `MessageBubbleViewModel.cs` resolves back to its pre-instrumentation blob `b635787`. The O(items)
  `Any(...)` scan in `AppendMessageBubbles` was deleted wholesale, not just its log line, and
  `int appended` reverted to `bool` with control flow unchanged. Verified: 0 `attach-diag` matches
  left in source, clean `build-and-run.ps1`, 399/399, app launched.
- [x] B2a keepers confirmed intact: `AppLog.MinLevel` in `App.xaml.cs` and the "Verbose logging"
  card in `AboutSettingsPage.xaml`. The pre-existing `Avatar[...]` traces were not touched.

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
- [ ] **Correction to an earlier note in this entry:** "the holder is bound twice before any decode
  starts" was wrong. `gen=2` on the *first* decode is expected — `OnDataContextChanged` increments
  once and `LoadMedia` increments again. The double-decode is the jump to `gen=3`, i.e. a **second**
  `LoadMedia` call, not a second bind.
- [ ] Leading hypothesis, from a source read and **not yet measured**: `LoadMedia`'s early-out is
  `if (vm.LocalPath == _renderedMediaPath && MediaImage.Source is not null) return;`. While the first
  decode is still in flight `_renderedMediaPath` is already set but `MediaImage.Source` is still
  null, so the guard cannot short-circuit and a second call decodes again. That also fits the
  `at_0_` exception, which took the synchronous cache-hit path and had `Source` assigned before any
  second call could arrive. Find the second caller before changing the guard.

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

#### B3. OTP toast has no "Copy code" button — **CLOSED: won't fix, upstream gap**
- **Decision (2026-08-11, maintainer):** do not ship a client-side OTP detector. Detecting one-time
  passcodes in notification text is the platform's job; carrying our own heuristic means owning it
  forever — every phrasing drift and every future false positive — to patch an OS gap that currently
  affects one sender's phrasing, where the workaround is reading six digits off the toast. Report it
  to Microsoft instead. **Do not re-litigate this by pointing at the detector's accuracy; the
  objection is to permanent ownership, not to the code's quality.**
- **Reproduced** 2026-08-11: a Google Voice SMS reading `Enter Advanced Access code 123456 online to
  verify your identity.` produced a toast with the reply box, Send and four tapbacks and no copy
  affordance of any kind.
- **The measurement, kept because it is the expensive part.** `OtpDetector` was run over the real
  local cache: **5,639 messages, 5,282 with text, 16 flagged (15 distinct) in 6 shape clusters.**
  Six representative toasts were then shown and the OS affordance observed by eye:

  | Cluster | Shape | Msgs | Windows affordance |
  |---|---|---|---|
  | C1 | sender prefix, `code is N` | 6 | yes |
  | C2 | warning preamble + `Enter <adj> code N online to ...` | 4 | **no** |
  | C3 | `Enter <adj> code N online to ...`, no preamble | 2 | **no** |
  | C4 | scam-warning preamble, terminal `Code is: N` | 1 | yes |
  | C5 | bracketed sender, code first (`[Walmart] N is your ... code`) | 1 | yes |
  | C6 | bare canonical (`Your verification code is: N`) | 1 | yes |

  **Windows: 9/15 messages, 4/6 clusters.** The gap is exactly the shape `Enter <adjective> code N`
  — `code` as the object of an imperative verb with a modifier wedged in. The preamble is
  irrelevant (C2 and C3 differ only by it and both failed). Both misses are **one sender**
  (Wells Fargo), so the 40% figure is fragile: one bank rewording its template moves it to 0%.
- **Refuted along the way, recorded so it is not re-investigated:**
  - ~~The missing pill is caused by our button budget~~ — a toast allows 5 actions *and* 5 inputs
    independently, and the OS affordance renders in its own row outside both. A harness variant with
    all five slots full still showed the pill.
  - ~~Windows covers this anyway~~ — that came from a sweep of ten *invented*, textbook-shaped
    strings showing 9/10. Real coverage is 9/15. **Never validate this class of question with
    invented test data**; canonical strings systematically overestimate a pattern matcher.
- **Design note for whoever revisits this.** The tempting option — "only add our button when Windows
  would miss it" — is undeliverable: you cannot ask Windows at runtime whether it will show its
  pill, so it can only be implemented by modelling the OS heuristic in our code. Its failure mode is
  the bad one: if the model drifts we suppress our button, Windows shows nothing, and B3 returns
  silently. Also note the shipping toast already uses 5 of 5 buttons (`Send` + 4 tapbacks), so a
  Copy button could not simply be added — it would have to replace the tapbacks.
- **Reopen only if** the gap widens beyond one sender's phrasing (a second, unrelated sender's shape
  starts failing), or Microsoft declines the report and the affected traffic is materially higher
  than 9/15.
- Research branches are the only record of the harness and detector and are **NOT FOR MERGE**:
  `experiment/otp-toast-windows-affordance` (`98ef65a`), `research/otp-real-corpus` (`b6c8cce`).

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
