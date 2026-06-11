# Punchlist

> **Cleared (detail in git history):** Phase-6 items 1–33, plus Debug Session 2 clusters
> **D** (diagnostics/logging), **H1** (repo hygiene), **N** (notifications), **S** (sync
> reliability), **L** (layout/animation), **AT1** (image flicker — incl. the scroll-recycle
> follow-up: decoded-bitmap LRU cache so recycled bubbles re-show inline images synchronously),
> **UN** (uninstall/reset cleanup), **A** (avatars — generic person glyph + info-bar avatar
> mirrors the list), **AT2** (in-app video playback via `MediaPlayerElement` with external
> fallback), **34** (GH Actions release workflow: `dotnet test` + `publish.ps1 -Platform x64`,
> draft `v<version>` Release with installer attached), and **35** (flaky
> `Reaction_FromOther_PersistedAndNotifies` test).
>
> **0.20.2 bugfix release (B1–B4):** stray `Ctrl+N` tooltip on first conversation hover
> (`KeyboardAcceleratorPlacementMode="Hidden"` on ShellPage's root Grid); group-chat info Back
> button needing multiple clicks (duplicate `DetailsRequested` subscriptions on the cached
> `ChatPage`, fixed with a `-=`/`+=` guard); avatar bubble flickering on contact reload
> (`LoadFromVCardAsync` now reuses the same `byte[]` reference for unchanged photos —
> `StablePhoto`); installer not closing the running app during update (`PrepareToInstall`
> `taskkill`s the instance before copy, unblocking U1).
>
> **0.20.3 debug-pass fixes (DP1–DP5):** contact reload could transiently blank avatars/names
> mid-reload (atomic dictionary swap in `ContactResolverService`); link-preview hero images
> missed the stale-callback generation guard (`UrlPreview.LoadRemoteHero`); `publish.ps1
> -Platform arm64` built silently despite S1 (now needs `-AcknowledgeBroken`); three silent
> `catch { }`s now logged (`ChatsService`, `SyncService`, `SocketService`); small leaks/doc rot
> (deterministic `SoftwareBitmap` dispose in `MessageComposer`, CLAUDE.md corrections,
> `App.xaml.cs` DPAPI comment fix).
>
> **0.20.4 bugfix release (B5–B9):** Ctrl+Click deselect left the tile highlighted —
> ListViewBase applies its own click-selection *after* `ItemClick`, re-selecting what the
> handler cleared (deferred re-clear via `DispatcherQueue` in `ConversationListPage`); chat and
> message deletes never reached the server so the next sync resurrected them — now server-first
> (`ChatsService.DeleteChatAsync` / `MessagesService.DeleteMessageAsync` call the existing
> wire-correct API endpoints and only mutate the cache on success; failures surface via
> `ContentDialog`, chat delete gained a confirmation dialog since it's now destructive
> server-side); new-chat "To" field kept partial text after picking a suggestion (VM→TextBox
> sync via the existing `PropertyChanged` switch); repeated "New message" clicks stacked
> NewChatPage back-stack entries (dedupe in `ShellPage.OnNewChatRequested`, reset-in-place with
> a discard-draft confirmation); composer placeholder hardcoded "iMessage" (now reflects
> `Chat.Service` — "Text Message" for non-iMessage, never "SMS" since the server doesn't
> distinguish SMS from RCS).
>
> **B10 (follow-up to B6, same 0.20.4 release):** drafting a new message to a contact whose chat
> had just been deleted silently failed and the conversation never (re)appeared in the list.
> Root cause chain: `FindExistingChatGuid` matches by participant *address*, so it can return a
> stale local row whose chat no longer exists server-side (relic rows from old syncs survive —
> verified live: local row's guid 404'd on the server); `SendToExistingChatAsync` then sent to
> the dead guid and **ignored the API response** — no error surfaced, `ChatReady` fired as if
> sent, and nothing bumped the chat's `LatestMessageDate`, so even a successful send to a stale
> tile stayed buried at its old sort position. Fix (`NewChatViewModel`): the existing-chat path
> now checks every response — on a clean rejection *before anything was delivered* it falls back
> to `chat/new` (which creates/returns the canonical chat and self-heals the relic); on success
> it calls `ChatsService.HandleNewMessageAsync` (bump sort date, undo soft-delete, reload list)
> instead of relying on the not-guaranteed self-echo; partial failures are logged, never
> retried (no double-send). Not unit-tested: lives in the WinUI project, which the `net8.0`
> test project can't reference (see T1).
>
> **B11–B15 (fixed, shipping with 0.21.0):** Danger Zone reset left stale/cross-wired in-memory
> caches re-syncing into the wiped DB — reset now relaunches the process via
> `AppInstance.Restart` (unpackaged-safe; tray icon removed first since Restart skips Closed
> handlers; in-place SetupPage navigation kept as the failure fallback; dead `ResetRequested`
> event removed). "Scroll to bottom" FAB stuck visible on short threads — `ViewChanged` only
> fires on *offset* changes, so a transient `ScrollableHeight > 0` during initial layout never
> got re-evaluated; `ChatPage` now registers a property-changed callback on
> `ScrollViewer.ScrollableHeight` and re-runs the `IsNearBottom()` rule whenever it changes.
> Sent image vanished after navigating away/back — the locally-picked file never entered the
> attachment cache under the *server* guid; `IAttachmentCacheService.SeedFromLocalFileAsync`
> copies it in when the send response returns the real attachment guid (wired in
> `OutgoingMessageService` + both direct-send paths in `NewChatViewModel`; covered by
> `SentAttachment_SeedsCacheUnderServerGuid`). Blank tile preview for attachment-only last
> message — new `MessagePreview.Derive` (Core/Utils) strips U+FFFC placeholders and falls back
> to "Image"/"Video"/"Audio Message"/"Attachment" (pluralized) from attachment mime types;
> applied in `ChatsService.LoadChatsInternalAsync` (last-message query now `Include`s
> attachments), `IncomingMessageProcessor`, and `NewChatViewModel` (unit-tested). Tray icon gone
> for good after an Explorer restart — `MainWindow.WndProc` now watches the registered
> `"TaskbarCreated"` broadcast and `SystemTrayService.HandleTaskbarCreated()` re-runs
> `Shell_NotifyIcon(NIM_ADD)`.

---

## Open bugs

*(none)*

---

## F — Feature backlog  *(feature → future minor)*

#### F1. Scheduled send — **shipped in 0.21.0**
- [x] **Architecture (investigated 2026-06-10):** the BlueBubbles *server* owns all timing — its
      `scheduledMessagesService` persists scheduled messages in the server DB, arms a
      `setTimeout` per message, and at fire time sends through the normal private-api path. It
      is a queue, **not** Apple's native iOS 18 "Send Later" (unreachable via the private API).
      Client = CRUD only, no client-side timer; the Mac server must be running at fire time; the
      message then arrives in-thread via the normal `new-message` socket path (no tempGuid —
      covered by the existing out-of-order delayed-emit logic). Payload is text-only.
- [x] Core: `ScheduledMessageService` (thin pass-through + validation guards; always sends
      `schedule: {type:"once"}` — the server validator rejects the empty schedule the old
      API-client default would have sent), the five `scheduled-message-*` socket events
      registered and routed via `IActionHandler.ScheduledMessagesChanged` (deleted event carries
      an array; others a single object, `data`-wrap tolerated), `ScheduledForLocal` ISO-parse
      helper + status constants on the model.
- [x] UI: right-click/long-press the send button → "Send later…" (`ScheduleSendDialog`:
      CalendarDatePicker + TimePicker, DST-safe combine, must be ≥ now+1 min). Text-only —
      disabled for staged attachments / reply / edit modes.
- [x] **In-thread pending display (UX rework, same release):** Apple-style "Send Later" — each
      pending scheduled message renders as a dashed-outline bubble pinned at the bottom of the
      thread (`ChatViewModel.ScheduledItems` + the `ScheduledSection` row in `ChatPage.xaml`),
      captioned "Send later — Thu, Apr 10 at 7:00 AM", with errored ones showing the server
      error inline. Right-click a bubble → Edit… (reopens `ScheduleSendDialog` prefilled) or
      Cancel send (confirm dialog — the composed text is lost). The list live-refreshes from the
      `scheduled-message-*` socket events and reloads on chat open; on `sent` the outlined
      bubble disappears and the real message arrives via the normal new-message path. The
      original hidden-queue surface (`ScheduledMessagesDialog`/`Flow`/`ViewModel`, the
      ChatDetailsPage entry, and the "Scheduled messages…" flyout item) was removed.
- [ ] **Future enhancement (not v1):** recurring schedules (wire format already supports
      `{type:"recurring", interval, intervalType}`); scheduling a reply (`selectedMessageGuid`
      is plumbed through the service, no UI).

#### F2. Audio message support
- [ ] Record and send audio messages (voice memos) from the composer, matching iMessage's
      tap-and-hold-to-record audio bubble.
- [ ] Inbound audio attachments already play back (`AttachmentHolder`/`AttachmentViewModel`); this
      is about *recording and sending* a new audio attachment.

#### F3. Improve taskbar unread-badge rendering quality — **shipped in 0.21.0**
- [x] **Researched (2026-06-10):** the "modern" badge API
      (`Windows.UI.Notifications.BadgeUpdateManager` /
      [Microsoft Learn: Badge notifications](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/badges))
      is **not viable** here — both `CreateBadgeUpdaterForApplication()` overloads update a
      packaged app's Start tile / require the caller to belong to a package, i.e. it's a
      `Package.Current`-class API and throws unpackaged, same as the APIs already forbidden in
      CLAUDE.md. There's no AUMID-only path for it like `AppNotificationManager` has for toasts.
      No OS-version gate is needed because the API can't be used at all on this distribution
      model — `TaskbarBadgeService`'s `ITaskbarList3.SetOverlayIcon` + `BadgeIconRenderer` is the
      correct (only) mechanism for an unpackaged Win32 app, on every supported Windows version.
- [x] **Implemented (2026-06-11):** `BadgeIconRenderer` rewritten from raw GDI (16x16
      `Ellipse`/`DrawText`, no anti-aliasing, no alpha — `CreateCompatibleBitmap` corners were
      undefined memory) to GDI+ via `System.Drawing.Common` (pinned 8.0.x to match the net8
      TFM): true 32bpp ARGB surface, `SmoothingMode.AntiAlias` circle in Windows 11's badge red
      (#C42B1C) + anti-aliased bold Segoe UI digits, rendered at 4x supersampling and
      downscaled `HighQualityBicubic`. Size is DPI-aware (`GetDpiForWindow` on the main window,
      16px @ 96dpi scaled up; cache keyed by count+size). The HICON is built manually with
      `CreateIconIndirect` over a 32bpp DIB section plus an alpha-derived 1bpp AND mask —
      **not** `Bitmap.GetHicon()`, which collapses smooth alpha to a 1-bit mask and re-creates
      the jagged box edge. Verified via standalone render harness: corner pixels alpha 0,
      anti-aliased rim, clean composite over a dark taskbar background.

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

---

## Release plan

**Next minor (0.21.0)** — scheduled send (F1) + bugfixes B11–B15 — implemented, pending release.

**Future minor** — client updater (U1).

No version bump: repo hygiene (H2). Stretch goal (no schedule): arm64 (S1).
