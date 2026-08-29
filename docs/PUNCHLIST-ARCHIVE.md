# Punchlist Archive

Completed `docs/PUNCHLIST.md` items, pulled out to keep that file focused on what's left. This is
engineering history — the *why* behind fixes already shipped, kept for when the same shape of bug
resurfaces. Ordered oldest to newest. For the public-facing GitHub Release body format, see
`docs/release-notes.md` instead.

## Pre-0.20.2 — Phase 6 and Debug Session 2

Detail in git history. Phase-6 items 1–33, plus Debug Session 2 clusters **D**
(diagnostics/logging), **H1** (repo hygiene), **N** (notifications), **S** (sync reliability),
**L** (layout/animation), **AT1** (image flicker — incl. the scroll-recycle follow-up:
decoded-bitmap LRU cache so recycled bubbles re-show inline images synchronously), **UN**
(uninstall/reset cleanup), **A** (avatars — generic person glyph + info-bar avatar mirrors the
list), **AT2** (in-app video playback via `MediaPlayerElement` with external fallback), **34**
(GH Actions release workflow: `dotnet test` + `publish.ps1 -Platform x64`, draft `v<version>`
Release with installer attached), and **35** (flaky `Reaction_FromOther_PersistedAndNotifies`
test).

## 0.20.2 — bugfix release (B1–B4)

Stray `Ctrl+N` tooltip on first conversation hover (`KeyboardAcceleratorPlacementMode="Hidden"`
on ShellPage's root Grid); group-chat info Back button needing multiple clicks (duplicate
`DetailsRequested` subscriptions on the cached `ChatPage`, fixed with a `-=`/`+=` guard); avatar
bubble flickering on contact reload (`LoadFromVCardAsync` now reuses the same `byte[]` reference
for unchanged photos — `StablePhoto`); installer not closing the running app during update
(`PrepareToInstall` `taskkill`s the instance before copy, unblocking U1).

## 0.20.3 — debug-pass fixes (DP1–DP5)

Contact reload could transiently blank avatars/names mid-reload (atomic dictionary swap in
`ContactResolverService`); link-preview hero images missed the stale-callback generation guard
(`UrlPreview.LoadRemoteHero`); `publish.ps1 -Platform arm64` built silently despite S1 (now needs
`-AcknowledgeBroken`); three silent `catch { }`s now logged (`ChatsService`, `SyncService`,
`SocketService`); small leaks/doc rot (deterministic `SoftwareBitmap` dispose in
`MessageComposer`, CLAUDE.md corrections, `App.xaml.cs` DPAPI comment fix).

## 0.20.4 — bugfix release (B5–B9)

Ctrl+Click deselect left the tile highlighted — ListViewBase applies its own click-selection
*after* `ItemClick`, re-selecting what the handler cleared (deferred re-clear via
`DispatcherQueue` in `ConversationListPage`); chat and message deletes never reached the server
so the next sync resurrected them — now server-first (`ChatsService.DeleteChatAsync` /
`MessagesService.DeleteMessageAsync` call the existing wire-correct API endpoints and only mutate
the cache on success; failures surface via `ContentDialog`, chat delete gained a confirmation
dialog since it's now destructive server-side); new-chat "To" field kept partial text after
picking a suggestion (VM→TextBox sync via the existing `PropertyChanged` switch); repeated "New
message" clicks stacked NewChatPage back-stack entries (dedupe in
`ShellPage.OnNewChatRequested`, reset-in-place with a discard-draft confirmation); composer
placeholder hardcoded "iMessage" (now reflects `Chat.Service` — "Text Message" for non-iMessage,
never "SMS" since the server doesn't distinguish SMS from RCS).

## 0.20.4 — B10 (follow-up to B6, same release)

Drafting a new message to a contact whose chat had just been deleted silently failed and the
conversation never (re)appeared in the list. Root cause chain: `FindExistingChatGuid` matches by
participant *address*, so it can return a stale local row whose chat no longer exists
server-side (relic rows from old syncs survive — verified live: local row's guid 404'd on the
server); `SendToExistingChatAsync` then sent to the dead guid and **ignored the API response** —
no error surfaced, `ChatReady` fired as if sent, and nothing bumped the chat's
`LatestMessageDate`, so even a successful send to a stale tile stayed buried at its old sort
position. Fix (`NewChatViewModel`): the existing-chat path now checks every response — on a
clean rejection *before anything was delivered* it falls back to `chat/new` (which
creates/returns the canonical chat and self-heals the relic); on success it calls
`ChatsService.HandleNewMessageAsync` (bump sort date, undo soft-delete, reload list) instead of
relying on the not-guaranteed self-echo; partial failures are logged, never retried (no
double-send). Not unit-tested: lives in the WinUI project, which the `net8.0` test project can't
reference (see T1).

## 0.21.0 — bugfixes (B11–B15)

Danger Zone reset left stale/cross-wired in-memory caches re-syncing into the wiped DB — reset
now relaunches the process via `AppInstance.Restart` (unpackaged-safe; tray icon removed first
since Restart skips Closed handlers; in-place SetupPage navigation kept as the failure fallback;
dead `ResetRequested` event removed). "Scroll to bottom" FAB stuck visible on short threads —
`ViewChanged` only fires on *offset* changes, so a transient `ScrollableHeight > 0` during initial
layout never got re-evaluated; `ChatPage` now registers a property-changed callback on
`ScrollViewer.ScrollableHeight` and re-runs the `IsNearBottom()` rule whenever it changes. Sent
image vanished after navigating away/back — the locally-picked file never entered the attachment
cache under the *server* guid; `IAttachmentCacheService.SeedFromLocalFileAsync` copies it in when
the send response returns the real attachment guid (wired in `OutgoingMessageService` + both
direct-send paths in `NewChatViewModel`; covered by `SentAttachment_SeedsCacheUnderServerGuid`).
Blank tile preview for attachment-only last message — new `MessagePreview.Derive` (Core/Utils)
strips U+FFFC placeholders and falls back to "Image"/"Video"/"Audio Message"/"Attachment"
(pluralized) from attachment mime types; applied in `ChatsService.LoadChatsInternalAsync`
(last-message query now `Include`s attachments), `IncomingMessageProcessor`, and
`NewChatViewModel` (unit-tested). Tray icon gone for good after an Explorer restart —
`MainWindow.WndProc` now watches the registered `"TaskbarCreated"` broadcast and
`SystemTrayService.HandleTaskbarCreated()` re-runs `Shell_NotifyIcon(NIM_ADD)`.

## 0.21.0 — F1 and F3 (scheduled send, taskbar badge)

F1 — the BlueBubbles *server* owns all timing (persists scheduled messages, arms a `setTimeout`
per message, sends through the normal private-api path at fire time); it's a queue, not Apple's
native iOS 18 "Send Later" (unreachable via the private API) — client is CRUD-only, no
client-side timer, and the fired message arrives in-thread via the normal `new-message` socket
path (no tempGuid, reuses the existing out-of-order delayed-emit logic). Pending sends render
Apple-style as a dashed-outline bubble pinned at the bottom of the thread
(`ChatViewModel.ScheduledItems`), replacing an earlier hidden-queue dialog surface (removed).
Recurring schedules and scheduling a reply were descoped to F4 (see `docs/PUNCHLIST.md`).

F3 — the "modern" `BadgeUpdateManager` badge API is **not viable** here (package-identity-only,
throws unpackaged, no AUMID-only escape hatch like `AppNotificationManager` has for toasts);
`ITaskbarList3.SetOverlayIcon` + `BadgeIconRenderer` is the only correct mechanism. Rewritten
from raw GDI (no anti-aliasing/alpha) to GDI+ (`System.Drawing.Common`, pinned 8.0.x to match
the net8 TFM): true 32bpp ARGB, anti-aliased Windows 11 badge red (#C42B1C), 4x supersampled +
downscaled, DPI-aware sizing. The HICON is built manually via `CreateIconIndirect` over a 32bpp
DIB + alpha-derived 1bpp AND mask — **not** `Bitmap.GetHicon()`, which collapses alpha to 1-bit
and re-creates a jagged edge.

## 0.21.1 — B16

Chat deletions made on the server or another device never propagated down — sync only ever
upserted chats, so a chat deleted elsewhere lingered in the list forever (the server emits no
chat-deleted socket event, and a deletion rides in no message delta). `SyncService` now
reconciles against the server's chat list as the single source of truth: a new
`ReconcileChatsAsync` pages the bare `chat/query` list on every incremental delta (launch, socket
reconnect, sleep/network recovery) and `PruneDeletedChatsAsync` soft-deletes any local chat the
server no longer returns (soft, not a row removal — mirrors the empty-chat prune and is
reversible: a returning chat keeps its deterministic iMessage GUID and is resurrected by
`EnsureChatExistsAsync`/`HandleNewMessageAsync` on the next message, history intact). Full sync
prunes too, reusing the authoritative list it already paged. Absence-based pruning is guarded
against mass-wipe on a flaky fetch — any non-2xx/throw on a page aborts the prune entirely, and
an empty list is trusted only when `chat/count` independently confirms zero. Covered by
`SyncServiceTests` (prune on incremental + full sync, the genuine all-deleted path, and both
safety guards).

## 0.21.2 — B17–B19, server-authoritative sync hardening

A follow-on to B16, closing the remaining places the cache treated itself as the source of truth.

**B17 (client-only state clobbered):** the server has no endpoint for pin/mute/archive (only
read/unread), so it returns the defaults — and every chat upsert blindly copied them, silently
un-pinning/un-muting/un-archiving the user's chats on each sync (message `IsBookmarked` too).
Field ownership is now centralized in `ChatFieldMerge.ApplyServerOwnedFields` (the single
authority all chat upserts call); client-owned fields survive by omission and `IsBookmarked` is
insert-only.

**B18 (remote message deletes/edits never synced down):** sync only ever upserted, and the
incremental delta keys on ROWID, which an edit/unsend/delete to an already-synced message
doesn't bump — so those changes made elsewhere never arrived. New `MessageWindowReconciler` makes
a fetched window authoritative for its range (chat-open `RefreshLatestFromServerAsync` and full
sync now soft-delete what the server omitted), plus a count-gated `UpdatedSinceSweepAsync`
(`AppSettings.LastUpdatedSync`) catches in-place edits to older messages. A `protectedGuids` guard
on `SaveMessagesAsync` keeps a true-up from clobbering an un-acked local mutation.

**B19 (chat-delete reconcile didn't fire while the app stayed open):** B16 only reconciled on
launch/reconnect/sleep-resume; with the window left open a conversation deleted elsewhere
lingered until restart (`Window.Activated` is unreliable here — missed at launch, and tray
hide/show via Win32 bypasses it). A public lean `ISyncService.ReconcileChatsAsync` (GUID-diff
only, reloads the list only if it pruned) now runs on an always-on poll gated by
`GetForegroundWindow` (`IWindowStateService.IsWindowFocused`), on `RestoreFromTray`, and on
focus-regain — throttled, and skipped while a full delta is in flight. Shipping alongside: a
one-time upgrade **heal** (`SyncService.RunHealIfNeededAsync` + `AppSettings.SyncModelVersion`)
that runs a delete-aware full true-up on first launch to converge caches an older build left
stale. The durable send outbox (would protect a send across a crash and add retry) was scoped,
built, then **deferred** — it rewrites the send path and can't be E2E-verified without a live
server; design kept for a focused pass.

Covered by `SyncServiceTests`/`MessagesServiceTests` (field preservation, window soft-delete,
updated-since edit, heal-once, foreground reconcile).

## 0.22.5 — security fix (SEC1)

**SEC1 (server password persisted in cleartext):** `SettingsService.Save` wrote
`ServerConfiguration.Password` straight into `%LOCALAPPDATA%\BlueBubbles\settings.json`, so the
BlueBubbles server password sat on disk in plain text next to the DPAPI-encrypted
`credential.bin` that was supposed to be its only home — readable by any process running as the
user, and easy to leak via a copied settings file or a support screenshot. `Save` no longer emits
the field at all (`PersistedSettings.Password` is now write-null + `[JsonIgnore(WhenWritingNull)]`,
kept only as a read path), and `Load` migrates an existing cleartext value into
`ICredentialService` (an entry already in the store wins, being the newer of the two) before
rewriting the file to drop it. `Load` also only seeds `ServerConfiguration.Password` from the
legacy field when it is actually present, so an already-migrated file can't blank the password
`App.xaml.cs` restored from DPAPI. Covered by `SettingsServiceTests` (round-trip asserts the
secret never reaches disk; two migration tests) and a `DependencyInjectionTests` case that
resolves the concrete `SettingsService` from the container, since its new credential parameter is
optional. Verified against a live install: the cleartext value was gone and `[Socket] Connected`
still succeeded off the DPAPI copy.

## 0.22.x Debug Session 3 — B2: inbound attachment images never rendered

Symptom: an inbound image showed as a bare bubble with only a timestamp. It appeared only after
clicking "fetch latest" AND switching to another thread and back.

Six suspects were audited against source first; four were refuted and are recorded so they are not
re-investigated: bubble double-append (`ReconcileVisibleBubblesAsync` never calls `Items.Add`, and
both `AppendMessageBubbles` call sites dedupe by GUID); `AttachmentHolder` bypassing
`_bindGeneration` (the generation is bumped and `MediaImage.Source` nulled before the cache probe,
and `ChatBubble.BuildContent` discards holders rather than reusing them); `ImageLoader` LRU key
collision (key is `$"{path}|{decodePixelWidth}"`); `TriggerAutoDownload` swallowing errors
(`DownloadInternalAsync` converts every failure to `State = Error` + `ErrorMessage`).

**Root cause:** `MessagePersistenceHelper` was the only place in the codebase that wrote attachment
rows, and the live socket save path did not go through it. `SaveMessageCoreAsync` stored
`HasAttachments = true` with zero rows, so the cache held a message flagged as having an attachment
with nothing attached. "Fetch latest" fixed the data (it runs `RefreshLatestFromServerAsync` ->
`MessagePersistenceHelper.SaveMessagesAsync`); the thread switch was needed separately because that
is what rebuilds `Items` via `LoadMessagesAsync`, which `.Include`s attachments.

**Fix** (PR 4, merged `e318fe1`): the attachment loop was extracted as
`MessagePersistenceHelper.SaveAttachmentsAsync` — one writer, dedupe unchanged — and
`SaveMessageCoreAsync` calls it after the message row saves. `FirstAsync` became
`FirstOrDefaultAsync` + skip (the socket path can run for a message that lost a concurrent-insert
race) and the `DbUpdateException` catch returns, since the winner owns the attachment write.
Rejected: routing `SaveMessageCoreAsync` wholesale through `SaveMessagesAsync` — it upserts where
this path skips on an existing GUID, and `SaveReactionAsync` shares the method.

Notes worth keeping: `AttachmentEntity.Guid` carries a unique index, so the dedupe protects against
a thrown exception rather than against a duplicate row. Hypothesis (d) — `MessageBubbleViewModel`
captures attachments at construction and never rebuilds them — is real but off the critical path,
because `ChatViewModel` copies `e.Message.Attachments` into the in-memory entity for the on-screen
bubble.

**Confirmed live 2026-08-11**: four inbound images each went toast -> auto-download -> decode ->
done, including an adversarial run against a deleted-and-recreated thread. No
`HasAttachments=true but 0 attachment rows` and no re-append warning in the whole session.

### B2a. Verbose logging was dead code

`AppLog.MinLevel` never read `AppSettings.VerboseLogging` at startup and the About > Diagnostics
toggle was `Visibility="Collapsed"`, so every `AppLog.Debug` in the app — including the pre-existing
avatar tracing — was dropped unconditionally. Restored in `f973010`; off by default. This is what
made B2 diagnosable at all, and it is why the toggle should stay.

## 0.23.0 — Debug Session 4 (A1-A3, B1, B2e, B2f, B2h, B2i, B3-B6)

### B1. New/updated messages never reached the conversation list

Messages persisted fine; the *event contract* was broken. Six causes, all fixed in PR 3
(`fix/sync-ui-propagation`): a silent bare catch in `IncomingMessageProcessor.ProcessAsync`;
`ChatsService.HandleNewMessageAsync` no-opping on an unknown chat; `ProcessUpdatedMessageAsync`
raising no event; `ConversationListViewModel` never subscribing to `MessagesPersisted`;
`EnsureChatExistsAsync` not refreshing the in-memory list; a silent participant-fetch failure.
`UpdateMessageAsync` now returns the owning chat GUID. A mutation of the owning-chat lookup
(`ChatId ± 1`) originally **survived** — closed by
`UpdateMessage_ReturnsOwningChatGuid_NotJustTheFirstChat` (`4fa277d`). Maintainer-verified live.
Deliberately unchanged: reactions raise no persist event (nothing list-visible changes).

### B2e. Removed the `[attach-diag]` instrumentation

Deletion-only, `b609d4a` merged `2cdd29e`; `MessageBubbleViewModel.cs` resolves back to its
pre-instrumentation blob `b635787`. The O(items) `Any(...)` scan in `AppendMessageBubbles` was
deleted wholesale rather than just its log line. B2a keepers left intact. **Lesson applied later in
B7:** this removed the last attachment tracing, and the next attachment bug was immediately harder
to see from a log — B7 re-added narrow permanent instrumentation behind verbose logging.

### B2f. Every attachment image decoded twice

`91d99a2`, merged `5d3a7d1`. `LoadMedia`'s early-out was `vm.LocalPath == _renderedMediaPath &&
MediaImage.Source is not null`; while the first decode was in flight `_renderedMediaPath` was set
but `Source` was still null, so a second call could not short-circuit. A `_loadingMediaPath` field
closes the window, cleared on rebind/retry/error/cache-hit and in a guarded `finally`. Measured
10 decode starts / 5 stranded -> 4 starts / 0 stranded. The two legitimate second callers: `Loaded`
firing ~46 ms after `DataContextChanged`, and `DownloadInternalAsync` raising `PropertyChanged`
twice (`LocalPath`, then `State`, ~3 ms apart).
**Refuted, do not re-investigate:** the `at_0_` optimistic-send lead (`at_N_` is just the attachment
index; asymmetry was cache state); "a systemic double-bind" (it is a second `LoadMedia` call, not a
second bind); "the list may not be virtualizing" (`ItemsStackPanel` virtualizes fine — off-screen
downloads came from view-model construction, which is B2g).

### B2h. Avatars decoded twice

One line, `4636a1f` merged `15aa7cd`: `AvatarControl.OnLoaded` calls `QueueRelayout()` instead of
`RefreshLayout()`. `OnLoaded` had run `RefreshLayout` *directly*, bypassing the coalescer it was
written for while a relayout queued by the binding's DP sets was still pending — one bind ran two
full relayouts ~90-130 ms apart, the second finding a cache MISS because the first decode was still
in flight. Discarded decode work **50% -> 21%** (`decode STALE` 26 -> 4; `RefreshLayout` 110 -> 69).
**Not B2f's defect class** — `_loadingMediaPath` was not transplanted and should not be. Also
refuted: "the conversation list builds tiles twice at startup" (every doubled pair carries the same
`Avatar[N]` `_instanceId`). Residual deliberate waste is B2j.

### B2i. `build-common.ps1` crashed when exactly one app instance was running

`f1214b2`: `$procs.Count` -> `@($procs).Count` in `Stop-BlueBubbles`. Under `Set-StrictMode -Version
Latest` a single `Process` is a scalar with no `.Count` in PowerShell 5.1, so the clean step threw
exactly when it most needed to kill the lock-holder. Zero or two-plus instances were unaffected.
Negative control reproduced first (`n=1 -> THREW`, `n=2 -> OK`). Swept all three `.ps1`; only
occurrence. `publish.ps1` shares the function and was fixed with it.

### B3. OTP toast has no "Copy code" button — WON'T FIX, upstream gap

**Decision (2026-08-11, maintainer):** do not ship a client-side OTP detector. Detecting one-time
passcodes in notification text is the platform's job; carrying our own heuristic means owning it
forever to patch an OS gap affecting one sender's phrasing. **Do not re-litigate by pointing at the
detector's accuracy — the objection is permanent ownership, not code quality.**
`OtpDetector` over the real cache: 5,639 messages, 5,282 with text, 16 flagged (15 distinct) in 6
shape clusters; Windows' own affordance covered **9/15 messages, 4/6 clusters**. The gap is exactly
`Enter <adjective> code N` — `code` as the object of an imperative verb with a modifier wedged in —
and **both misses are one sender** (Wells Fargo), so the 40% figure is fragile.
**Refuted:** that our button budget suppressed the pill (a toast allows 5 actions *and* 5 inputs
independently; the OS pill renders in its own row outside both, and showed with all five slots
full); and "Windows covers this anyway", which came from ten *invented* textbook strings showing
9/10 — **never validate a pattern matcher with invented data.**
The tempting "only add our button when Windows would miss it" is undeliverable: you cannot ask
Windows at runtime, so it means modelling an undocumented OS heuristic whose failure mode is silent.
Research branches are **NOT FOR MERGE**: `experiment/otp-toast-windows-affordance` (`98ef65a`),
`research/otp-real-corpus` (`b6c8cce`).

### B4. `OnChatUpdated` was `async void` with an unthrottled full reload

`345ef87`. The handler no longer runs `LoadChatsAsync()` on every group-name/participant socket
event and can no longer throw unobserved; it routes through a debounce matching B1's pattern. Until
B6 landed this throttled a reload that could not reflect its own trigger — the throttle was always
correct, it only started doing real work once B6 merged.

### B5. Tests wrote to the real `%LOCALAPPDATA%\BlueBubbles\logs`

`87f961f` (`2333bb2`). `AppLog.RedirectLogDirectory(string)` plus a `[ModuleInitializer]` in the
test assembly pointing the suite at `%TEMP%\BlueBubblesTests\logs-<guid>`. **`[ModuleInitializer]`
and not a fixture** because `_logDir ??=` caches on first use and xunit runs collections in
parallel, so any fixture hook can lose the race; module init runs at assembly load, before
discovery. Deliberately no "disable file logging" flag — a production-only off switch can ship
accidentally on and would stop the suite exercising `WriteToFile`. Verified both directions: fixed
branch leaves the real log byte-identical (308880 bytes), unfixed `main` grows it by 2928 bytes.
Declared untested, not dressed up: the `_currentLogDate = DateTime.MinValue` reset and midnight
rollover. Audit: `AppLog` was the only leak into real user state.

### B6. `chat-updated` socket events were never persisted

`cf376e5` (`65499c8` + `5146e3d` + `ccc34c4`). `ActionHandler` parses the chat out of the payload's
`chats[0]`; `ChatsService.ApplyChatUpdateAsync` is the single writer, going through
`ChatFieldMerge.ApplyServerOwnedFields` and reconciling participant rows (`LinkParticipantsAsync`
for adds plus an explicit diff-and-delete for removals, since that helper only ever adds).
The four events fired `ChatUpdated` and nothing else — no DB write — a deviation from
`docs/PLAN.md:112`. **It did not self-heal while the app was open**: deltas run only at launch,
socket connect and network recovery, and `ReconcileChatsAsync` only prunes deletes. Correctness bug,
not latency. Also refuted: the details-pane rename never worked either — `ChatDetailsViewModel`
deserialized the whole payload as a `Chat`, got the *message's* GUID, and its `chat.Guid !=
_chatGuid` guard always returned.
**Regression caught in review:** `ApplyServerOwnedFields` copied `HasUnreadMessage` unconditionally
and the model property was non-nullable `bool`, so a group-event payload omitting the field
deserialized to `false` and **renaming a group cleared its unread badge**. Every fixture stated the
field explicitly, so nothing caught it. Fixed by making it `bool?` and guarding inside
`ChatFieldMerge` — the authority, not a call-site exception. Silence now means "no opinion".

### A1. Removed four Appearance settings

Colorful bubbles, Dense chat tiles, Hide dividers, Avatar size (`02dbe69` + `c13cb2a`, merged
`c24619d`). **No settings migration needed:** `SettingsService.JsonOpts` leaves
`UnmappedMemberHandling` at `Skip`, so an existing `settings.json` still loads with dead keys
present; `SettingsVersion` correctly left at 1. Fixed in review: the defaults test had swapped a
non-vacuous assertion (`AvatarScale == 1.0`) for a **vacuous** one (`Use24HrFormat == false`, where
`bool` already defaults to `false` and the constructor never sets it).

### A2. Dropped "Scroll to last unread"; status indicators made unconditional

`6562a07` (`2d0202b` + `b76f526`). **Neither was dead code** — the indicator only ever appears on
chats where *you* sent the last message, which is why toggling it looked like a no-op. Removing the
setting cascaded `AppSettings` out of `ApplyAppearance`, the tile constructor and
`ConversationListViewModel`; live 24-hour-time updates were verified unaffected (they run through a
separate subscription in `ConversationListPage.xaml.cs`).
**Caught in review:** the PR as pushed still contained `ComputeFirstUnread` with zero callers — its
deletion existed only as an *uncommitted working-tree edit*, so the reported green run was measured
against a tree that was not the deliverable. An unused private method is not a compiler warning.

### A3. Removed the "Theme backup" section, folded its keys into Settings backup

`2d00f00`. `theme`, `colorfulAvatars` and `use24HrFormat` now ride in the settings payload, and the
restore path calls `ThemeHelper.Apply` so a restore re-themes the live window instead of waiting for
a relaunch. Backward compatibility was a **source read, not an executed test** (`BackupSettingsPage`
lives in `BlueBubbles.Windows`, unreachable from the suite — B2b). Accepted: a theme backup already
on the server is now unreachable from this client.

## 0.24.0 — B7, F5, U1

### B7. Photos rendered twice — duplicate attachment rows

`7354c4b` (`fe911bd` + `4a58b60`). Attachment identity is now **`(MessageId, OriginalRowId)`** with
the GUID check kept as a strict superset. `AttachmentDeduplicator.CollapseDuplicatesAsync` repairs
existing caches from `SyncService` (the cache has no version stamp — `EnsureCreatedAsync`, no
migrations — and the pass is idempotent).
**Root cause, measured:** `originalROWID` is Apple's chat.db attachment ROWID, passed through
verbatim by the server's `AttachmentSerializer`. **Apple rewrites the GUID as a transfer completes**
(plain UUID -> `at_<n>_<messageGuid>`) while the ROWID stays put, so GUID-keyed dedupe stored the
same photo twice. We never synthesize `at_N_`. Verified against a pre-sync snapshot: of 59 groups,
**18 are the bug** (each exactly one `at_N_` plus one plain GUID) and 41 are genuinely distinct
attachments. Repair `944 -> 926`, second run removed 0, `distinctPairs == total`.
**Coverage hole found in review:** a `TransferName` identity — the plausible wrong fix — **survived
the full 420-test suite**, because the `OriginalRowId` pre-filter masks it in every synthetic case.
It is not harmless: 9 messages in real data have a mixed shape and msg 4213 would drop from 3 rows
to 1. Closed by `Collapse_MessageWithBothADuplicateAndADistinctRow_CollapsesOnlyTheDuplicate`.
**Refuted:** "B2 (`e318fe1`) introduced this" — 17 of 18 affected rows predate that merge.
Residual open threads carried forward as B9.
**Process note:** the repair executed against the maintainer's **live cache** during an agent's
build-and-run — unreviewed code mutated real user data pre-merge. No harm (correct 18 rows, snapshot
predates it), but agents must not run migrations against live user data before review.

### F5. Toast actions: two reactions plus Mark as read

`38dc7c8` (PR 12). Message toast is now Send + Love + Like + Mark as read; reaction
toast is Send + Mark as read. Mark as read does **not** foreground the app: the routing decision was
lifted into `BlueBubbles.Core/Services/ToastActivationRouter.cs` as a pure `Resolve(args, userInput)`
with `ActivatesWindow => Kind is OpenChat or OpenApp`. Mutation adding `MarkRead` to that rule fails
`Resolve_InlineActions_DoNotActivateWindow`. **This established the preferred answer to B2b** — lift
the decision into Core, leave rendering in the view.
Decisions: the button is always shown, silently local-only when Private API is unavailable (hiding
it would make toast layout depend on a setting). Known and scoped out: if the app is closed, a toast
action still shows the window — inherent to unpackaged single-instancing, predates F5.

### U1. In-app updater

`6a1ce4a` (PR 13). Checks `/releases/latest` (excludes drafts and prereleases) once per launch, no
poll. **The download is verified before execution:** the SHA-256 digest is parsed *before* any bytes
are fetched, so a missing digest means no download at all; `CryptographicOperations.FixedTimeEquals`
gates the single `_launcher.Launch` call site, and a mismatch deletes the file and logs at Error.
Mutation forcing the comparison true fails `Download_DigestMismatch_RefusesToExecuteAndDeletesFile`
— the check is not decorative. Host allowlist applies to the asset URL *and* the post-redirect final
URI, checked before the download path is built, so an untrusted redirect never writes a file.
Semantic comparison on parsed ints; `remote <= local` never offers a downgrade. `CheckForUpdateAsync`
wraps its whole body in a catch-all with a timeout, so the fire-and-forget call cannot break startup.
Uses a **separate `HttpClient`** so the BlueBubbles server's auth/proxy headers never reach
`api.github.com` — keep it that way.
Never exercised end to end: the download -> verify -> launch cycle has not run against a real
release (0.23.0 had no update check, so the 0.23.0 -> 0.24.0 hop cannot test it). SmartScreen on the
unsigned installer is likewise unverified.

