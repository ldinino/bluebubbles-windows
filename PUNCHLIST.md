# Post-Phase 6 Punchlist

Work through these items **in order** before starting Phase 7. Each item is self-contained. Mark `[x]` when done.

---

## Critical — Will cause bugs/crashes

### 1. Replace server-based contact resolution with local vCard import
The current `ContactResolverService` loads contacts from the DB (synced from the server). Replace this entire approach with local vCard (.vcf) parsing.

**Status: DONE** — completed in prior chat sessions.

### 2. Fix JSON serialization policy mismatch — silent data loss
`SyncService` and `MessagesService` use default `JsonSerializerOptions` (PascalCase), but `MappingExtensions` uses `CamelCase`. JSON blobs written during sync have PascalCase keys; when read back via `MappingExtensions.ToDto()`, fields silently deserialize to null.

**Status: DONE** — `JsonDefaults.Options` created and all call sites updated in prior chat sessions.

### 3. Fix thread-unsafe collections in ActionHandler — intermittent crashes
`_handledNewMessages` and `OutOfOrderTempGuids` are plain `List<string>` mutated from socket callbacks, timer threads, and `OutgoingMessageService` concurrently.

**Status: DONE** — ConcurrentDictionary replacements and eviction cap added in prior chat sessions.

### 4. Fix `SendMessageAsync` hang — no timeout or cancellation
`SocketService.SendMessageAsync` creates a `TaskCompletionSource` with no timeout. If the server never ACKs, the caller hangs forever.

**Status: DONE** — CancellationToken + 30s default timeout added in prior chat sessions.

### 5. Fix double-update of `LatestMessageDate` — race condition
Both `MessagesService.SaveIncomingMessageAsync` and `ChatsService.HandleNewMessageAsync` update `LatestMessageDate` on the same chat using separate `DbContext` instances.

- [x] Remove the `LatestMessageDate`/`HasUnreadMessage` update from `MessagesService.SaveIncomingMessageAsync` — let `ChatsService.HandleNewMessageAsync` be the single owner
- [x] Verify `ChatsService.HandleNewMessageAsync` is always called after `SaveIncomingMessageAsync` in the `ActionHandler` flow

---

## Important — Correctness issues, resource leaks, threading

### 6. Fix `SocketService.State` background-thread PropertyChanged — crashes UI
`State` and `LastError` are `[ObservableProperty]` fields set from socket callback threads. WinUI3 requires PropertyChanged on the UI thread.

- [x] ~~Give `SocketService` a `DispatcherQueue` reference~~ — not appropriate; `SocketService` is in platform-agnostic `BlueBubbles.Core`
- [x] ~~Marshal all `State`/`LastError` updates through `DispatcherQueue.TryEnqueue`~~ — handled at ViewModel level instead
- [x] Have the consuming ViewModels handle the marshaling (ConversationListViewModel already does; SettingsViewModel now does too)

### 7. Fix `SettingsViewModel` socket PropertyChanged not marshaled to UI thread
`SettingsViewModel.cs` lines 38-44 sets `ConnectionState` directly from the socket's background-thread `PropertyChanged` callback. `ConversationListViewModel` correctly wraps this in `RunOnUI`; `SettingsViewModel` does not.

- [x] Wrap the socket `PropertyChanged` handler in `RunOnUI(() => { ... })`

### 8. Fix thread-unsafe dictionaries in `OutgoingMessageService` and `ContactResolverService`
- [x] `OutgoingMessageService._delayCancellations`: replace `Dictionary` with `ConcurrentDictionary`
- [x] `ContactResolverService._nameCache`/`_avatarCache`: already using `ConcurrentDictionary` (fixed in prior pass)

### 9. Fix `CancellationTokenSource` leaks
- [x] `OutgoingMessageService.cs` ~line 69: add `cts.Dispose()` in the `finally` block alongside the dictionary removal
- [x] `SetupViewModel.cs` ~line 80: call `_browserAuthCts?.Dispose()` before replacing with a new instance

### 10. Fix `AvatarControl.GetColorForText` — non-deterministic and can crash
- [x] Replace `String.GetHashCode()` with a deterministic hash (DJB2 with `uint`)
- [x] Replace `Math.Abs(hash)` — using `uint` throughout eliminates both the overflow risk and the need for `Math.Abs`

### 11. Fix `SyncService` duplicate detection
`RunFullSyncAsync` always calls `db.Chats.Add()` without checking if a chat with the same GUID already exists. Re-running sync creates duplicates.

- [x] Use upsert pattern: check `db.Chats.AnyAsync(c => c.Guid == chatGuid)` before adding, or use `db.Chats.Update()` for existing records — already implemented (FirstOrDefaultAsync + conditional Add)
- [x] Same for handles and messages during sync — already implemented

### 12. Fix `ConversationListPage` re-assigning `ItemsSource` on every `CollectionChanged`
Lines 35-45 re-set `ItemsSource` on every collection change, forcing full ListView rebuild.

- [x] Remove the `CollectionChanged` handlers that re-assign `ItemsSource`
- [x] Set `ItemsSource` once in `OnLoaded` (already done) and let `ObservableCollection` handle updates

### 13. Fix `RebuildList` breaking selection state
`ConversationListViewModel.RebuildList` creates new VM instances every time, so `SelectedConversation` no longer matches.

- [ ] Instead of recreating all tiles, diff against existing tiles by chat GUID and update in-place
- [x] Or: after rebuilding, re-select by matching the previously selected chat GUID

### 14. Fix `ServerConnectPage.xaml` `IsReachable` binding
`Visibility="{Binding IsReachable}"` binds a bool directly to a `Visibility` enum — silently fails.

- [x] Add `Converter={StaticResource BoolToVisibilityConverter}` to the binding

### 15. Fix `MessageComposer` `DefaultButtonStyle` crash
`Application.Current.Resources["DefaultButtonStyle"]` throws `KeyNotFoundException` — not a standard WinUI3 resource.

- [x] Replace with `SendButton.Style = null` to revert to the default button style

### 16. Fix `CloudflareRetryHandler` `HttpRequestMessage` leak
The retry `HttpRequestMessage` at ~line 28 is never disposed.

- [x] Wrap in `using` or dispose after `base.SendAsync`

### 17. Fix `ShellViewModel` namespace
`namespace BlueBubbles.Windows.Views` should be `namespace BlueBubbles.Windows.ViewModels`.

- [x] Update the namespace declaration

---

## Minor — Polish and robustness

### 18. `ChatBubble` Unloaded cleanup
- [x] Add `Unloaded` handler that unsubscribes `_currentVm.PropertyChanged`

### 19. `ChatPage.OnLoaded` duplicate scroll handler subscription
- [x] Guard against re-subscribing `_scrollViewer.ViewChanged` on repeated `Loaded` events (unsubscribe first)

### 20. `GoogleSignInPage` WebView2 leak
- [x] Call `OAuthWebView.Close()` in `Unloaded`

### 21. `GenerateTempGuid` truncation — low entropy
13-char temp GUID has only 32 bits of randomness (~50% collision at 65k messages).
- [x] Increased to 25 characters: `$"temp-{Guid.NewGuid():N}"[..25]`

### 22. Silent `catch {}` blocks in `SyncService`
- [x] Add logging to bare catch blocks in `SyncService.RunFullSyncAsync` so failures are visible in the app log

### 23. `Contact.FirstName`/`LastName` data loss in `MappingExtensions`
- [x] Moot — will be addressed by vCard rewrite (item 1)

### 24. Missing index on `MessageEntity.ChatId`
- [x] Add `.HasIndex(e => e.ChatId)` in `BlueBubblesDbContext.OnModelCreating`

### 25. Dead `PayloadType` enum
`PayloadData.cs` declares `PayloadType` enum but `PayloadData.Type` is `int`, not `PayloadType`.
- [x] Use the enum for `Type` instead of `int`

### 26. `PinIndex` not mapped in `Chat.ToEntity()`
- [x] N/A — `Chat` DTO doesn't carry `PinIndex` (server doesn't provide it); sync code already preserves local `PinIndex` by not overwriting it

### 27. `SyncCollection` causes UI flicker
`ConversationListViewModel.SyncCollection` fires N+1 `CollectionChanged` events (Clear + N Adds).
- [x] Use a diffing approach: replace only at positions where the chat GUID changed, add/remove at the tail

### 28. `DraggableDivider` cursor allocation
- [x] Cache `InputSystemCursor` instances in static fields instead of creating new ones on every pointer enter/exit

---

## Test gaps to close

### 29. Add tests for `MessagesService.SaveIncomingMessageAsync`
Currently zero test coverage on the critical incoming message path.
- [x] Duplicate message detection (same GUID)
- [x] Handle upsert (new handle created if not exists)
- [x] Chat lookup (message for unknown chat)
- [x] ~~`LatestMessageDate` / `HasUnreadMessage` update~~ — removed from `MessagesService` (item 5); test that it does NOT update these fields
- [x] Concurrent saves (verify `_saveLock` serialization)

### 30. Add tests for `SyncService`
- [x] Chat pagination (offset increments, break on empty page)
- [x] Handle deduplication
- [x] Duplicate sync run (re-running doesn't create duplicate records — after item 11 fix)
- [x] Cancellation token propagation
- [x] Progress reporting

### 31. Fix flaky `OutgoingMessageServiceTests`
Three tests use `Task.Delay` for synchronization:
- [x] `SendDelay_CanBeCancelled` — use event-based sync instead of racing against a 10s delay
- [x] `PrivateApi_SendsCorrectMethod` — replace `Task.Delay(500)` with event wait
- [x] `SentMessage_RemovesFromOutOfOrderTempGuids` — same

### 32. Fix `DataModelTests.Database_CreatesAllTables` no-op assertion
`Assert.NotNull(_db.Chats)` always passes — `DbSet` properties are never null.
- [x] Replace with `_db.Chats.ToList()` (throws if table doesn't exist)

### 33. Add vCard parsing tests (part of item 1)
**Status: DONE** — vCard parsing tests added as part of item 1 in prior chat sessions.

---

## Backlog — Release & CI

### 34. GitHub Actions: build + attach installer to a Release
Automate cutting a release so it's a one-tag operation.
- [ ] Workflow on tag push (e.g. `v*`): `windows-latest` runner, `dotnet test`, then `.\publish.ps1` for x64 (and arm64).
- [ ] Install Inno Setup on the runner (`winget`/Chocolatey) so `publish.ps1` produces the `Setup.exe`.
- [ ] Create a GitHub Release and upload `dist\BlueBubbles-Setup-<version>-<arch>.exe` as an asset.
- [ ] (Later) add a code-signing step (Azure Trusted Signing) before upload to remove the SmartScreen prompt.

### 35. Fix flaky `IncomingMessageProcessorTests.Reaction_FromOther_PersistedAndNotifies`
Test waits on the `ReactionSaved` persistence signal, then asserts on `notifSvc.Notifications`,
which is raised on a separate async hop — under machine load the notification isn't recorded yet
and `Assert.Single` fails. Passes in isolation; flaked once during a heavy installer build.
- [ ] Also wait for the notification (event/TCS) before asserting, instead of relying on ordering.
