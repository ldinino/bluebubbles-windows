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

#### B9. Class B attachment groups may still double-render — a *different* bug from B7
- [ ] Carried forward from B7 (archived, shipped 0.24.0). B7 fixed **Class A**: two rows sharing
  `(MessageId, OriginalRowId)`, caused by Apple rewriting the GUID mid-transfer. The pre-sync
  snapshot also held **41 groups / 82 rows** with *distinct* `OriginalRowId`s — two genuine server
  rows for the same file. Those are legitimate and must survive; collapsing them client-side would
  fight the server. **If photos still double after 0.24.0, this is the remaining cause** and it
  needs a server-side answer, not another dedupe rule.
- Maintainer's 2026-08-23 pass saw no doubling, but Class B was not specifically hunted for.
- [ ] **Unexplained, worth knowing before assuming the write-side fix is sufficient:** Class A begins
  abruptly at `OriginalRowId` 9022 / 2026-06-08 in a cache reaching back to 2025-08-02. Something
  changed then and it was not this codebase.

#### B10. `ChatDetailsViewModel.RefreshParticipantsAsync` reads stale in-memory chats
- [ ] Carried forward from B6 (archived). It reads `_chatsService.Chats` rather than the database,
  so an open details pane can lag one beat behind the now-correct persisted state. Small, and only
  visible with the pane open while a rename or participant change arrives.

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
  number.
- [ ] **Repro (maintainer, 2026-08-29): reset the local cache, then open a thread fresh with verbose
  logging on.** That forces every attachment back to `NotDownloaded`, which is the state the fix
  changes behaviour in — and it is why every previous attempt measured nothing. Capture the
  auto-download order and the time-to-first-visible, and compare against the pre-fix baseline below.
  Depends on the draw-timing instrumentation in B2b to produce a time-to-first-visible number at all.
- [ ] **Behaviour changes to watch for in use:** attachments in messages never scrolled to are no
  longer prefetched (intended), and scroll-back pages now auto-download where they never did before
  (`PrependMessages` never called `TriggerAutoDownload`). If fast scrolling now feels worse, this is
  the trade to revisit.

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

#### B2b. UI verification is instrumentation-based — give verbose logging draw timing
- **Reframed by the maintainer, 2026-08-29, and the evidence supports it.** This entry used to ask
  whether to add a `net8.0-windows` test project. That is the wrong question: **every UI defect this
  project has actually settled was settled by log counts, not tests** — B2f (10 decode starts / 5
  stranded -> 4 / 0), B2h (discarded decode work 50% -> 21%, from a caller-breakdown table), B2g
  (19 auto-downloads inside 20 ms in ascending date order). The missing capability is *measurement*,
  not a test host.
- [ ] **Add draw/decode timing to verbose logging.** Enough to answer "what did this cost and in what
  order": decode start -> landed duration, relayout counts per control with the caller, and a
  time-to-first-visible for a thread open. Aggregate it into a dumpable per-session summary rather
  than leaving the reader to count lines in a log by hand, which is how B2f/B2h/B2g were each read.
- Two consumers already waiting on it: **B2g** cannot produce a time-to-first-visible number without
  it, and **B2k** (an idle avatar reaching `gen=13` with nobody touching the app) is exactly the
  "what is republishing these properties" question this instrumentation answers.
- **The logic half is already handled, and differently.** F5 (`38dc7c8`) established the pattern:
  lift the *decision* into `BlueBubbles.Core` as a pure function (`ToastActivationRouter`, with
  `ActivatesWindow`), where the existing suite already reaches it, and leave rendering in the view.
  Apply that per-bug. **Do not** add a `net8.0-windows` test project on the strength of this entry;
  between the Core-decision pattern and draw timing, the residual gap is rendering itself, which is
  eyeball work regardless.

#### B2c. `GetMessagesByGuidsAsync` omits `.Include(m => m.Attachments)`
- [ ] `LoadMessagesAsync` and `LoadMessagesAfterAsync` include it; this one doesn't. Harmless today
  (reconcile and reply snippets don't read attachments) but it is exactly the query a B2 fix would
  reuse, and it would silently return zero attachments.

#### B2d. `ChatBubble.Unloaded` nulls `_currentVm` but not `_renderedContentForVm`
- [ ] The two fields are meant to move together; no misrender case was constructed. Low confidence
  that this is real, recorded so it isn't rediscovered.

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

**Execution plan (head engineer, 2026-08-29) — measured, not assumed.** Counting construction sites
by file: `ChatsService.cs` **6**, `SyncService.cs` **6**, `MessagePersistenceHelper.cs` 1,
`MessagesService.cs` 1 — all in `BlueBubbles.Core`. Every W2 target is in `BlueBubbles.Windows`
(7 files, ViewModels/Views). So:
- **W1b and W2 run in parallel — zero file overlap.** Separate worktrees under `..\scratch\`.
- **W1b and W1c must be sequential**: they concentrate in the *same two files*
  (`ChatsService.cs`, `SyncService.cs`). Two agents there would fight over every hunk.
- **W1a-2 last**, as its own entry already requires.
- W3 is a decision, not a diff — the head engineer settles "dead vs staged" with the maintainer.
- Whoever lands second rebases on `main` before the PR.

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

#### W3. Dead transport scaffolding — **DECIDED: delete it**
- **Decision (maintainer, 2026-08-29): FaceTime and Find My are not planned.** So this is dead, not
  staged. Remove it rather than carrying an unimplemented surface that reads like a roadmap.
  Re-adding it later is cheap — these are thin wrappers over documented server endpoints, and the
  Flutter client remains the protocol reference.
- **The audit's description was wrong in two ways — corrected by measurement, 2026-08-29:**
  - "Three socket events registered and raised with zero subscribers" — all three *are* registered,
    but via two different mechanisms: `SocketService.cs:96-97` registers `FtCallStatusChanged` and
    `IMessageAliasesRemoved` through `RegisterEvent`, while `IncomingFacetime` gets its own explicit
    `_socket.On` handler at `:104`. Only `IncomingFacetime` and `FtCallStatusChanged` reach
    `ActionHandler`; `IMessageAliasesRemoved` is a constant with no handler at all.
  - "Zero subscribers" is not quite true either: **`ActionHandlerTests.cs:186` subscribes to
    `FaceTimeStatusChanged`**. There is a live test to delete with it.
- **This is not a pure deletion — it reaches into the test project.** Full measured footprint:
  - Whole files: `Models/FindMyDevice.cs`, `Models/FindMyFriend.cs`, `IFaceTimeService.cs`,
    `IFindMyService.cs`
  - `SocketEvents.cs` 3 constants; `SocketService.cs:96-97,104-114`; `ActionHandler.cs` 2 events +
    2 `case` arms; `IActionHandler.cs` 2 event declarations
  - `IBlueBubblesApiService.cs` + `BlueBubblesApiService.cs`: **7 methods** (1 FaceTime
    availability, 4 Find My, 2 FaceTime answer/leave)
  - Test stubs implementing those 7: `OutgoingMessageServiceTests.cs:413-423`,
    `SyncServiceTests.cs:872-882`, plus the `ActionHandlerTests.cs` subscription
- [ ] **Sequenced after W1b merges.** The stub edits land in `SyncServiceTests.cs`, which W1b is
  live in right now; doing both at once buys a conflict for no reason. Zero urgency.
- Nothing else depends on it: no DI registration in `App.xaml.cs`, and no implementation of either
  interface exists.

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

#### F6. Export conversations for archival
Let a user save their chat history to disk from **Settings > Backup & Restore** — keeping a personal
record, or producing a readable transcript as evidence. Export only; **no re-import** (restore is a
much larger problem and bundling them sinks both).

- **The page already exists but does something else.** `BackupSettingsPage` currently backs up *app
  settings* to the BlueBubbles server (`SettingsBackupName = "BlueBubbles WinUI Settings"`). This is
  a second, unrelated capability on the same page — do not entangle it with the settings payload.
- **MEASURED — the honesty problem, and the most important part of this feature.** The local cache
  is **not** the full history. `ChatEntity.OldestSyncedMessageDate` is a per-chat watermark
  (`= 0` means synced back to the beginning). `MessagesService.LoadMessagesAsync(chatId, limit,
  beforeDate)` reads the **local cache only**. So a naive export silently produces a *partial*
  archive while looking complete — worst case for someone relying on it as a record.
  **Requirement:** the export must state its own coverage (per-chat oldest synced date, and whether
  it reaches the beginning), and the UI must not imply completeness. Offer to run the existing
  older-message sync first rather than silently fetching inside the export.
- **Format decision (maintainer + head engineer, 2026-08-29): JSONL, one file per conversation**,
  plus a `manifest.json`. Rejected XML: message text is full of `<`, `>`, `&`, emoji and newlines,
  where one bad control character invalidates an entire document, and JSONL streams line-by-line so
  a corrupt line costs one message instead of the file.
- **Also emit a plain-text transcript (`.txt`) per conversation, selectable.** Nobody reads JSONL,
  and the archival/evidence use is the documented purpose. Plain text, not HTML — HTML with
  embedded media means asset copying and relative paths, which is scope creep.
- **Record shape matters more than the container.** From `Message.cs`, all present already:
  `isFromMe`; `associatedMessageGuid`/`associatedMessageType` (**tapbacks are separate messages** —
  fold onto the parent or the transcript fills with `Liked "..."` noise); `threadOriginatorGuid` for
  real reply structure; `dateCreated`; `itemType`/`groupActionType` non-zero = system event, not
  speech; `dateEdited` (export the final text); `hasAttachments` -> explicit placeholder, never a
  silent empty message.
- **Timestamps as ISO 8601 with offset.** The stored value is a Unix ms epoch; a bare local time in
  an archive is ambiguous and worthless a year later.
- **Scope guard (head engineer): attachments are referenced by filename, not fetched.** Copy a
  cached attachment into `attachments/` when the file is already local; never pull from the server
  during an export. Record a placeholder for anything uncached so the gap is visible.
- **Deterministic filenames** (slug + short GUID hash) so a re-export diffs cleanly instead of
  duplicating.
- **`FolderPicker` is not used anywhere in this codebase yet.** Unpackaged, it needs the same
  `InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow))` interop as
  the 7 existing `FileOpenPicker`/`FileSavePicker` call sites (e.g. `FullscreenMediaPage.xaml.cs:160`).
  A picker without it throws when unpackaged (CLAUDE.md identity rules).
- **"Export all" needs progress and cancellation.** On a mature cache this is a lot of rows; without
  it the window looks hung.
- [ ] One line on the page stating the export is unencrypted plaintext. No further ceremony.
- **Testability:** put the record-building and transcript-rendering in `BlueBubbles.Core` as pure
  functions over messages, following the F5 precedent (`ToastActivationRouter`) — see B2b. The
  picker and file IO stay in the view.

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

**0.24.0 — PUBLISHED 2026-08-23** (`gh release list` shows `v0.24.0` as Latest). Shipped F5 (toast
actions) and U1 (update checker), plus the B7 duplicate-attachment fix; details in
`docs/PUNCHLIST-ARCHIVE.md`. CI run `32671986227`; asset `BlueBubbles-Setup-0.24.0-x64.exe` carries
`digest: sha256:d15575e4...`, confirming the field the updater depends on is really published, and
the name matches `AssetPrefix`/`AssetSuffix` exactly.
- **First release attempt (`32671654362`) failed CI** on `ActionHandlerTests
  .NewMessage_FromMe_NoTempGuid_DelaysBeforeFiring` — a 700ms fixed wait with only 200ms of slack
  above the production 500ms delay. Fixed in `2f9d80b` by waiting on a `TaskCompletionSource`.
  **The test was also hollow:** with an explicit rebuild, deleting the production delay passed the
  old test (970ms) and fails the new one (`Assert.NotSame`, 295ms vs 792ms real). Debug-only local
  runs never saw it; CI runs Release.
- [ ] **The next release is the first real test of the updater.** The 0.23.0 -> 0.24.0 hop could not
  exercise it (0.23.0 has no update check), so download -> verify -> launch and the SmartScreen
  prompt are still unproven. Watch that hop deliberately rather than assuming it works.

**Future minor** — audio message support (F2), scheduled-send enhancements (F4).

No version bump: repo hygiene (H2). Stretch goal (no schedule): arm64 (S1).
