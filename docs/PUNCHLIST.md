# Punchlist

> Completed work lives in `docs/PUNCHLIST-ARCHIVE.md` — moved out to keep this file focused on
> what's left.

---

## Open bugs

#### B8. `DeleteMessageAsync` persists a soft delete and announces nothing
- [ ] Found during W1a. It sets `DateDeleted` and saves, but raises no `MessagesPersisted` and no
  reload — the open thread only updates because the caller happens to refresh afterwards. Same
  defect class as B1 and B6 (persistence and notification decided in different components), and a
  concrete instance of the audit's "9 of 25 paths" finding.
- [ ] Likely resolved by W1a-2 (single announcer). Check there first before writing a point fix.

#### B7. Photos render twice in a thread — duplicate attachment rows — **FIXED**
- [x] Merged `7354c4b` (`fe911bd` + `4a58b60`). Identity for an attachment is now
  **`(MessageId, OriginalRowId)`** with the GUID check kept as a strict superset, so the write path
  is a superset of the old rule. `AttachmentDeduplicator.CollapseDuplicatesAsync` repairs existing
  caches, run from `SyncService` (the cache has no version stamp — `EnsureCreatedAsync`, no
  migrations history — and the pass is idempotent).
- **Root cause, measured:** `originalROWID` is Apple's chat.db attachment ROWID and `guid` is
  Apple's GUID, both passed through verbatim by the server's `AttachmentSerializer`. **Apple
  rewrites the GUID as a transfer completes** (plain UUID -> `at_<n>_<messageGuid>`) while the ROWID
  stays put, so GUID-keyed dedupe stored the same photo twice. We never synthesize `at_N_`.
- **Verified independently by me against the pre-sync snapshot**
  (`Documents\bb-evidence\bluebubbles-presync-20260823-1119.db`):
  - The 59 groups are two populations: **18 are the bug** (same `MessageId`+`OriginalRowId`), and
    every one is **exactly one `at_N_` plus one plain GUID** (18 and 18) — strong confirmation of the
    Apple-rewrite mechanism. The other **41 groups / 82 rows** have distinct `OriginalRowId`s and are
    genuinely separate server attachments that must survive.
  - Repair on a copy using shipped code: `944 -> 926`, removed 18, second run removed 0,
    identity-dupe groups `18 -> 0`, and **`distinctPairs(926) == after.total(926)`** — every
    distinct `(MessageId, OriginalRowId)` survives exactly once.
  - 0 rows have a NULL `OriginalRowId`, so the GUID fallback is untested by real data.
- [x] ~~B2 (`e318fe1`) introduced or worsened this~~ **REFUTED** — affected rows date 2026-06-08 to
  2026-08-12 and B2 merged 2026-08-11; 17 of 18 predate it. That was my inference and it was wrong.
- [x] **Coverage hole found and closed in review.** A `TransferName` identity — the plausible wrong
  fix — **survived the full 420-test suite**, because the `OriginalRowId` pre-filter masks it in
  every synthetic case. It is not harmless: **9 messages in real data have a mixed shape** (a
  same-ROWID duplicate *and* a distinct attachment sharing the TransferName — 2671, 2735, 2738,
  2761, 2762, 4213, 4756, 4864, 5015), and under that rule msg 4213 drops from 3 rows to 1, losing a
  legitimate attachment. Shipped code handles all nine correctly. Added
  `Collapse_MessageWithBothADuplicateAndADistinctRow_CollapsesOnlyTheDuplicate` (`4a58b60`), which
  fails under the mutation.
- Narrow permanent instrumentation was re-added on the attachment write path (duplicate skipped /
  duplicate collapsed), gated behind verbose logging — B2e had removed the last tracing and we
  immediately lost the ability to see the next attachment bug from a log.
- [x] **Verified by the maintainer, 2026-08-23** (build off `4a24917`): photos render once in real
  threads, both on arrival and scrolling back through older threads. No Class B doubling observed
  either — so the 41 distinct-`OriginalRowId` groups are not visibly duplicating in practice, though
  that was not specifically hunted for.
- [ ] **Class B may still double-render, and would be a DIFFERENT bug.** Those 41 groups are two
  genuine server rows for the same file; collapsing them client-side would fight the server. If
  photos still double after this, that is the remaining cause.
- [ ] **Unexplained:** Class A begins abruptly at `OriginalRowId` 9022 / 2026-06-08 in a cache
  reaching back to 2025-08-02. Something changed then and it was not this codebase. Worth knowing
  before assuming the write-side fix is sufficient.
- Process note: the repair executed against the maintainer's **live cache** during the agent's
  build-and-run, i.e. unreviewed code mutated real user data pre-merge. It removed exactly the
  correct 18 rows and the snapshot predates it, so no harm — but agents must not run migrations
  against live user data before review.

#### B6. `chat-updated` socket events were never persisted — **FIXED**
- [x] Merged `cf376e5` (`65499c8` + `5146e3d` + `ccc34c4`). `ActionHandler` now parses the chat out
  of the payload's `chats[0]`; `ChatsService.ApplyChatUpdateAsync` is the single writer, going
  through `ChatFieldMerge.ApplyServerOwnedFields` and reconciling participant join rows —
  `LinkParticipantsAsync` for adds plus an explicit diff-and-delete for removals, since that helper
  only ever adds. A payload with no participants leaves membership alone; an unknown chat is
  ignored, since creation stays with `EnsureChatExistsAsync`. Drained on the existing serialized
  queue, so persistence stays in Core.
- **Original diagnosis, confirmed:** the four events fired `ChatUpdated` and nothing else — no DB
  write, no service call — with only two subscribers, one reading the database and one touching an
  in-memory label. `docs/PLAN.md:112` had stated the intent as "`group-name-change`,
  `participant-*` -> update chat in DB", so this was a spec deviation.
- **Refuted along the way:** the details-pane rename never worked either. The server emits these as
  a serialized *message* whose `chats[0]` is the chat; `ChatDetailsViewModel` deserialized the whole
  payload as a `Chat`, got the message's GUID, and its `chat.Guid != _chatGuid` guard always
  returned. Verified against the server source and the Flutter client, not guessed.
- **It did not self-heal while the app was open.** A delta only runs at launch, socket connect and
  network-change recovery, and `ReconcileChatsAsync` only prunes deletes — so with a healthy socket
  the stale name persisted indefinitely. Correctness bug, not latency.
- [x] **Caught in review — a regression this PR would have introduced.** `ApplyServerOwnedFields`
  copied `HasUnreadMessage` unconditionally and `Chat.HasUnreadMessage` was a non-nullable `bool`,
  so a group-event payload omitting `hasUnreadMessage` deserialized to `false` and **renaming a
  group cleared its unread badge**. No existing test caught it because every fixture stated the
  field explicitly. Fixed by making the model property `bool?` and guarding inside `ChatFieldMerge`
  — the authority, not a call-site exception. Silence now means "no opinion", not "read", which
  closes the same latent exposure in `SyncService` since it shares the merge.
- Verified by me, not accepted from the report: the failing test I pushed survived untouched;
  restoring the unconditional copy fails only it (410/411); an `&& false` guard fails only the two
  new theory cases (409/411); and the original client-only-field mutation is still caught under the
  nullable model (410/411). 411/411 on a clean build with a real launch.
- **Consequence for B4:** its debounce was throttling a reload that could not reflect its own
  trigger. The throttle was always correct; only now is it doing real work.
- [x] **Verified by the maintainer, 2026-08-23:** a real rename and participant change from another
  device landing
  on the conversation tile *and* in an open details pane.
- [ ] Follow-up, small: `ChatDetailsViewModel.RefreshParticipantsAsync` reads the in-memory
  `_chatsService.Chats`, so an open pane can lag one beat behind the now-correct database.
- Unverified reasoning, not measurement: that the server's `ChatSerializer` omits
  `hasUnreadMessage` for these events. The guard is correct either way.

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
- [x] **Orphans swept by A2:** `ContactColors.TintForKey` and
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
- [x] **Verified by the maintainer, 2026-08-23:** tile indicators showing unconditionally, threads
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
- [x] **Verified end-to-end by the maintainer, 2026-08-23** — a live inbound message lands in the list with the
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

#### A3. Remove the "Theme backup" section, fold its three keys into Settings backup — **DONE**
- [x] Merged `2d00f00`. The Theme backup card, its three handlers, `ThemeBackupName`, and the
  `GetThemeAsync`/`SetThemeAsync`/`DeleteThemeAsync` API methods (interface, implementation, both
  test stubs) are gone. `theme`, `colorfulAvatars` and `use24HrFormat` now ride in the settings
  payload, and the settings-restore path calls `ThemeHelper.Apply` so a restore re-themes the live
  window instead of waiting for a relaunch.
- [x] Verified: zero matches for `ThemeAsync|backup/theme|ThemeBackupName` anywhere in the source;
  no shared helper deleted (`RunAsync`, `Report`, `TryExtractPayload`, `TryBool`, `TryInt`,
  `TryString` are all still used by the settings path); clean build, 400/400 unchanged from
  baseline, launch confirmed by process (`Id 11408`, `MainWindowTitle "BlueBubbles"`).
- **Backward compatibility is a source read, not an executed test** — `BackupSettingsPage` is in
  `BlueBubbles.Windows` and unreachable from the suite (B2b). `TryBool`/`TryInt` return false before
  the caller's assignment when a key is absent, so an old backup leaves current values alone. Note
  `ThemeHelper.Apply` still fires on such a restore, harmlessly re-applying the unchanged theme.
- [x] **Verified end-to-end by the maintainer, 2026-08-23:** Settings > Backup rendering; a real save/restore
  round-trip proving the three keys persist server-side; the live re-theme on restore; and an actual
  pre-change backup restoring as a no-op.
- Accepted: an existing theme backup already on the server is now unreachable from this client.

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

#### B2h. Avatars decode twice — **FIXED**
- [x] Fixed in `4636a1f`, merged `15aa7cd`. One line: `AvatarControl.OnLoaded` now calls
  `QueueRelayout()` instead of `RefreshLayout()`. Generation guard, `_loadGeneration`, the cache and
  both decode paths untouched.
- **Root cause, measured — and it is NOT B2f's defect class.** `OnLoaded` ran `RefreshLayout`
  *directly*, bypassing the coalescer it was written for, while the relayout already queued by the
  binding's dependency-property sets was still pending. One bind therefore ran two full relayouts
  ~90-130 ms apart; the second found a cache MISS because the first decode was still in flight, so
  it decoded again and orphaned the first. Caller breakdown over a settle window: `Loaded` 70,
  `queued:DP:Initials` 32, `queued:DP:IsGroup` 5, `queued:DP:Size` 3, 60 coalesced.
- **Struck as refuted (both were my hypotheses, and both were wrong):**
  - ~~"the signature is identical to B2f / same defect class"~~ — B2f was one bind calling
    `LoadMedia` twice through an early-out blind to an in-flight decode. This is one bind running
    `RefreshLayout` twice. `_loadingMediaPath` was **not** transplanted and should not be.
  - ~~"the conversation list may build its tiles twice at startup"~~ — ruled out: every doubled pair
    carries the **same** `Avatar[N]` `_instanceId`, and a second list build would create new
    controls with new ids. Also not container recycling, not `Loaded`-after-rebind, and not the
    `ColorfulAvatars` `PropertyChanged` subscription.
- **Measured before/after** (same machine, same day, same chat set; recounted independently by me
  from `bluebubbles-2026-08-12.log`):

  | | before (11:47) | after (11:53) |
  |---|---|---|
  | `cache MISS -> clear+decode` | 52 | 19 |
  | `decode STALE` (discarded) | 26 | 4 |
  | `decode landed` | 26 | 15 |
  | `RefreshLayout` calls | 110 | 69 |

  Discarded decode work **50% -> 21%**. 400/400, clean build, launch confirmed by process.
- No unit test: the change is entirely in `BlueBubbles.Windows` and unreachable from the suite
  (B2b). Evidence is the counts plus the clean build and launch — not the 400/400, which covers
  none of this.
- [x] **Verified by the maintainer, 2026-08-23:** correct face for the correct person after a hard scroll of the
  conversation list and after navigating away and back. The change defers the initial render by one
  dispatcher tick, which is its only theoretical risk. `QueueRelayout` falls back to a synchronous
  `RefreshLayout` if `TryEnqueue` fails, so there is no path where an avatar never renders.

#### B2j. Four avatar decodes are still discarded per launch, deliberately
- [ ] The residual `STALE` decodes after B2h have a **different** cause: their two relayouts are
  only 10-20 ms apart and *both* come from queued items, because the bound properties arrive across
  two dispatcher ticks and the one-tick coalescing window cannot merge them. The relayout is
  legitimate; only the decode restart is waste.
- [ ] Closing it would take B2f's in-flight-path shape, but here it needs three fields (single +
  two group ellipses) on the recycled-container path — precisely where a wrong face would show.
  Judged not worth the correctness risk: a wasted decode is invisible, a wrong face is not.
  Reopen only if the remaining 21% ever matters.

#### B2k. An idle avatar control relayouts repeatedly with no user interaction
- [ ] `Avatar[14]` reached `gen=13` during a ~90-second idle window with nobody touching the app.
  Every relayout was a `cache HIT`, so it costs no decodes, but something is churning that
  control's dependency properties. Not investigated. Worth finding out *what* is republishing tile
  properties while idle — the answer may not be about avatars at all.

#### B2i. `build-common.ps1` crashed when exactly one app instance was running — **FIXED**
- [x] Fixed in `f1214b2`: `$procs.Count` -> `@($procs).Count` in `Stop-BlueBubbles`. Under
  `Set-StrictMode -Version Latest` (`build-common.ps1:27`) a single `Process` is a scalar and has no
  `.Count` in PowerShell 5.1, so the clean step threw exactly when it most needed to kill the
  lock-holder. Zero or two-plus instances were unaffected.
- [x] Verified end to end, not just in isolation: with one live instance, `build-and-run.ps1` now
  prints `Stopping 1 running BlueBubbles instance(s)...` — the statement that used to throw — then
  cleans, builds, and passes 400/400. Negative control reproduced first (`n=1 -> THREW`,
  `n=2 -> OK`).
- [x] Swept all three `.ps1` for the same pattern; this was the only occurrence. `publish.ps1` shares
  the function and is fixed with it. Re-checked pure-ASCII (0 non-ASCII bytes) and 0 parse errors
  across `build-and-run.ps1`, `build-common.ps1`, `publish.ps1`.


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

#### B4. `ConversationListViewModel.OnChatUpdated` was `async void` with an unthrottled full reload — **FIXED**
- [x] Merged `345ef87`. The handler no longer runs `LoadChatsAsync()` directly on every
  group-name/participant socket event, and it can no longer throw unobserved; it routes through a
  debounce matching B1's pattern.
- Note recorded during B6: until B6 landed, this debounce was throttling a reload that could not
  reflect its own trigger, because nothing persisted the `chat-updated` payload. The throttle was
  always correct; it only started doing real work once B6 merged.

#### B5. Tests wrote to the real `%LOCALAPPDATA%\BlueBubbles\logs` — **FIXED**
- [x] Merged `87f961f` (`2333bb2`). `AppLog.RedirectLogDirectory(string)` (guarded by the existing
  `_fileLock`, resets `_currentLogDate` so the new directory gets created and pruned on next write)
  plus a `[ModuleInitializer]` in the test assembly pointing the suite at
  `%TEMP%\BlueBubblesTests\logs-<guid>`, with best-effort cleanup on `ProcessExit`.
- **Why `[ModuleInitializer]` and not a fixture:** `_logDir ??=` caches on first use and xunit runs
  collections in parallel, so any fixture or collection hook can lose the race. Module init runs at
  assembly load, before discovery.
- **Deliberately not done:** no "disable file logging" flag — a production-only off switch can ship
  accidentally on, and it would stop the suite exercising `WriteToFile` at all. Redirecting keeps
  the real write path under test.
- **The fixture's `[ERROR] ... injected save failure` line was NOT silenced.** It is the assertion in
  B1's `FailedEvent_IsLoggedAndQueueKeepsDraining`; only where it lands was the problem.
- Verified by me, both directions: on the fixed branch a full suite leaves the real log
  byte-identical (308880 bytes / 3069 lines / 77 `injected` before and after, 413 passed); on
  unfixed `main` the same run grows it by **2928 bytes** (411 passed). Nothing changed in
  formatting, levels, retention, `MaxEntries`, `EntryAdded` or `Entries`.
- Mutation worth keeping in mind: pointing `_logDir` at a *wrong* temp path (not merely removing the
  redirect) still fails the location test, which is what makes the two tests non-redundant.
- [ ] Untested by design, declared not dressed up: the `_currentLogDate = DateTime.MinValue` reset —
  the suite never calls `Initialize()`, so nothing distinguishes its presence. Midnight rollover
  during a run is also unverified.
- **Audit finding (clean):** `AppLog` was the only leak into real user state. Two `GetFolderPath`
  sites exist in Core (`AppLog`, `SettingsService`); the SQLite and attachment paths live in
  `BlueBubbles.Windows`, which the test project does not reference; all 12 `new SettingsService(...)`
  sites pass a temp file; DB tests use in-memory/`TestDbContextFactory`.

---

## W — Structural work  *(from the 2026-08-23 architecture audit at `34f439f`)*

> **Decision (maintainer + head engineer, 2026-08-23): no rewrite.** The audit was commissioned to
> test whether core components needed restructuring, partly for reliability and partly so the app
> could be ported from the BlueBubbles client-server model to a native iMessage stack
> (rustpush / OpenBubbles-style). It found that the fragility is specific and measurable, not
> pervasive, and that portability debt is small. Rewriting the tested half (Core, 72% line coverage)
> while the untested half stays untested was rejected. **Do not re-open this as "should we rewrite"
> without new measurements.**

**The audit's load-bearing numbers** (verified independently by the head engineer; line numbers
drift a few lines because B7 merged after `34f439f`):

| | |
|---|---|
| Writers per entity | Attachments **1** (shared helper). MessageEntity **5**, ChatEntity **13**, Handle **6**, ChatParticipant **6** — no shared helper. |
| Persistence vs notification | Decided in *different components* on **9 of 25** inbound paths. |
| Locking | **34 of 42** `SaveChangesAsync` outside the only lock (`MessagesService._saveLock`, 7 regions). Three message-persistence implementations; the lock covers one. No WAL, no `busy_timeout`. |
| Test reach | `BlueBubbles.Core` 0.7227 line-rate. `BlueBubbles.Windows` **absent from the coverage report entirely** — not 0%, uninstrumented, because the test project does not reference it. 10,514 C# + 2,801 XAML lines = 58.0% of production C#. |
| Port seam | 33 files would change. True leakage: **43 references across 10 files**. |

**Why this is the right target:** four of this release's eight bugs — B1 (persisted, never notified),
B2 (a writer that didn't write attachments), B6 (a writer that didn't write chat updates), B7 (two
writers, two identity schemes) — are all the same shape. The one entity with a single writer through
a shared helper is Attachments, which is also the one just proven correct in B7. That is the
template.

#### W1. One writer per entity, in Core
- [x] **W1a. MessageEntity persistence — DONE** (`2c28e00`, merged `0e21ecc`). All field assignment
  now lives in `MessagePersistenceHelper`: `ApplyServerFields` is the single definition of the
  36-field message field list, and the update, soft-delete and reaction-parent mutations moved into
  the same file. Insert-only vs upsert semantics, identity (server GUID) and the `IsBookmarked`
  client-ownership rule are unchanged. 423/423, Core line-rate 0.7227 -> 0.7241. Clean
  `build-and-run.ps1` with a real launch on trunk.
  - [x] **Live message flow verified by the maintainer, 2026-08-23:** send, receive, edit, unsend,
    tapback and reply all correct. That was the risk this refactor carried — it rewrote where every
    message field is assigned — and it is now closed by observation, not inference.
  - **The audit's enumeration was incomplete: MessageEntity had 6 writers, not 5.** The agent found
    `MessagesService.SaveReactionAsync` setting `parent.HasReactions = true` on the parent row.
  - **Identity was already uniform** — the server GUID on all six. No B7-shape hazard existed here;
    `temp-` GUIDs are only ever excluded, `OriginalRowId` is only used for ordering.
  - Verified by me: mutating the `if (isNew)` guard on `IsBookmarked` so an upsert copies it from the
    server fails `RefreshLatest_PreservesLocalBookmark_StillAppliesServerText` — the CLAUDE.md
    client-ownership rule is defended by an existing test. Dropping `DidNotifyRecipient` from the
    consolidated writer fails **only** the new
    `LiveInsertAndServerUpsert_WriteTheSameFieldsFromTheSamePayload` (422 pass) — the pre-existing
    421 tests missed a dropped field entirely, which is the exact B2 shape this closed.
  - Good methodology worth copying: the negative control for a refactor was to run the new tests
    against **pre-refactor** `35ab22e` (423/423), proving they are genuine characterization tests,
    *then* mutate the old code to expose the hole.
- [ ] **W1a-2. Single announcer for messages** — carved out of W1a deliberately. Consolidating
  announcement into the writer **cannot** be done without a behaviour change: `SaveMessagesAsync`
  has 4 callers, of which `MessagesService.cs:111` and `:166` announce nothing at all, while
  `SyncService.cs:344` / `:482` announce once per chat per batch. Moving the call into the writer
  changes `MessagesPersisted` from per-batch to per-call and adds events to paths that intentionally
  have none, driving `ConversationListViewModel.ScheduleReloadFromDatabase`. Verified: exactly 4
  production `NotifyMessagesPersisted` call sites. **Do this after W1b/W1c**, with its own evidence.
- [ ] **W1b. HandleEntity (6) and ChatParticipant (6)** — spread across `MessagePersistenceHelper`,
  `ChatsService`, `MessagesService`, `SyncService`. One removal site total, in `ChatsService`.
- [ ] **W1c. ChatEntity (13)**, including the two construction sites that bypass
  `ChatFieldMerge.ApplyServerOwnedFields` — confirmed at `ChatsService.cs:258` and
  `SyncService.cs:551`. Both are insert paths today; the audit could not determine whether either
  can run against a row that already holds client-only state (e.g. after a soft-delete/resurrect).
  **CLAUDE.md hard rule applies.**
- Note from W1a for W1b/W1c: `MessageWindowReconciler` is now *policy* over the writer (which rows
  are gone), not a field writer. The same split should fall out for chats.
- [ ] Expected side effect: the locking exposure largely dissolves. "Which of three writers needs the
  mutex" is hard; "my one writer takes it" is not. Do **not** attack the 34-of-42 figure directly
  first.
- [ ] Not measured: whether the unlocked saves actually collide at runtime. SQLite's default
  rollback-journal locking may have been absorbing it. Worth instrumenting, not worth assuming.

#### W2. Transport leakage cleanup — 43 references, 10 files
- [ ] The genuinely wrong ones, in priority order: `ChatViewModel.cs` emitting **raw wire strings**
  (`"started-typing"` / `"stopped-typing"`) at ~L668/675/687; the `SocketState` connection-banner
  cluster in `ConversationListViewModel` + `ConversationListPage.xaml.cs` +
  `ConnectionSettingsPage.xaml.cs`; `BackupSettingsPage.xaml.cs` taking
  **`ApiResponse<JsonElement>` in a UI method signature**; `AboutSettingsPage.xaml.cs` resolving
  `IBlueBubblesApiService` from the container in view code-behind; `ChatDetailsViewModel.cs:343`
  branching on `SocketEvents.GroupNameChange`; and `ConversationListViewModel` downcasting
  `_socketService is ObservableObject`.
- [ ] Excluded deliberately: DI registration in `App.xaml.cs` (a composition root naming concrete
  transports is what it is for) and the setup / server-management UI (genuinely
  BlueBubbles-specific).
- [ ] **Larger, deferred:** wire DTOs are used directly as the domain model — every file in
  `Core/Models` except `SocketEvents.cs` carries `[JsonPropertyName]`, and `Message`/`Chat`/`Handle`/
  `Attachment` are what the ViewModels bind against. That is the real portability debt. Not now.

#### W3. Dead or staged transport scaffolding
- [ ] Three socket events are registered and raised with **zero subscribers**:
  `incoming-facetime`, `ft-call-status-changed`, `imessage-aliases-removed`.
- [ ] `IFaceTimeService` and `IFindMyService` exist with **no implementation registered** in
  `App.xaml.cs`. Dead or staged is currently indistinguishable from source — decide and record which.

#### W4. Sequencing note (supersedes earlier advice)
- The head engineer previously said B2b (UI testability) must come first, on the grounds that you
  cannot refactor what you cannot test. **That was wrong for this work:** W1 lives in Core, which is
  at 72% line coverage and where every mutation test this session has bitten. B2b is not a
  prerequisite. Order: **W1 -> W2 -> B2b**.
- The audit found the UI layer **untested**, not **wrong**. Chat list, chat thread, avatars and chat
  details feeling fragile is a coverage problem (it is why B2f, B2h, A3 and B2g shipped on log counts
  and eyeballs). Rewriting them would produce new untested code, not fewer bugs.

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

#### F5. Toast actions: two reactions + mark as read  *(target 0.24.0)*
- [ ] Today the toast emits `AddTextBox` + **Send** + **4** tapbacks (Love, Like, Dislike, Laugh) =
  5 buttons, the Windows ceiling (`NotificationService.cs:25`, `:172-186`). Change to **Send + 2
  reactions + Mark as read = 4**, which fits with a slot to spare.
- [ ] **Mark as read must not foreground the app.** Saving a click is the whole point; if it raises
  the window it has cost one instead. `IChatsService.MarkChatReadAsync(chatGuid, read,
  notifyServer)` already exists.
- **Do not claim this buys back the OS "Copy code" affordance.** B3 measured that the button budget
  is *not* what suppresses it — the OS affordance renders in its own row, outside both the 5-action
  and 5-input budgets. Freeing a slot changes nothing there.
- [ ] Check whether server-side mark-as-read requires Private API (`PrivateMarkChatAsRead` exists as
  a preference) and what should happen when it is unavailable.

#### U1. In-app updater  *(target 0.24.0)*
- **Measured 2026-08-23:** no update code exists anywhere (0 matches for
  `UpdateService|CheckForUpdate|releases/latest|api.github.com`). `AppInfo.Version` already returns a
  clean 3-part version from the entry assembly, unpackaged-safe — that is the local-version source.
- [ ] Use the GitHub **`/releases/latest`** endpoint: it excludes drafts and prereleases, which
  matters because this project cuts drafts routinely and has published-then-deleted a release before.
- [ ] **Verify the download before executing it.** The GitHub release API returns a per-asset
  `digest` (`sha256:...`) — confirmed present on this repo's assets. An installer that is downloaded
  and run without a hash check is a remote-code-execution path into the user's machine.
- [ ] Asset is `BlueBubbles-Setup-X.Y.Z-x64.exe`; **x64 only** (arm64 is blocked, see S1).
- [ ] Never auto-install. Surface "update available", let the user choose, and expect the SmartScreen
  prompt because the installer is unsigned.
- [ ] Version comparison must be semantic, not string — tags are `vX.Y.Z`, `AppInfo.Version` is
  `X.Y.Z`, and `0.9.0` vs `0.10.0` breaks a string compare.
- [ ] Unauthenticated GitHub API is rate-limited (60/hr/IP); a check on launch is fine, a poll is not.
- [ ] No package-identity APIs (CLAUDE.md hard rule).
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
