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
