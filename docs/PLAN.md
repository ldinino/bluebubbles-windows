# BlueBubbles WinUI3 — Implementation Plan

## Context

We're building a first-class WinUI3 iMessage client that ports the BlueBubbles Flutter app (323 Dart files, 90+ API endpoints, Socket.IO real-time, Firebase server discovery, EF Core local cache). The design spec (`BlueBubbles-WinUI3-Design-Spec.md`) is the binding reference. The Flutter source in this repo is the behavioral reference for every service, event handler, and edge case.

**Key decisions:**
- **Database:** EF Core SQLite (ORM with migrations, LINQ, relationship tracking)
- **Testing:** `BlueBubbles.Windows.Tests` project included from Phase 0
- **Integration testing:** Live BlueBubbles macOS server available throughout development

The plan is structured as **checkpoint phases**: each phase produces something testable, and we stop after each to verify before moving on. Earlier phases build foundations that later phases depend on — models are designed complete from the start to avoid migrations, all API endpoints are ported before any UI consumes them, and all socket events are wired before the chat view exists.

## Philosophy — Better Than the Flutter App

The Flutter source is a **protocol reference**, not a **design reference**. We use it to understand *what the server expects* (endpoints, JSON shapes, socket events, auth). We do not replicate *how the Flutter client is structured* — its resource management is poor, its UX is janky, and no amount of theming makes Flutter feel native on Windows.

- **Protocol: copy faithfully.** If the server expects `method: "private-api"` in a POST body, we send it exactly. The Flutter source is authoritative for field names, endpoint paths, auth patterns, and event contracts.
- **Architecture: build it right.** Where Flutter has six tangled booleans, we use one server-driven capability flag. Where Flutter scatters conditional logic, we pick the correct path and commit. Simplify ruthlessly.
- **UX: be a Windows app.** Mica, WinUI3 controls, proper taskbar integration, toast notifications, system tray. No cross-platform abstractions pretending to be native.
- **Private API is the only API.** The AppleScript fallback (`method: "apple-script"`) exists in the server for legacy clients. We don't use it. Every outgoing message sends `method: "private-api"` unconditionally.

When in doubt: read the Flutter source for *what* to send, then forget how it sends it.

---

## Phase 0 — Solution Scaffolding & DI Foundation
**Complexity: Small | Depends on: nothing**

Create the Visual Studio solution with two projects and get a window on screen.

**Deliverables:**
- `BlueBubbles.Windows` — WinUI3 app, **unpackaged** (.NET 8, Windows 10 19041+); ships via the Inno Setup installer, no MSIX
- `BlueBubbles.Core` — Class library, no UI references
- NuGet packages installed per spec section 9
- `App.xaml.cs` — `IServiceProvider` via `Microsoft.Extensions.DependencyInjection`, placeholder service stubs registered
- `MainWindow.xaml` — Mica backdrop, `ExtendsContentIntoTitleBar`, custom title bar
- `ShellPage.xaml` — empty two-column Grid (left 320px, right fills)
- `Converters/` — `DateTimeToRelativeConverter` (spec 10.5 date logic), `BoolToVisibilityConverter`

**Key files:**
- `BlueBubbles.Windows/App.xaml.cs`
- `BlueBubbles.Windows/MainWindow.xaml`
- `BlueBubbles.Windows/Views/ShellPage.xaml`
- `BlueBubbles.Core/BlueBubbles.Core.csproj`
- `BlueBubbles.Windows.Tests/BlueBubbles.Windows.Tests.csproj` — xUnit test project referencing BlueBubbles.Core

**Verify:** App launches, shows Mica window with custom title bar and empty two-column layout. DI resolves services without error. Test project builds and runs (empty test passes).

---

## Phase 1 — Core Data Models & Database
**Complexity: Large | Depends on: Phase 0**

Define every model with full field coverage from day one. The Flutter `Message` class has ~80 fields — we include fields for reactions, replies, edits, unsend, and effects now even though their UI comes later, to avoid schema migrations.

**Deliverables:**
- **API DTOs** (immutable records with `[JsonPropertyName]`) in `BlueBubbles.Core/Models/`:
  `ApiResponse<T>`, `ApiError`, `ChatDto`, `MessageDto`, `HandleDto`, `AttachmentDto`, `ContactDto`, `ContactAddressDto`, `ServerInfoDto`, `FcmDataDto`, `FirebaseProjectDto`, `AttributedBodyDto`, `MessagePartDto`, `ScheduledMessageDto`, `FindMyDeviceDto`, `FindMyFriendDto`, `SocketEvents` (string constants)
- **EF Core entities** (mutable, for persistence) in `BlueBubbles.Core/Data/Entities/`:
  `ChatEntity`, `MessageEntity` (all ~50 fields including `AssociatedMessageGuid`, `ThreadOriginatorGuid`, `DateEdited`, `HasReactions`, `PayloadDataJson`), `HandleEntity`, `AttachmentEntity`, `ContactEntity`, `FcmDataEntity`, join table `ChatParticipant`
- **`BlueBubblesDbContext`** — relationships, indexes on Guid columns and DateCreated
- **DTO-to-Entity mapping extensions** (`MessageDto.ToEntity()`, `ChatEntity.ToDto()`)
- **`AppSettings`** — all settings fields from Flutter's `Settings` class, as `ObservableObject`
- **`ServerConfiguration`** — URL, password ref, proxy type, custom headers, FCM data, localhost port

**Key source reference:** `lib/database/io/message.dart` (lines 230-299 for field list), `lib/database/global/settings.dart` (lines 16-150 for settings)

**Verify:** Unit tests create in-memory SQLite DB, insert Chat with Participants and Messages with Attachments, query relationships, round-trip a `MessageDto` from real server JSON.

---

## Phase 2 — API Service Layer (HTTP Client)
**Complexity: Large | Depends on: Phase 1**

Port all 90+ endpoints from `lib/services/network/http_service.dart`. Every method returns `Task<ApiResponse<T>>`.

**Deliverables:**
- **`ProxyHeaderHandler : DelegatingHandler`** — `ngrok-skip-browser-warning` for ngrok, `skip_zrok_interstitial` for zrok, custom headers from settings
- **`CloudflareRetryHandler : DelegatingHandler`** — retry once on 502 when URL contains `trycloudflare`
- **`BlueBubblesApiService`** — singleton, takes `HttpClient` via `IHttpClientFactory`:
  - `BuildUrl(path, extraParams)` appending `?guid={password}` (spec 4.1)
  - `OriginOverride` property for localhost detection
  - Timeouts: 15s connect, configurable send/receive (default 30s), 12x for attachment downloads
  - All endpoints by category: Server (11), FCM (2), Attachments (5), Chats (16), Messages (17), Handles (6), Contacts (3), iCloud/FindMy (7), FaceTime (2), Backup (6) = **75 methods**
  - `CancellationToken` on every method
- DI registration with handler pipeline in `App.xaml.cs`

**Key source reference:** `lib/services/network/http_service.dart` (full file — auth pattern lines 24-30, headers lines 62-70, timeout line 76, all endpoint methods)

**Verify:** Integration test against real server: `PingAsync` returns pong, `ServerInfoAsync` returns version, `QueryChatsAsync` deserializes correctly. Unit test: proxy headers added for ngrok/zrok URLs, 502 retry fires for trycloudflare.

---

## Phase 3 — Socket.IO & AES Decryption
**Complexity: Medium | Depends on: Phase 1, Phase 2**

Wire all 11 real-time events. The socket connection is operational before any UI depends on it.

**Deliverables:**
- **`CryptoUtils`** — port `decryptAESCryptoJS`/`encryptAESCryptoJS` from `lib/utils/crypto_utils.dart` using `System.Security.Cryptography.Aes` (CBC, PKCS7, MD5-based CryptoJS KDF)
- **`SocketService`** — `SocketIOClient` NuGet, query auth `{ guid: password }`, transports `["websocket", "polling"]`:
  - `SocketState` enum as `ObservableProperty` (Disconnected → Connecting → Connected, Error with 5s retry)
  - All 11 event subscriptions (spec 5.1)
  - Encrypted response check: `{ encrypted: true, data: ... }` → decrypt
  - `SendMessageAsync(event, data)` with ack
  - `Connect()`, `Disconnect()`, `Reconnect()`, `RestartSocket()`
- **`ActionHandler`** — port of `lib/services/backend/action_handler.dart` `handleEvent`:
  - `new-message` / `updated-message` → deserialize, queue for processing, dedup via `tempGuid`
  - `typing-indicator` → update per-chat observable
  - `chat-read-status-changed` → toggle unread
  - `group-name-change`, `participant-*` → update chat in DB
  - `HandledNewMessages` GUID cache (last 100) and `OutOfOrderTempGuids` tracking

**Key source reference:** `lib/services/network/socket_service.dart` (state machine lines 57-192), `lib/utils/crypto_utils.dart` (AES lines 9-42), `lib/services/backend/action_handler.dart` (event dispatch)

**Verify:** Connect to real server, verify `Connected` state. Send message from another device → `new-message` fires. Disconnect server → state transitions Error → Connecting → Connected on restart. Unit test AES round-trip with known Dart-encrypted string.

---

## Phase 4 — Setup Flow & Server Connection
**Complexity: Medium | Depends on: Phases 0-3**

First-run wizard: connect to server, sync data, enter the app. This unlocks testing everything downstream.

**Deliverables:**
- **`CredentialService`** — `PasswordVault` for password storage (`Save`, `Get`, `Delete`)
- **`FirebaseService`** — `FetchFirebaseConfigAsync()` (GET /fcm/client), `FetchNewUrlAsync()` (RTDB/Firestore REST), `SetRestartDateAsync()` (Firestore PATCH)
- **`ServerDiscoveryService`** — Google OAuth (WebView2, desktop client ID `500464701389-18rfq...`, redirect `http://localhost:8641/oauth/callback`), project listing, RTDB/Firestore URL resolution, ping reachability
- **`SanitizeServerAddress`** helper (spec 6.5 rules)
- **`SyncService`** — full sync: fetch chat count, stream pages, save to DB, fetch messages per chat, progress reporting via `IProgress<SyncProgress>`, fetch FCM config, cache contacts
- **Setup Views** (5 pages per spec 8.9):
  - `WelcomePage.xaml` — logo + "Get Started"
  - `ServerConnectPage.xaml` — Google (primary) + Manual tabs: project list with ProgressRing/reachability, URL TextBox + PasswordBox
  - `GoogleSignInPage.xaml` — WebView2 hosting OAuth consent
  - `SyncPage.xaml` — ProgressBar + status text
  - `SetupCompletePage.xaml` — "You're all set!" + Enter App
- **`SetupViewModel`** — wizard orchestration, commands for connect/sync/finish
- **App startup routing** — check `FinishedSetup` → setup or main shell

**Key source reference:** `lib/helpers/ui/oauth_helpers.dart` (desktop OAuth lines 81-105), `lib/services/network/firebase/firebase_database_service.dart` (URL resolution lines 57-113), `lib/services/backend/sync/full_sync_manager.dart`

**Verify:** Fresh launch → wizard appears. Manual: enter URL + password → connect → sync → main shell. Google: OAuth → project list → password → sync → main shell. Relaunch → skips setup. Password in PasswordVault, not plaintext.

---

## Phase 5 — Conversation List & Master Pane
**Complexity: Medium | Depends on: Phase 4**

The left pane with all conversations, pinned contacts, search, and connection status.

**Deliverables:**
- **`ChatsService`** — loads chats from DB in batches, watches socket events for reordering, sorting (pinned first by order, then by last message date desc)
- **`ContactResolverService`** — address → display name dictionary, `GetDisplayName`, `GetInitials`, `GetAvatar`, server refresh
- **`ConversationListViewModel`** — `ObservableCollection<ConversationTileViewModel>`, `PinnedContacts`, `SearchQuery` with filter, `SelectedConversation`
- **`ConversationTile` control** (spec 8.4) — PersonPicture 40x40, bold name if unread, relative timestamp, 2-line preview, unread dot, right-click MenuFlyout (Pin, Mark Read, Archive, Delete)
- **`PinnedContact` control** (spec 8.5) — PersonPicture 56x56, name caption, InfoBadge, 3-column grid
- **`AvatarControl`** — contact photo or colored-circle-with-initials fallback, group variant with stacked avatars
- **`ConversationListPage.xaml`** — AutoSuggestBox, pinned grid, ListView, Settings button, ConnectionStatusBadge InfoBar
- **`ShellPage.xaml` updated** — left pane hosts ConversationListPage, right pane "Select a conversation" empty state, responsive single-pane < 768px, draggable divider 280-400px

**Verify:** Synced chats appear sorted correctly. Pinned chats in grid. Unread = bold + dot. Search filters. Selection highlights. Socket new-message reorders list. Resize below 768px → single pane.

---

## Phase 6 — Chat View: Message Display & Scrolling
**Complexity: Large | Depends on: Phase 5**

The right pane with the message thread — the core messaging experience.

**Deliverables:**
- **`MessagesService`** — per-chat message list, DB pagination, receives new/updated messages from ActionHandler, date grouping
- **`ChatViewModel`** — `MessageGroups`, `CurrentChat`, `IsTyping`, `IsLoading`, `LoadInitialMessages`/`LoadMoreMessages` commands
- **`ChatBubble` control** (spec 8.6) — outgoing right/accent, incoming left/card-bg, 12px corners/4px tail, max 65% width, sender name in groups, timestamp, delivery status (sent/delivered/read), big emoji, subject line
- **`TypingIndicator` control** — three bouncing dots
- **`ChatPage.xaml`** — header bar (name, participants, info button), ListView in ScrollViewer (bottom-anchored), scroll-to-bottom FAB, infinite scroll up, date separators, typing indicator

**Verify:** Select conversation → messages load. Correct alignment. Scroll up → older messages load. Group chat sender names. Delivery status. Typing indicator from other device. Date separators. Emoji-only = larger.

---

## Phase 6.5 — Private API True-Up & Protocol Audit ✅
**Complexity: Medium | Depends on: Phase 6**

Eliminated the six-boolean Private API settings mess inherited from Flutter. Private API is now the authoritative, always-on communication method — `method: "private-api"` is sent unconditionally on every outgoing message.

**Deliverables:**
- **Settings collapse** — Removed `EnablePrivateAPI`, `PrivateAPISend`, `PrivateAPIAttachmentSend`. Kept `ServerPrivateAPI` (capability flag from server), `PrivateSendTypingIndicators` and `PrivateMarkChatAsRead` (user preferences, both default `true`). Added `PrivateManualMarkAsRead`.
- **Always Private API** — All conditional `EnablePrivateAPI && PrivateAPISend` checks removed from `OutgoingMessageService`, `ChatsService`, `ChatViewModel`. Every send uses `method: "private-api"`.
- **Full field passthrough** — `OutgoingItem` expanded with `EffectId`, `SelectedMessageGuid`, `PartIndex`, `DdScan`, `IsAudioMessage`, `Parts`. `EnqueueText` and `EnqueueAttachment` accept all Private API parameters.
- **New outgoing operations** — `EnqueueMultipart` (mentions), `SendTapbackAsync` (reactions), `SendEditAsync`, `SendUnsendAsync` added to `IOutgoingMessageService`.
- **Chat DTO fix** — Added `customAvatarPath` and `pinIndex` to `Chat` record with bidirectional mapping.
- **API endpoint audit** — `CreateChatAsync` defaults to `method: "private-api"`. `CreateScheduledMessageAsync`/`UpdateScheduledMessageAsync` payloads expanded with `effectId`, `subject`, `selectedMessageGuid`, `partIndex`.
- **Service interface scaffolding** — `IMessageActionsService`, `IScheduledMessageService`, `IFindMyService`, `IFaceTimeService` created (interfaces only, no DI registration until implementing phase).
- **Server capability validation** — `ServerPrivateAPI` populated from `GET /server/info` at setup and on every incremental sync reconnect.

**Key files changed:**
- `BlueBubbles.Core/Configuration/AppSettings.cs`
- `BlueBubbles.Core/Services/OutgoingMessageService.cs`, `IOutgoingMessageService.cs`
- `BlueBubbles.Core/Services/BlueBubblesApiService.cs`, `IBlueBubblesApiService.cs`
- `BlueBubbles.Core/Services/ChatsService.cs`
- `BlueBubbles.Core/Services/SyncService.cs`
- `BlueBubbles.Core/Models/Chat.cs`, `BlueBubbles.Core/Data/MappingExtensions.cs`
- `BlueBubbles.Windows/ViewModels/ChatViewModel.cs`, `SetupViewModel.cs`
- New: `IMessageActionsService.cs`, `IScheduledMessageService.cs`, `IFindMyService.cs`, `IFaceTimeService.cs`

**Verify:** `grep -r "apple-script" BlueBubbles.Core/` returns zero results. `grep -r "EnablePrivateAPI" BlueBubbles.Core/` returns zero results. Solution builds cleanly. Message sends include `method: "private-api"` in server debug logs.

---

## Phase 7 — Message Sending & Outgoing Queue ✅
**Complexity: Medium | Depends on: Phase 6**

Send text and attachments with optimistic UI and tempGuid deduplication.

**Deliverables:**
- **`MessageComposer` control** (spec 8.7) — multi-line auto-grow TextBox (up to 5 lines), send button, attachment `+` button with MenuFlyout → file picker, attachment staging bar with file icon + name + remove, Enter to send (configurable via `SendWithReturn`), typing indicator emission via socket
- **`OutgoingMessageService`** — Channel-based sequential queue, tempGuid generation (`temp-{shortGuid}`), `method: "private-api"` unconditional, states: Sending → Sent/Failed/Cancelled, error handling with `MessageStateChanged` event
- **`IncomingMessageProcessor`** — Channel-based sequential processing of socket events (`new-message`, `updated-message`). Saves messages to DB via `MessagesService`, updates chat list via `ChatsService.HandleNewMessageAsync`, fires `MessageProcessed` event for UI notification. Decoupled `ConversationListViewModel` from direct DB work.
- **Message send flow** — `ChatViewModel.SendMessage()` → `OutgoingMessageService.EnqueueText/Attachment` → optimistic bubble insert (`InsertOptimisticMessage`) → queue processes with send delay → API call → `MessageStateChanged` → `ChatViewModel.OnOutgoingMessageStateChanged` → `ConfirmSent(serverGuid)`. Socket echo dedup via `_pendingTempGuids` and `ActionHandler` out-of-order GUID tracking.
- **Send delay** (0-10s configurable via `AppSettings.SendDelay`, cancel during delay) — `IsDelayed` property on `MessageBubbleViewModel`, "Cancel" HyperlinkButton in `ChatBubble`, `CancelPending(tempGuid)` removes from queue
- **Optimistic conversation list update** — `SendMessage()` immediately calls `ChatsService.HandleNewMessageAsync` to bump the chat to the top with the sent text as preview, before the API response arrives

**Key files changed:**
- New: `BlueBubbles.Core/Services/IIncomingMessageProcessor.cs`, `IncomingMessageProcessor.cs`
- `BlueBubbles.Windows/ViewModels/ChatViewModel.cs` — `IChatsService` dependency, optimistic chat update, `IsDelayed`/cancel wiring, `Sending` state handling
- `BlueBubbles.Windows/ViewModels/ConversationListViewModel.cs` — replaced `IMessagesService` + direct `ActionHandler.NewMessageReceived` DB work with `IIncomingMessageProcessor.MessageProcessed` for mark-as-read only
- `BlueBubbles.Windows/ViewModels/MessageBubbleViewModel.cs` — `IsDelayed`, `CancelAction`, `DeliveryStatusText` shows "Scheduled" during delay
- `BlueBubbles.Windows/Controls/ChatBubble.xaml` — Cancel HyperlinkButton in meta panel
- `BlueBubbles.Windows/Controls/ChatBubble.xaml.cs` — `IsDelayed` property change handler, `OnCancelClick`
- `BlueBubbles.Windows/App.xaml.cs` — `IIncomingMessageProcessor` DI registration + `Start()` in `OnLaunched`

**Verify:** Type + Enter → message appears instantly (sending state) → sent → delivered → read. Attach file → uploads + appears on other device. No connection → error state on message. Send delay cancelable. Multiple messages send in order.

---

## Phase 8 — Attachment Display & Caching
**Complexity: Medium | Depends on: Phase 7**

Inline media in chat bubbles with lazy download and local cache.

**Deliverables:**
- **`AttachmentCacheService`** — download queue (max 2 concurrent), local file cache in `LocalFolder/attachments/{guid}/`, keyed semaphore, progress reporting, auto-download setting
- **`AttachmentHolder` control** — MIME-type-aware rendering: images (thumbnail, click to fullscreen), videos (thumbnail + play overlay), audio (play/pause/progress), other (icon + name + size), loading state with ProgressRing or blurhash placeholder
- **Fullscreen media viewer** (`FullscreenMediaPage.xaml`) — zoom/pan, save-to-disk, share, navigate between media
- **Blurhash decoder** — placeholder bitmap while full image loads

**Verify:** Chat with images → thumbnails render. Progress indicator during download. Click → fullscreen. Reopen chat → loads from cache. Auto-download off → placeholder with download button. Two downloads in parallel, third queues.

---

## Phase 9 — Notifications, Tray, & Badge
**Complexity: Medium | Depends on: Phase 7**

Windows toast notifications, taskbar unread badge, system tray.

**Deliverables:**
- **`NotificationService`** — toast for new messages when app unfocused or different chat active, respects enable/disable + reaction + unknown sender settings, toast click → navigate to chat, summary notification for 2+ chats
- **Taskbar badge** — overlay icons badge-1 through badge-9, clears when all read
- **System tray** — icon + context menu (Show, Settings, Quit), minimize-to-tray and close-to-tray settings, double-click restore

**Verify:** Message while minimized → toast. Click toast → correct chat. Unread badge count on taskbar. Mark all read → badge gone. Minimize-to-tray. Tray right-click → Show/Quit.

---

## Phase 10 — Incremental Sync & Reconnection ✅
**Complexity: Medium | Depends on: Phase 4, Phase 3**

Stay in sync across app restarts and connection drops.

**Deliverables:**
- **`IncrementalSyncManager`** — `SyncService.RunIncrementalSyncAsync()` with `lastIncrementalSync` timestamp + `LastIncrementalSyncRowId` row-ID cursors, batch fetching (1000/batch), server-version-aware (fetches ServerPrivateAPI capability). Triggered automatically on socket connection.
- **`HandleSyncManager`** — integrated into incremental sync as reactive handle upsert via `SaveHandlesAsync()`. Handles are created/updated as they appear in message data.
- **Firebase URL re-resolution** — on socket ERROR/DISCONNECTED, exponential backoff (5×2^n, cap 60s), fetches fresh URL from Firebase RTDB/Firestore REST, updates config, restarts socket.
- **`LocalhostDetectionService`** — user-controlled LAN shortcut (manual toggle in Settings). When enabled, probes server-reported `local_ipv4s`/`local_ipv6s` on configured port (default 1234), tries HTTPS then HTTP, sets `OriginOverride` on success. No automatic detection or network monitoring — user flips toggle on/off as needed.
- **Startup tasks orchestration** — on socket connect: incremental sync runs, then localhost detection (if enabled). On app startup with toggle on: re-probes once, silently falls back to remote on failure.

**Key files:**
- `BlueBubbles.Core/Services/SyncService.cs` — `RunIncrementalSyncAsync()` (lines 241-354)
- `BlueBubbles.Core/Services/SocketService.cs` — `OnConnectedAsync()`, `RefreshUrlAndRestartAsync()`, `ScheduleReconnect()`
- `BlueBubbles.Core/Services/LocalhostDetectionService.cs` + `ILocalhostDetectionService.cs`
- `BlueBubbles.Core/Services/FirebaseService.cs` — `FetchNewServerUrlAsync()`
- `BlueBubbles.Windows/Views/SettingsPage.xaml` — "Use Local Connection" toggle + port + test button
- `BlueBubbles.Windows/ViewModels/SettingsViewModel.cs` — `ToggleLocalConnectionCommand`, `TestLocalConnectionCommand`

**Verify:** Close app, send messages from other device, reopen → all messages appear. Server restarts with new CF URL → socket reconnects via Firebase. Settings → toggle "Use Local Connection" ON → probes LAN IPs → shows resolved address or error.

---

## Phase 11 — Chat Details & Group Management ✅
**Complexity: Medium | Depends on: Phase 6**

**Deliverables:**
- **`ChatDetailsPage.xaml` + `ChatDetailsPage.xaml.cs`** — Full details page in the right pane: back button header, 80px avatar, display name with inline edit for groups, participant count, mute toggle (ToggleSwitch on SettingsCard), participant list with avatars/names/addresses + remove button for groups, add participant TextBox, group actions (set/delete group photo via FileOpenPicker, leave group with confirmation dialog), shared media gallery (GridView of thumbnails with load-more), status InfoBar for errors.
- **`ChatDetailsViewModel`** — Loads participant list from `ConversationTileViewModel`, media gallery from `IMessagesService.LoadMediaAttachmentsAsync`, commands for rename (`RenameChatAsync`), mute (`ToggleMuteAsync`), add/remove participant, leave group, set/delete icon. Listens to `ActionHandler.ChatUpdated` for real-time participant/name changes via socket events.
- **`IChatsService` extended** — 7 new methods: `RenameChatAsync`, `ToggleMuteAsync`, `AddParticipantAsync`, `RemoveParticipantAsync`, `LeaveChatAsync`, `SetChatIconAsync`, `DeleteChatIconAsync`. All call server API first, then update local DB + in-memory list, fire `ChatUpdated` event.
- **`IMessagesService` extended** — `LoadMediaAttachmentsAsync(chatId, limit, offset)` queries attachments with image/video MIME types ordered by message date.
- **Navigation wiring** — Info button (ⓘ) added to `ChatPage` header → `DetailsRequested` event → `ShellPage` navigates `ChatFrame` to `ChatDetailsPage`. Back button returns to `ChatPage`. Leave group clears frame and reloads chat list.
- **DI registration** — `ChatDetailsViewModel` registered as singleton in `App.xaml.cs`.

**Key files:**
- New: `BlueBubbles.Windows/Views/ChatDetailsPage.xaml`, `ChatDetailsPage.xaml.cs`
- New: `BlueBubbles.Windows/ViewModels/ChatDetailsViewModel.cs` (includes `ParticipantItemViewModel`)
- Modified: `BlueBubbles.Core/Services/IChatsService.cs`, `ChatsService.cs`
- Modified: `BlueBubbles.Core/Services/IMessagesService.cs`, `MessagesService.cs`
- Modified: `BlueBubbles.Windows/Views/ChatPage.xaml`, `ChatPage.xaml.cs` (info button + DetailsRequested event)
- Modified: `BlueBubbles.Windows/Views/ShellPage.xaml.cs` (ChatDetailsPage navigation, back, leave)
- Modified: `BlueBubbles.Windows/App.xaml.cs` (DI registration)

**Verify:** Group chat details → see participants/media. Rename → updates everywhere. Add/remove participant → server confirms. Mute toggle persists. Leave group → returns to empty state. Socket events update participants/name in real time.

---

## Phase 12 — New Chat Creation ✅
**Complexity: Small | Depends on: Phase 5, Phase 7**

**Deliverables:** `NewChatPage.xaml` + `NewChatViewModel` — contact search, multi-recipient selection (chips), iMessage availability check, reuse MessageComposer, creates chat via API.

**Verify:** New Chat → search contact → type message → send → chat appears in list, message arrives on other device. Group chat with multiple recipients.

---

## Phase 13 — Reactions (Tapbacks) ✅
**Complexity: Medium | Depends on: Phase 6**

Tapbacks end-to-end: render existing reactions, send/toggle via right-click picker, and reflect remote reactions in real time. Reactions are stored as ordinary `Message` rows (associated GUID + type) but never shown as standalone bubbles — they are summarized into pills beneath their parent.

**Deliverables:**
- **`ReactionTypes`** (`Core/Utils`) — the six types (love/like/dislike/laugh/emphasize/question), emoji + past-tense verb maps, removal detection (`-type`), and `NormalizeAssociatedGuid`/`ResolveAssociatedPart` porting Flutter's `message.dart` prefix stripping (`p:N/`, `bp:N/`).
- **`ReactionSummarizer`** (`Core/Utils`) — reduces raw reaction records to ordered badges: latest reaction per reactor wins, removals cancel, grouped by type with count + `IncludesMe`; plus `SelfReaction` for toggle decisions. Port of `getUniqueReactionMessages`.
- **Prefix normalization on persist** — `MessagePersistenceHelper` (sync) and `MessagesService.SaveMessageCoreAsync` (live) store the bare parent GUID + parsed part so reactions match parents in the DB.
- **`IMessagesService` extended** — `LoadReactionsAsync(parentGuids)` (reactions with handles for a page of messages) and `SaveReactionAsync` (persists a reaction + flags the parent `HasReactions`). `LoadMessagesAsync` continues to exclude associated messages from the bubble stream.
- **`ActionHandler.ReactionReceived`** — `new-message` events carrying a reaction type route to a dedicated event (resolved parent GUID) instead of the message stream, skipping the temp-guid/echo dance.
- **`IncomingMessageProcessor`** — persists incoming reactions and raises a reaction notification for others (verb-based body, gated by `NotifyReactions`), never for own/removal.
- **`MessageBubbleViewModel`** — holds raw reaction records, recomputes `Reactions` (badges) + `SelfReactionType`, bumps `ReactionRevision`; `SendReactionAction` callback; de-dupes optimistic vs. echo by GUID.
- **`ChatViewModel`** — loads reactions after each message page and on prepend, attaches them to the last bubble of each parent; live `OnReactionReceived`; `SendReaction` toggle (tapping your active type sends `-type`) with optimistic insert, server call via `SendTapbackAsync`, and response persistence.
- **`ChatBubble`** — reaction pills below the bubble (accent when you reacted, count when >1), and a right-click `MenuFlyout` picker (6 options, checkmark on your active reaction). Pills are also clickable to toggle.

**Key files changed:**
- New: `BlueBubbles.Core/Utils/ReactionTypes.cs`, `ReactionSummarizer.cs`
- `BlueBubbles.Core/Services/MessagesService.cs`, `IMessagesService.cs`, `MessagePersistenceHelper.cs`
- `BlueBubbles.Core/Services/ActionHandler.cs`, `IActionHandler.cs`, `IncomingMessageProcessor.cs`, `Models/SocketEvents.cs`
- `BlueBubbles.Windows/ViewModels/MessageBubbleViewModel.cs`, `ChatViewModel.cs`
- `BlueBubbles.Windows/Controls/ChatBubble.xaml`, `ChatBubble.xaml.cs`
- Tests: new `ReactionTests.cs`, reaction cases added to `IncomingMessageProcessorTests.cs`

**Verify:** Messages with reactions → badges with correct icons. Right-click → pick reaction → appears (optimistically, then confirmed). Tap your reaction again → removed. Other device reacts → real-time pill update. 207 unit tests pass (reaction grouping, toggle, removal, GUID normalization, persistence).

---

## Phase 14 — Reply Threads ✅
**Complexity: Small | Depends on: Phase 6, Phase 7**

Reply threads end-to-end: show the quoted original above a reply, enter reply mode from the bubble context menu, send the thread link, and jump to the original on tap.

**Protocol note:** the server derives the thread from `selectedMessageGuid` + `partIndex` on the normal text/attachment send (already supported since Phase 6.5) — there is no separate `threadOriginatorGuid` send field. The returned/echoed message carries `threadOriginatorGuid`/`threadOriginatorPart` (a plain GUID, no `p:`/`bp:` prefix), which drives display.

**Deliverables:**
- **`IMessagesService.GetMessagesByGuidsAsync`** — batch-loads messages (with handles) to resolve reply originals' snippets. Reply messages are *not* filtered from the main stream (unlike reactions) — they render as normal bubbles with an indicator.
- **`MessageBubbleViewModel`** — `ThreadOriginatorGuid`/`IsReply`, async-resolved `ReplySenderLabel`/`ReplyPreviewText` (+ `ReplyContextReady`), and `StartReplyAction`/`ScrollToMessageAction` callbacks. Only the first bubble of a split (text+attachment) message hosts the indicator.
- **`ChatViewModel`** — `ReplyingTo` reply-draft state, `StartReply`/`CancelReply`, sends the reply via `EnqueueText`/`EnqueueAttachment` with `selectedMessageGuid` = target GUID, optimistic reply bubble, `ResolveReplySnippetsAsync` (prefers loaded bubbles, falls back to DB), and `ScrollToMessageRequested`.
- **`ChatBubble`** — reply indicator (accent left bar + sender + quoted snippet, palette-matched, tap to jump) and a "Reply" item appended to the context menu (after the reaction picker). Reaction-active checkmark switched to the encoding-safe `SymbolIcon(Symbol.Accept)`.
- **`MessageComposer`** — reply preview bar above the input ("Replying to {sender}" + snippet + cancel ✕) with `ShowReply`/`HideReply` and a `ReplyCancelled` event.
- **`ChatPage`** — syncs `ReplyingTo` ↔ composer preview (auto-focuses input), routes cancel, and scrolls to the original on `ScrollToMessageRequested`.

**Key files changed:**
- `BlueBubbles.Core/Services/IMessagesService.cs`, `MessagesService.cs`
- `BlueBubbles.Windows/ViewModels/MessageBubbleViewModel.cs`, `ChatViewModel.cs` (new `ReplyDraft` record)
- `BlueBubbles.Windows/Controls/ChatBubble.xaml(.cs)`, `MessageComposer.xaml(.cs)`, `Views/ChatPage.xaml.cs`
- Tests: new `ReplyTests.cs`

**Verify:** Reply messages show the quoted snippet + sender, tap to scroll to the original. Right-click → Reply → composer enters reply mode (preview + focus). Send reply → optimistic bubble shows the indicator, server creates the thread. 210 unit tests pass (GetMessagesByGuids, thread-link persistence, replies not filtered from the stream).

---

## Phase 15 — Message Actions (Edit, Unsend, Context Menu) ✅
**Complexity: Small | Depends on: Phase 7**

Completed the bubble context menu by wiring the three deferred items (Edit, Undo Send, Delete) left as disabled placeholders during the earlier menu-build sessions. Edit and Undo Send use the Private API edit/unsend endpoints (own messages only); Delete is a local soft-delete. `updated-message` events now drive remote edits/unsends in real time, and edited/unsent messages render correctly on reload.

**Protocol note:** An edit is an `updated-message` carrying the new `text` + a non-null `dateEdited` (the server overwrites `text` with the edited content — see `message.dart` merge, `existing.text = newMessage.text`). An unsend is an `updated-message` whose `messageSummaryInfo[0].retractedParts` contains the part index; the text is treated as retracted. The edit POST sends `editedMessage` + a `backwardsCompatibilityMessage` of `"Edited to: “{text}”"` (matching the Flutter client). Per-message actions use `partIndex: 0`, consistent with the existing reply/reaction code.

**Deliverables:**
- **`MessageEdits`** (`Core/Utils`) — `IsPartRetracted` (over the deserialized `MessageSummaryInfo` model and over the persisted `MessageSummaryInfoJson` column) and `BuildBackwardsCompatText`. Ports the unsend/edit detection from Flutter's `message_widget_controller`/`message.dart`.
- **`MessagesService`** — `UpdateMessageAsync` now persists `MessageSummaryInfoJson` (so an unsend survives a reload) and guards `Text`/`DateEdited` so a later delivery-only update can't wipe an edit (mirrors the Flutter merge). New `SoftDeleteMessageAsync` sets `DateDeleted`; `LoadMessagesAsync` already excludes deleted rows.
- **`MessageBubbleViewModel`** — `Text` is now an `[ObservableProperty]` (an edit rewrites it in place). Added `DateEdited`/`IsEdited`, `IsUnsent`, `ApplyEdit`/`ApplyUnsend` (clears the retracted text), and `StartEditAction`/`UnsendAction`/`DeleteAction` callbacks. Unsent state is detected from the entity on load.
- **`ChatViewModel`** — `EditingMessage` (`EditDraft`) edit-mode state; `StartEdit` (pre-fills the composer, mutually exclusive with reply), `CancelEdit`, `CommitEdit` (optimistic `ApplyEdit` → `SendEditAsync` → persist), `Unsend` (optimistic `ApplyUnsend` → `SendUnsendAsync` → persist), and `DeleteMessage` (removes all bubbles for the GUID, prunes orphan date separators, soft-deletes). `SendMessage` branches to `CommitEdit` in edit mode; typing emission is suppressed while editing. `OnMessageUpdated` rewritten to route retracted → unsend, `dateEdited` → edit, then reconcile delivery status across all bubbles of a message.
- **`ChatBubble`** — the placeholder Edit / Undo Send / Delete items are now enabled and wired. `OnGlyphFlyoutOpening` shows Edit (own, sent, has text, not unsent) and Undo Send (own, sent, not unsent) per message and hides the shared divider for incoming messages; Copy is gated off for unsent. Delete shows a confirmation `ContentDialog`. Renders the "Edited" label in the meta row and a muted italic "This message was unsent" placeholder (`RenderUnsent`).
- **`MessageComposer` / `ChatPage`** — an "Editing message" preview bar (mirrors the reply bar, mutually exclusive with it) with `ShowEdit`/`HideEdit` + `EditCancelled`, synced to `ChatViewModel.EditingMessage`.

**Key files changed:**
- New: `BlueBubbles.Core/Utils/MessageEdits.cs`
- `BlueBubbles.Core/Services/MessagesService.cs`, `IMessagesService.cs`
- `BlueBubbles.Windows/ViewModels/MessageBubbleViewModel.cs`, `ChatViewModel.cs` (new `EditDraft` record)
- `BlueBubbles.Windows/Controls/ChatBubble.xaml(.cs)`, `MessageComposer.xaml(.cs)`, `Views/ChatPage.xaml.cs`
- Tests: new `MessageActionsTests.cs` (`MessageEdits` + `UpdateMessageAsync`/`SoftDeleteMessageAsync`); `SoftDeleteMessageAsync` added to test `IMessagesService` stubs

**Verify:** Right-click own message → Edit → composer enters edit mode (preview bar + focus) → send → text updates + "Edited" label. Undo Send → bubble shows "This message was unsent", content removed. Delete → confirmation → bubble disappears (stays gone after reload). Remote edit/unsend from another device reflected in real time. 220 unit tests pass (retracted-part detection, edit/unsend persistence, soft-delete hides from load, null-text guard).

---

## Phase 16 — Link Previews ✅
**Complexity: Small | Depends on: Phase 6**

Links and their previews are now first-class: URLs in any message body render as clickable hyperlinks, and rich link (URL balloon) messages render a preview card instead of inert URL text plus a "weird attachment that does nothing."

**Protocol note:** A rich link preview is an iMessage URL balloon — `balloonBundleId == "com.apple.messages.URLBalloonProvider"` and/or a `payloadData` of `type: url` whose `urlData[0]` carries `title`/`summary`/`siteName` and the destination `URL` (stored as `{ "NS.relative": "https://…" }`). The preview **image is not a public URL** — Apple's `imageMetadata`/`iconMetadata` URLs are internal/unusable, so the image is delivered as the message's `pluginPayloadAttachment` and is reused as an ordinary cached attachment (matching Flutter's `url_preview.dart`, which finds the attachment whose `transferName` contains `pluginPayloadAttachment`). Click target is `urlData[0].url` (→ `originalUrl` → first URL found in the text).

**Deliverables:**
- **`UrlDetector`** (`Core/Utils`) — source-generated regex finding `http(s)://` and bare `www.` spans; trims trailing sentence punctuation (keeps balanced wiki parens), upgrades bare `www.` to `https://`, and `IsSingleUrl` (a pure-URL message shows only its card). Pure/testable, no UI host.
- **Clickable URLs in all bodies** — `ChatBubble.SetMessageInlines` rebuilds `MessageText` as `Run` + `Hyperlink` inlines (palette-matched, underlined, `NavigateUri` → browser) instead of a flat string. Applies to every message, preview or not.
- **Card is its own surface (not nested in a bubble)** — for a link-preview message `ChatBubble` strips the coloured bubble (transparent background, no padding/corners) so the card stands alone, and recolours the meta row (time/status) to neutral since it no longer sits on an accent fill.
- **`UrlPreview` control + `UrlPreviewViewModel`** — a four-state card: **Rich** (hero image + title + 2-line summary + site), **NeedsPreview** (a "Show preview" affordance for a bare link), **Loading** (spinner), **Generic** (host + URL, still opens). The hero is either the Apple `pluginPayloadAttachment` (local, via `AttachmentCacheService` with a generation guard per [[feedback-async-image-generation]]) or a fetched remote `og:image`. Tapping the card opens the link.
- **On-demand metadata fetch** — new **`LinkPreviewService`** (`Core/Services`, registered with a plain `HttpClient` to the open web, separate from the proxied server client) + **`LinkMetadataParser`** (`Core/Utils`, tolerant regex extraction of Open-Graph / Twitter-card / `<title>`, no HTML-parser dependency). "Show preview" runs `UrlPreviewViewModel.LoadPreviewCommand` → fetch → upgrade to Rich, else fall back to Generic. Wired per-bubble in `ChatViewModel.WireBubble` from an optional `ILinkPreviewService` (DI-injected; null-safe for tests).
- **Clickable URLs in all bodies** — `ChatBubble.SetMessageInlines` rebuilds `MessageText` as `Run` + `Hyperlink` inlines (palette-matched, underlined, `NavigateUri` → browser). Applies to every message.
- **`MessageBubbleViewModel`** — parses `PayloadDataJson` + `BalloonBundleId`, exposes `UrlPreview`/`IsUrlPreview`; a server-rich link starts `Rich`, a bare single-URL message starts `NeedsPreview`. `CreateFromEntity` returns a *single* card bubble (no text/attachment split); the payload file-chip attachment is suppressed.
- **Live path** — `ChatViewModel.OnNewMessageReceived` threads `BalloonBundleId`, `HasApplePayloadData`, `PayloadDataJson`, and attachment `Uti` onto the in-memory entity so a freshly-received preview renders immediately (DB reload round-trips these via `MessagesService`/`MessagePersistenceHelper`).

**Key files changed:**
- New: `BlueBubbles.Core/Utils/UrlDetector.cs`, `LinkMetadataParser.cs`; `BlueBubbles.Core/Models/LinkMetadata.cs`; `BlueBubbles.Core/Services/ILinkPreviewService.cs`, `LinkPreviewService.cs`; `BlueBubbles.Windows/ViewModels/UrlPreviewViewModel.cs`; `BlueBubbles.Windows/Controls/UrlPreview.xaml(.cs)`
- `BlueBubbles.Windows/ViewModels/MessageBubbleViewModel.cs`, `ChatViewModel.cs`; `BlueBubbles.Windows/Controls/ChatBubble.xaml(.cs)`; `BlueBubbles.Windows/App.xaml.cs` (DI)
- Tests: new `UrlDetectorTests.cs`, `LinkMetadataParserTests.cs`

**Verify:** Build + full unit suite green (incl. `UrlDetectorTests`, `LinkMetadataParserTests`). Manual: URL in text → clickable link → browser. Rich server link → standalone card (no surrounding bubble) with image/title/summary; tap → browser. Bare link → "Show preview" → fetch → rich card, or generic card if nothing usable. "text + link" → clickable text *and* card.

---

## Phase 17 — Settings Pages ✅
**Complexity: Medium | Depends on: Phase 5**

Settings is now its own full-window navigation context (spec 8.8) instead of living in the shell's right pane — entering Settings no longer shows the conversation list. A left `NavigationView` (Windows 11 Settings style) hosts 8 category pages, each using `SettingsCard`/`SettingsExpander` and binding directly to the shared `AppSettings`.

**Deliverables:**
- **Navigation restructure** — the conversation-list gear (and tray "Settings") now navigate `MainWindow.RootNavigationFrame` to `SettingsPage` rather than the in-shell `ChatFrame`. `ShellPage` is marked `NavigationCacheMode="Required"` so its selected chat/scroll state survives while Settings is open; the NavigationView back button calls `RootFrame.GoBack()` to restore it. Removed the old in-shell settings handling (`OnSettingsGoBack`, `GoBackRequested` on `SettingsPage`).
- **`SettingsPage` shell** — `NavigationView` (PaneDisplayMode=Left, back button, no pane toggle) with 8 `NavigationViewItem`s + an inner `ContentFrame`; Tag→page-type mapping on selection; default-selects Connection on load. The **Private API** item is gated on `AppSettings.ServerPrivateAPI == true`.
- **8 category pages** under `Views/Settings/`:
  - **Connection** — status, server URL + "Fetch Latest URL", proxy service, Use Local Connection expander (toggle/port/test), vCard import, connection log (copy/clear). Reuses the existing `SettingsViewModel`.
  - **Notifications** — notify-in-unfocused-chats, notify-for-reactions, filter-unknown-senders.
  - **Appearance** — theme (System/Light/Dark, applied live), colorful avatars/bubbles, dense tiles, hide dividers, 24-hr time, avatar-size slider.
  - **Messaging** — display name, auto-download, send-with-Enter, delivery timestamps, chat-list indicators, scroll-to-last-unread, send delay (NumberBox 0–10).
  - **Private API** (gated) — send typing indicators, mark-chat-as-read, manual-mark-as-read (the always-on collapsed model from Phase 6.5; no per-send toggles).
  - **Server Management** — live server info, statistics, soft/hard/iMessage restart (confirmed), check/install update, server logs viewer, and the **Reset App** danger zone (moved here from the old flat page).
  - **Backup & Restore** — settings + theme backup save/restore/delete against the `backup/settings` and `backup/theme` endpoints (round-trips this app's own schema).
  - **About** — app version (from package), live server version, GitHub/Discord/Docs links.
- **Theme application** — new `Services/ThemeHelper` maps `AppSettings.Theme` (0=System/1=Light/2=Dark) to `ElementTheme` and applies it to the window root; applied at startup in `MainWindow` and live from the Appearance page.
- **Auto-save** — new `Services/SettingsAutoSave` wires each category page's `AppSettings` edits (x:Bind TwoWay) to `ISettingsService.Save()` while loaded.

**Key files:**
- New: `Views/SettingsPage.xaml(.cs)` (rewritten as NavigationView shell), `Views/Settings/{Connection,Notification,Appearance,Messaging,PrivateApi,ServerManagement,Backup,About}SettingsPage.xaml(.cs)`, `Services/ThemeHelper.cs`, `Services/SettingsAutoSave.cs`
- Modified: `Views/ShellPage.xaml(.cs)` (RootFrame nav + cache), `MainWindow.xaml.cs` (startup theme)

**Verify:** Build clean (0 warnings). Entering Settings hides the conversation list and shows the category NavigationView; back restores the open chat. Theme switch applies live and persists. Connection test/fetch work. Server restart/update/logs hit the API with confirmation. Toggles persist across restart. Private API category hidden when the server lacks Private API.

---

## Phase 18 — Polish, Accessibility, & Performance ✅
**Complexity: Medium | Depends on: all**

Closed out the polish phase. Several performance foundations were already in place from earlier phases and were verified rather than rebuilt: the message thread and conversation list use virtualizing panels (`ItemsStackPanel` with `ItemsUpdatingScrollMode="KeepLastItemInView"`; the default `ListView` panel for chats), the `BlueBubblesDbContext` already indexes every Guid/foreign-key/date column it queries on (`ChatId`, `DateCreated`, `AssociatedMessageGuid`, `ThreadOriginatorGuid`, `LatestMessageDate`, `IsPinned`, …), attachments are thumbnail-cached on disk by `AttachmentCacheService`, and window size/position already persisted via `MainWindow` + `AppSettings`. This phase added the missing accessibility, keyboard, animation, startup, and session-restore work.

**Deliverables:**
- **Launch-at-startup** — new `StartupTaskService`. New **General** settings category (`GeneralSettingsPage`) exposes "Launch at sign-in" (+ "Start minimized"), plus the previously-unexposed "Minimize to tray" / "Close to tray" toggles. `App.ReconcileStartupState` syncs the setting to the registered state on launch and, when launched with the minimized flag, hides the window to the tray (`MainWindow.HideToTray`). *(Note: originally implemented via the packaged `Windows.ApplicationModel.StartupTask` API; reworked to a per-user `HKCU\…\Run` registry entry + a `--minimized` launch arg during the unpackaged-distribution work below, since the packaged API needs MSIX identity.)*
- **Settings persistence true-up** — `SettingsService`'s `PersistedSettings` only serialized a subset of fields; Theme, all Appearance/Messaging toggles, the Private-API preferences, and `UseLocalIpv6` were silently *not* persisted across restarts. Expanded `Save`/`Load`/`PersistedSettings` to round-trip every user-facing setting, with record defaults matched to the `AppSettings` constructor so an older settings file never clobbers a default (e.g. `SendWithReturn`/`AutoDownload`/`CloseToTray` stay `true`).
- **Session restore (selected chat)** — new `AppSettings.LastSelectedChatGuid`, saved by `ShellPage` when a conversation is opened and restored by `ConversationListPage` after the first chat load (guarded so it never fights in-session navigation).
- **Keyboard navigation** — `ShellPage` `KeyboardAccelerator`s: **Ctrl+N** (new chat), **Ctrl+F** (focus search via `ConversationListPage.FocusSearch`), **Escape** (close the open conversation → empty state via `ClearSelection`). Tab/arrow traversal and list item navigation come from the standard WinUI controls.
- **Accessibility** — `AutomationProperties.Name` on the icon-only controls Narrator couldn't read (chat-details/info, scroll-to-bottom, new-message, settings, archive, back buttons across ChatPage/ConversationListPage/ChatDetailsPage/NewChatPage/FullscreenMediaPage, and the composer's attach/send/input). High-contrast support is inherent: the UI is built entirely on `ThemeResource` system brushes, which remap under high-contrast themes.
- **Animations** — `NavigationThemeTransition` on the `ChatFrame` for page transitions; `ItemContainerTransitions` (Add/Delete/Reorder/Content) on the conversation list so pin/reorder/new-chat changes animate; `AddDeleteThemeTransition` on the message list so new bubbles ease in.

**Key files:**
- New: `Services/StartupTaskService.cs`, `Views/Settings/GeneralSettingsPage.xaml(.cs)`
- `Core/Configuration/AppSettings.cs` (`LastSelectedChatGuid`), `Core/Services/SettingsService.cs` (full-fidelity persistence)
- `MainWindow.xaml.cs` (`HideToTray`), `App.xaml.cs` (DI + `ReconcileStartupState`), `StartupTaskService` (per-user `HKCU\...\Run` registry entry — the unpackaged replacement for the MSIX startupTask extension)
- `Views/ShellPage.xaml(.cs)` (accelerators, nav transition, selection persistence), `Views/ConversationListPage.xaml(.cs)` (restore, focus/clear, list transitions), `Views/SettingsPage.xaml(.cs)` (General category)
- `Controls/MessageComposer.xaml`, `Views/ChatPage.xaml`, `Views/{ChatDetails,NewChat,FullscreenMedia}Page.xaml` (AutomationProperties + transitions)
- Tests: `SettingsServiceTests.cs` (full round-trip + old-file default preservation)

**Verify:** Build clean (0 warnings). 246 unit tests pass (incl. new settings round-trip / default-preservation). Theme and all toggles now persist across restart. Open a chat, relaunch → it reopens. Ctrl+N/Ctrl+F/Escape work. Narrator announces icon buttons. New messages and pin/reorder animate. General settings → "Launch at sign-in" registers the startup entry (and reflects an external change).

---

## Distribution & Packaging ✅

**Decision:** ship as a **free, unpackaged, self-contained `.exe` installer** (no MSIX, no certificate) for the public GitHub release. The build bundles the .NET + Windows App SDK runtimes, so it runs on a clean machine with no prerequisites. Trade-off: the unsigned `.exe` shows a one-time SmartScreen "unknown publisher" prompt until reputation builds (or until code signing is added). MSIX was fully removed — its code-signing requirement had broken toast-notification activation, so packaged identity is no longer used anywhere.

**Unpackaged rework (package identity → identity-free):** going unpackaged means package-identity APIs throw, so:
- **Credentials** — `CredentialService` replaced `Windows.Security.Credentials.PasswordVault` (needs identity) with **DPAPI** (`ProtectedData`, `CurrentUser`) writing `LocalAppData\BlueBubbles\credential.bin`.
- **Launch-at-startup** — `StartupTaskService` replaced the packaged `StartupTask` API with the per-user **`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`** entry; "start minimized" is encoded as a `--minimized` arg that `App` reads to start hidden.
- **About version** — reads the **assembly version** instead of `Package.Current.Id.Version`.
- **Toasts** — `AppNotificationManager.Default.Register()` is wrapped in try/catch (every `Show` was already guarded), so a registration failure degrades gracefully instead of crashing launch.
- **Single-instancing** — `AppInstance` already works unpackaged; unchanged. WinApp SDK bootstrap **auto-initializes** even with the custom `Program.Main` (verified the published exe launches).

**Tooling:**
- **`publish.ps1`** — publishes unpackaged self-contained (via the `win-<arch>.pubxml` profiles, now `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`), then builds a per-user (no-UAC) **Inno Setup** installer → `dist\BlueBubbles-Setup-<version>-<arch>.exe` (~60 MB). Falls back to a portable `.zip` if Inno Setup is absent. Version read from csproj `<Version>`.
- **`installer/BlueBubbles.iss`** — Inno Setup script: installs to `%LocalAppData%\Programs\BlueBubbles`, Start-menu + optional desktop shortcut, upgrade-in-place, uninstaller.
- **`INSTALL.md`** — end-user + maintainer instructions; signing options (Azure Trusted Signing ~$10/mo, or OV cert) to remove the SmartScreen prompt.

**New `System.Security.Cryptography.ProtectedData` package** added for DPAPI.

---

## Architectural Decisions That Minimize Rework

1. **Complete models from Phase 1** — MessageEntity includes reaction, reply, edit, unsend, and effect fields from the start. No schema migrations needed when those UIs arrive in Phases 13-16.
2. **All 75+ API methods in Phase 2** — FindMy, FaceTime, Scheduled Messages, Backup methods exist even though their UIs are v1-excluded. Future phases call them immediately.
3. **All 11 socket events wired in Phase 3** — ActionHandler processes events before any UI depends on them. The DB stays current throughout development.
4. **Two-project split enforced from Phase 0** — `BlueBubbles.Core` has zero UI references. Unit-testable without a UI host.
5. **ObservableObject/ObservableCollection everywhere** — CommunityToolkit.Mvvm reactive bindings mean socket events automatically flow to UI through the binding chain.

## Critical Flutter Source Files to Reference

| Purpose | Path |
|---------|------|
| HTTP API (all endpoints) | `lib/services/network/http_service.dart` |
| Socket.IO state machine | `lib/services/network/socket_service.dart` |
| Event dispatch + message matching | `lib/services/backend/action_handler.dart` |
| Message entity (~80 fields) | `lib/database/io/message.dart` |
| Settings fields | `lib/database/global/settings.dart` |
| AES encryption | `lib/utils/crypto_utils.dart` |
| Google OAuth (desktop) | `lib/helpers/ui/oauth_helpers.dart` |
| Firebase URL resolution | `lib/services/network/firebase/firebase_database_service.dart` |
| Full sync engine | `lib/services/backend/sync/full_sync_manager.dart` |
| Localhost detection | `lib/helpers/network/network_tasks.dart` |
