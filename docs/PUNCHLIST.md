# Punchlist

> Completed work lives in `docs/PUNCHLIST-ARCHIVE.md` — moved out to keep this file focused on
> what's left.

---

## Open bugs

#### A1. Remove four Appearance settings — **DONE**
- [x] Colorful bubbles, Dense chat tiles, Hide dividers and Avatar size removed from Settings >
  Appearance (`02dbe69` + `c13cb2a`, merged `c24619d`). Kept: Theme, Colorful avatars, 24-hour time.
  Surviving behaviour is today's defaults, so a user on defaults sees no change. Appearance page
  eyeballed by the maintainer.
- [x] **No settings migration, and none was needed.** `SettingsService.JsonOpts` leaves
  `UnmappedMemberHandling` at `Skip`, so an existing `settings.json` still loads with the dead keys
  present. `SettingsVersion` was correctly left at 1 and the v0->v1 migration untouched.
- [x] New test `Load_FileWithRemovedAppearanceKeys_StillAppliesSurvivingSettings` covers the upgrade
  path, which nothing did before. Negative control run by me: setting
  `UnmappedMemberHandling.Disallow` fails that test and **only** that test.
- [x] Fixed in review: the defaults test had swapped a non-vacuous assertion (`AvatarScale == 1.0`,
  where `double` defaults to `0.0`) for a vacuous one (`Use24HrFormat == false`, where `bool`
  already defaults to `false` and the constructor never sets it). Replaced with `SettingsVersion`,
  `NotificationSound` and `LocalhostPort`; mutation-proved by stripping two constructor defaults.
- [ ] **Orphans left behind, sweep in A2:** `ContactColors.TintForKey` and
  `MessageBubbleViewModel.SenderColorKey` (still assigned, never read) have no remaining callers.

#### A2. Messaging settings: drop "Scroll to last unread", make status indicators unconditional — **DONE**
- [x] Merged `6562a07` (`2d0202b` + `b76f526`). `ScrollToLastUnread` removed and threads always open
  at the bottom; `StatusIndicatorsOnChats` removed as a *toggle* while the Sent/Delivered/Read tile
  indicator stays on permanently (`show = _lastMessageIsFromMe`). 400/400, clean build, app launched.
- [x] **Neither was dead code.** Recorded so this isn't rediscovered: the indicator only ever appears
  on chats where *you* sent the last message, which is why toggling it looked like a no-op.
- [x] Removing the setting made `ApplyAppearance` need no `AppSettings` at all, which cascaded to the
  tile constructor (3 call sites) and to `ConversationListViewModel`'s whole `AppSettings`
  dependency. **Verified this does not break live 24-hour-time updates on the chat list** — those
  run through a separate subscription in `ConversationListPage.xaml.cs` that calls
  `RaiseTimestampChanged()` on each tile; the view model was never involved.
- [x] Orphans swept: `ContactColors.TintForKey`, `MessageBubbleViewModel.SenderColorKey`,
  `ChatViewModel.FirstUnreadGuid` and `ComputeFirstUnread`. Zero matches remain for any of them.
- [x] **Caught in review:** the PR as pushed still contained `ComputeFirstUnread` with zero callers —
  its deletion existed only as an *uncommitted* working-tree edit, so the reported green run was
  measured against a tree that was not the deliverable. An unused private method is not a compiler
  warning, so nothing else would have caught it. Committed as `b76f526` and re-verified from clean.
- [x] Upgrade-path test renamed and extended to carry both removed keys. Negative control run by me:
  `UnmappedMemberHandling.Disallow` fails that test and only that test.
- [ ] **Not verified end-to-end (human-only):** tile indicators showing unconditionally, threads
  opening at the bottom, Settings > Messaging layout, and restoring an old backup file that still
  carries `scrollToLastUnread` / `statusIndicatorsOnChats`.

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

#### A3. Remove the "Theme backup" section, fold its three keys into Settings backup
- [ ] Settings > Backup currently has **two independent** server-backed features: "Settings backup"
  (`SetSettingsBackupAsync` / `Get` / `Delete`, 11 messaging/notification/private-API keys) and
  "Theme backup" (`SetThemeAsync` / `Get` / `Delete`). Remove the second.
- **After A1, the theme payload is only `theme`, `colorfulAvatars`, `use24HrFormat`** — three
  ordinary settings, which is exactly why a whole separate section for them reads as pointless.
- **Decision: fold those three keys into the settings payload rather than dropping them.** Costs
  three lines each way plus a `ThemeHelper.Apply(_settings.Theme)` on the settings-restore path;
  deleting them outright would make "restore my settings" silently lose your theme, which is a small
  regression for no gain. Old settings backups simply lack the keys and the `Try*` helpers no-op.
- [ ] `IBlueBubblesApiService.GetThemeAsync` / `SetThemeAsync` / `DeleteThemeAsync` lose their only
  callers. Remove them from the interface, `BlueBubblesApiService`, and the two test stubs
  (`OutgoingMessageServiceTests`, `SyncServiceTests`) — consistent with the A1/A2 orphan sweeps.
- Known, accepted: a user with an existing theme backup on the server can no longer delete it from
  this client. It is inert server-side data; not worth solving.

 — mechanism fixed, **effect not yet measured**
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
