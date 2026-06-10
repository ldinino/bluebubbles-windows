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

---

## Open bugs

#### B5. Ctrl+Click deselect doesn't un-highlight the conversation
- [ ] Ctrl+clicking the selected conversation in the list deselects it (the thread closes /
      selection is cleared in the view model), but the list item stays visually highlighted.

#### B6. Deleting a message or conversation doesn't write back to the server
- [ ] `MessagesService.SoftDeleteMessageAsync` and `ChatsService.DeleteChatAsync` only mutate the
      local SQLite cache (set `DateDeleted` / remove the row) — they never call the server.
      `IBlueBubblesApiService` already has `DeleteMessageFromChatAsync(chatGuid, ...)` and
      `DeleteChatAsync(guid)` but nothing calls them.
- [ ] Net effect: a local delete is **undone** the next time `SyncService` syncs down from the
      server (the message/chat still exists server-side and gets re-pulled). Need to call the
      server delete endpoint first (private-API), and only update the local cache on success.

#### B7. New-chat "To" field keeps partial text after picking a suggestion
- [ ] In the new-conversation composer, typing a partial name/number and clicking a suggestion in
      `ResultsList` adds the recipient chip, but `RecipientSearchBox.Text` keeps the partially
      typed text instead of clearing.
- [ ] Root cause: `RecipientSearchBox` only binds one-way to `NewChatViewModel.SearchQuery` (via
      `OnRecipientSearchTextChanged`, `NewChatPage.xaml.cs`). `AddRecipient` resets `SearchQuery`
      to `string.Empty` (`NewChatViewModel.cs`), but nothing writes that back to
      `RecipientSearchBox.Text`. Fix in `OnResultItemClick`/`OnRemoveChipClick` (or wherever a
      recipient is added) by also clearing `RecipientSearchBox.Text`.

#### B8. Repeated "New message" clicks stack multiple draft pages
- [ ] Clicking "New message" while already on a new-chat draft calls `ChatFrame.Navigate(typeof
      (NewChatPage))` again unconditionally (`ShellPage.xaml.cs`, `OnNewChatRequested`). Since
      `NewChatPage` isn't cached/deduped, each click pushes another back-stack entry — so the
      user has to hit Back once per click to actually leave.
- [ ] **Desired fix:** if `ChatFrame.Content` is already a `NewChatPage`, don't navigate again —
      just reset the existing draft (`_vm.Reset()` / clear recipients + composer text) in place.
      If the draft has unsaved content (non-empty `Recipients` or composer text), warn the user
      before discarding it instead of silently clearing.

#### B9. Composer always says "iMessage", even for forwarded SMS/RCS chats
- [ ] `MessageComposer.xaml`'s `InputBox` has `PlaceholderText="iMessage"` hardcoded — it never
      reflects the chat's actual transport, so a chat being relayed via SMS/RCS forwarding still
      shows "iMessage".
- [ ] `ChatEntity`/`Chat` already carries a `Service` field (`"iMessage"` vs `"SMS"`, populated
      from the server's `chat.service` — see `SyncService`/`ChatsService`/`MappingExtensions`).
      Wire `ChatViewModel` (it already loads the `ChatEntity` via `_chatsService.Chats` in
      `LoadChatAsync`) to expose this, and have `ChatPage` set the composer placeholder
      accordingly.
- [ ] **Wording:** don't show "SMS" for the non-iMessage case — the BlueBubbles server doesn't
      distinguish SMS from RCS (forwarding works for both), so labeling it "SMS" would be wrong
      for RCS-forwarded chats. Use a neutral term (e.g. "Text Message") when `Service !=
      "iMessage"`.

---

## F — Feature backlog  *(feature → future minor)*

#### F1. Scheduled send
- [ ] Let the user compose a message and pick a future date/time to send it, instead of sending
      immediately.
- [ ] `BlueBubbles.Core` already has `IScheduledMessageService` / `ScheduledMessage` and the API
      client method, but there's no WinUI surface (composer UI to schedule, and a view to list/
      edit/cancel pending scheduled messages).

#### F2. Audio message support
- [ ] Record and send audio messages (voice memos) from the composer, matching iMessage's
      tap-and-hold-to-record audio bubble.
- [ ] Inbound audio attachments already play back (`AttachmentHolder`/`AttachmentViewModel`); this
      is about *recording and sending* a new audio attachment.

#### F3. Improve taskbar unread-badge rendering quality
- [ ] **Researched (2026-06-10):** the "modern" badge API
      (`Windows.UI.Notifications.BadgeUpdateManager` /
      [Microsoft Learn: Badge notifications](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/badges))
      is **not viable** here — both `CreateBadgeUpdaterForApplication()` overloads update a
      packaged app's Start tile / require the caller to belong to a package, i.e. it's a
      `Package.Current`-class API and throws unpackaged, same as the APIs already forbidden in
      CLAUDE.md. There's no AUMID-only path for it like `AppNotificationManager` has for toasts.
      No OS-version gate is needed because the API can't be used at all on this distribution
      model — `TaskbarBadgeService`'s `ITaskbarList3.SetOverlayIcon` + `BadgeIconRenderer` is the
      correct (only) mechanism for an unpackaged Win32 app, on every supported Windows version.
- [ ] **Real improvement:** `BadgeIconRenderer` renders a 16x16 GDI bitmap (plain `Ellipse` +
      `CreateFont`/`DrawText`, no anti-aliasing), which looks blocky/pixelated next to native
      Windows 11 badges, especially at higher DPI. Improve quality instead: render at a larger
      size (e.g. 32x32 or 48x48, matching `GetSystemMetrics(SM_CXICON)`/DPI) and downscale, or
      switch to GDI+ (`System.Drawing.Graphics` with `SmoothingMode.AntiAlias` /
      `TextRenderingHint.AntiAliasGridFit`) for a smooth circle and crisp digits, matching the
      red badge styling Windows 11 uses for its own app badges.

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

**Next patch** — Ctrl+Click deselect highlight (B5).

**Future minor** — client updater (U1).

No version bump: repo hygiene (H2). Stretch goal (no schedule): arm64 (S1).
