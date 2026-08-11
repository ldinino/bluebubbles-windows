# Punchlist

> Completed work lives in `docs/PUNCHLIST-ARCHIVE.md` — moved out to keep this file focused on
> what's left.

---

## Open bugs

#### A1. Remove four Appearance settings (maintainer decision, 2026-08-11)
- [ ] Remove **Colorful bubbles**, **Dense chat tiles**, **Hide dividers** and **Avatar size** from
  Settings > Appearance. Kept: Theme, Colorful avatars, 24-hour time.
- **They are not dead code** — all four are wired and have real effects
  (`ChatBubble.xaml.cs` bubble tint, `ConversationTileViewModel` `TilePadding`/`DividerThickness`,
  `AvatarControl` `Size * AvatarScale`). This is a deliberate feature removal for being clutter, not
  a cleanup, so the surviving behaviour has to be chosen rather than fallen into.
- **Surviving behaviour = today's defaults**, so a user on defaults sees no visual change: bubbles
  take `ControlFillColorDefaultBrush`, tiles use `Thickness(8,10,8,10)`, the 1px divider stays,
  avatars render unscaled.
- **Measured, and the reason this is safe:** `SettingsService.JsonOpts` sets only `WriteIndented`
  and `CamelCase` — no `UnmappedMemberHandling.Disallow` — so System.Text.Json skips unknown
  properties and an existing `settings.json` still loads once the keys are gone. **No migration or
  cleanup shim is needed or wanted.** (Had that option been `Disallow`, this removal would have
  reset every setting for every existing user on upgrade.)
- [ ] Docs to follow the code: `README.md`, `docs/BlueBubbles-WinUI3-Design-Spec.md` (the Appearance
  list), `docs/PLAN.md` phase entry.

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

#### B2f. Every attachment image is decoded twice — **FIXED**
- [x] Fixed in `91d99a2`, merged `5d3a7d1`. Confirmed: images render (maintainer, 2026-08-11), and the
  before/after on the same scenario is **10 decode starts / 5 stranded -> 4 decode starts / 0
  stranded, 4 short-circuits**.
- **Root cause, as hypothesised and then confirmed by instrumentation:** `LoadMedia`'s early-out was
  `vm.LocalPath == _renderedMediaPath && MediaImage.Source is not null`. While the first decode was
  in flight `_renderedMediaPath` was set but `Source` was still null, so a second call could not
  short-circuit. A new `_loadingMediaPath` field closes that window; it is cleared on rebind, retry,
  error, cache hit, and in a `finally` guarded so a stranded decode cannot clear its successor's.
  The generation guard was left untouched.
- **The two second callers, measured:** `Loaded` firing after `DataContextChanged` (~46 ms later),
  and `AttachmentViewModel.DownloadInternalAsync` raising `PropertyChanged` twice as a download
  completes — once for `LocalPath`, once for `State` (~3 ms apart). Both are legitimate.
- **Struck as refuted, do not re-investigate:**
  - ~~the `at_0_` optimistic-send lead~~ — `at_0_`/`at_1_` is just the attachment index within a
    message. 19 such attachments all double-decoded, and the same GUID double-decodes in one run and
    single-decodes in another. The asymmetry was cache state, not the prefix.
  - ~~"the trigger is a systemic double-bind"~~ — it is a second `LoadMedia` call, not a second bind.
  - ~~"the list may not be virtualizing"~~ — `ItemsStackPanel` in `ChatPage.xaml` virtualizes fine.
    Off-screen attachments were downloading because the download was fired from view-model
    construction, not from container realization. That is B2g.

#### B2g. Images decode oldest-first — mechanism fixed, **effect not yet measured**
- [x] `TriggerAutoDownload` removed from `BuildMessageList`; the download now starts when the
  container is realized (`AttachmentHolder`, `NotDownloaded` case), so it follows what is on or near
  screen instead of walking the loaded window oldest-first. The new-message append path still
  triggers directly. `bbd899c`, merged `5d3a7d1`.
- **Measured before the fix:** `BuildMessageList` fired 19 auto-downloads within 20 ms in strictly
  ascending `DateCreated` order, and decodes then arrived in download-completion order — so the
  on-screen newest images sat behind the whole page.
- [ ] **The improvement itself is unverified.** Every post-fix run had all attachments already
  cached, so no auto-download fired and there is no after-order list and no time-to-first-visible
  number. To settle it: open a thread containing undownloaded images with verbose logging on.
- [ ] **Behaviour changes to watch for in use:** attachments in messages never scrolled to are no
  longer prefetched (intended), and scroll-back pages now auto-download where they never did before
  (`PrependMessages` never called `TriggerAutoDownload`). If fast scrolling now feels worse, this is
  the trade to revisit.

#### B2h. Avatars decode twice too — same defect class, ~half the work wasted
- [ ] **Measured on the 2026-08-11 16:48 launch (post-B2f build): 40 avatar decodes started, 19
  stranded, 21 landed.** Nearly half the avatar decode work at startup is thrown away. The signature
  is identical to B2f — `RefreshLayout gen=1` then `gen=2` 1-3 ms later, first decode dropped as
  `STALE`.
- [ ] Deliberately out of scope for the B2f pass and still unproven to share a cause. `AvatarControl`
  has its own generation machinery and its own history (archive cluster A / AT1), so confirm the
  mechanism before assuming `_loadingMediaPath` transplants.
- [ ] Performance only. Avatars render correctly today.

#### B2i. `build-common.ps1` crashes when exactly one app instance is running
- [ ] `Stop-BlueBubbles` does `$procs.Count`, and with `Set-StrictMode -Version Latest`
  (`build-common.ps1:27`) a single `Process` object has no `Count` in PowerShell 5.1. Reproduced:
  `The property 'Count' cannot be found on this object.` Zero or two-plus instances work; exactly
  one — the case where the script most needs to release the file lock — kills it at "Cleaning
  obj/bin".
- [ ] Affects `build-and-run.ps1` and `publish.ps1`, both of which set StrictMode too. Fix is
  `@($procs).Count`. **Release machinery — needs maintainer authorisation before it is touched.**


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
