# Punchlist

> Completed work lives in `docs/PUNCHLIST-ARCHIVE.md` — moved out to keep this file focused on
> what's left.

---

## Open bugs

#### B8. Deleting the newest message left a stale conversation-list preview — **FIXED** (`197fe7d`, PR 14, merged `98db324`)
- **The original entry was wrong on the symptom** and is kept corrected here: it claimed the open
  thread "only updates because the caller happens to refresh". It does not — `ChatViewModel` removes
  the bubbles itself, synchronously (`ChatViewModel.cs:1466-1468`, both bubbles of a
  text+attachment message sharing one GUID). **The thread was never broken.** The real gap was the
  conversation list, whose tile preview is derived live from the newest non-deleted, non-reaction
  message (`ChatsService.cs:91-95`).
- [x] **Fixed by classifying on what actually happened, not on which method raised the event.**
  `DeleteMessageAsync` announces `NewOrUpdated` unconditionally; the two reconcile paths announce
  `NewOrUpdated` only when `ReconcileWindowAsync` actually pruned something (its `int` return is the
  soft-delete count), `ServerTrueUp` otherwise. `SyncService.ReconcileChatWindowAsync` now returns
  `Task<int>` to carry it. 572/572.
- **Conservative over-signal chosen deliberately.** `pruned > 0` also fires when a *mid-history*
  message was pruned, which cannot change the tile. The precise alternative — comparing the newest
  non-deleted message before and after — costs **2 extra DB round-trips on every reconcile**, i.e. a
  permanent tax on the frequent path (`RefreshLatestFromServerAsync`, every full-sync chat) to avoid
  one coalesced reload in a rare case. Conservative is strictly cheaper on the hot path. **Do not
  "optimise" this into a newest-message comparison without new measurements.**
- **Span-bound finding, worth keeping:** the reconcile can only prune the newest local message when
  a server message shares an equal-or-newer `DateCreated`, because pruning is bounded to
  `[oldest..newest]` of the server page. So `DeleteMessageAsync` is the real B8 vector; the
  reconcile half is convergence insurance.
- Verified by me, not accepted from the report: red-first reproduced exactly (**3 fail / 9 pass** on
  unfixed Core, the three being the new tests); mutating the reconcile to announce `NewOrUpdated`
  unconditionally — the "simplification" a future maintainer would make — fails
  `FullSync_WindowReconcile_AnnouncesServerTrueUp` (571/572). The prune tests assert `DateDeleted`
  *before* asserting the kind, so they cannot pass vacuously.
- **Existing assertion changed, and it was right to change it.** `DeleteMessage_AnnouncesTheSoftDelete`
  asserted `ServerTrueUp` — it *pinned the bug*. Rewritten as
  `DeleteMessage_AnnouncesAListAffectingKind`, asserting through `AffectsConversationList` rather
  than the enum so it survives a future third kind. Slightly less specific, but mutation-proven not
  hollow, and `ServerTrueUp` remains pinned by the two reconcile tests.
- Side effect, accepted: `ChatViewModel.cs:157` passes `NewOrUpdated` through, so a delete now also
  wakes `AppendPersistedMessagesAsync`. It only appends rows the view lacks, so a delete gives it
  nothing to do — one wasted DB read on a rare user action.
- [x] **Human-verified by the maintainer, 2026-08-29:** deleting the newest message updates the
  chat-list preview to the older message. The `AffectsConversationList` subscriber link is now
  confirmed by observation, not just by compilation — which also closes the same standing gap for
  W1a-2.

#### B9. Class B attachment groups — **CLOSED 2026-08-29: not reproducible in the field**
- **Closed on the maintainer's observation:** running 0.24.0 across several machines over an extended
  period with no double render seen.
- **This is field evidence, not a proof of impossibility — and it is stronger than plain absence.**
  The 41 Class B groups (82 rows with *distinct* `OriginalRowId`s for the same file) were measured
  as present in the real cache during B7. So the hazard population exists and is **not** rendering
  twice, rather than the population having gone away. That is what makes the closure honest.
- **What B7 actually fixed was Class A** — two rows sharing `(MessageId, OriginalRowId)`, caused by
  Apple rewriting the GUID mid-transfer. Class B rows are two genuine server rows for the same file;
  collapsing them client-side would fight the server and is still the wrong fix.
- [ ] **If photos ever double again, start here**, and check Class B *before* touching dedupe: group
  `Attachments` by `(MessageId, TransferName)` having distinct `OriginalRowId`s, and confirm whether
  the duplicate pair is one of those groups. If it is, the answer is server-side, not another
  client-side identity rule.
- **Still unexplained, kept because it was never accounted for:** Class A began abruptly at
  `OriginalRowId` 9022 / 2026-06-08 in a cache reaching back to 2025-08-02. Something changed then
  and it was not this codebase.

#### B10. Chat details pane races the persistence path on participant changes
- **Upgraded from "reads stale in-memory chats" after tracing it properly, 2026-08-29. It is a
  RACE, not a one-beat lag**, and that changes the fix.
- **Measured fan-out:** one `_actionHandler.ChatUpdated` event feeds **two independent async paths**.
  `IncomingMessageProcessor.cs:44-48` writes the event to a **channel drained on a background task**,
  which eventually calls `ChatsService.ApplyChatUpdateAsync` — and that only refreshes the in-memory
  list at its very **last** line (`ChatsService.cs:289`, `await LoadChatsAsync()`). Meanwhile
  `ChatDetailsViewModel.cs:327-337` enqueues straight onto the UI dispatcher and refreshes
  immediately. Nothing orders the two.
- **`RefreshParticipantsAsync` (`ChatDetailsViewModel.cs:301-318`) reads `_chatsService.Chats`**, so
  with an open details pane it will usually render the *pre-update* participant set.
- **Why the 2026-08-29 rename test passed anyway:** the display name is applied straight from the
  payload in the view model's own `ApplyChatUpdateAsync(kind, chat)`. Only the participant list goes
  through the stale in-memory read. **Renames look fine; add/remove participant is the broken case.**
- [ ] Fix direction (head engineer, open to argument): prefer the payload's own participants — the
  same source the name already uses — with the in-memory list only as a fallback. Note
  `ChatsService.ResolveParticipantsAsync` (`:292-310`) documents that the payload's participants are
  preferred but **can be absent**, returning an empty list on failure, so the fallback is load-bearing
  and must not blank a populated pane.
- Alternative worth measuring against it: give the pane an ordering signal so it refreshes *after*
  persistence. Cleaner, but `ApplyChatUpdateAsync` currently raises no event at all, so it means
  adding one — larger blast radius.
- Related, already handled: W1b left a null guard inside `RemoveParticipantsMissingFrom` and W1c
  restructured the stale-set computation. Neither touches this read.

#### B2g. Images decoded oldest-first — **CLOSED 2026-08-29: fix measured working**
- [x] `TriggerAutoDownload` removed from `BuildMessageList`; the download now starts when the
  container is realized (`AttachmentHolder`, `NotDownloaded` case), so it follows what is on or near
  screen instead of walking the loaded window oldest-first. The new-message append path still
  triggers directly. `bbd899c`, merged `5d3a7d1`.
- **Measured before the fix:** `BuildMessageList` fired 19 auto-downloads within 20 ms in strictly
  ascending `DateCreated` order, and decodes then arrived in download-completion order — so the
  on-screen newest images sat behind the whole page.
- [x] **Measured after the fix, 2026-08-29, on a genuinely cold cache** (the maintainer's 456-file
  attachment folder was moved aside, then restored). B2b's instrumentation supplied the numbers:
  - **Downloads now follow the viewport, not the page.** #1-#3 arrived seconds apart as containers
    realized (14:01:06 / :08 / :09); a later burst of 7 landed inside 130 ms at 14:07:18 when a
    scroll brought them on screen at once. That is container-driven, structurally unlike the old
    single 19-in-20 ms page-order burst.
  - **`thread.open->first-image`: n=3, median 698.5 ms, max 934.6 ms.** The number that did not
    exist before this week.
  - 53 auto-downloads, `attach.download` n=35 (median 77 ms, max 475 ms), `attach.decode.image`
    n=43 (median 158 ms).
- **Not exhaustively checked:** individual GUIDs in the 130 ms burst were not mapped back to their
  `DateCreated`, so "ordering within a burst" is inferred from the trigger mechanism rather than
  measured. Good enough to close; noted rather than glossed.
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
  control's dependency properties. Worth finding out *what* is republishing tile properties while
  idle — the answer may not be about avatars at all.
- [x] **Now answerable — B2b (`6dd6534`) added trigger attribution.** `QueueRelayout` accumulates the
  dependency-property names behind each coalesced relayout and the rollup buckets them as
  `avatar.relayout.by:<names>`, with anything unattributed labelled as such.
- [ ] **Did not reproduce during B2b's capture:** 54 relayouts, all attributed, none unattributed,
  none during idle. The machinery works; the churn needs a longer or different window. Reproduce
  first, then read the attribution bucket — do not theorise ahead of a capture.
- [ ] **Did not reproduce a SECOND time, 2026-08-29 (maintainer ran a deliberate idle window).** Last
  relayout 14:09:25, app closed 14:11:13 — **108 seconds of true idle with zero relayouts**. The 31
  relayouts logged at 14:09 are accounted for by the group-rename test immediately before, which is
  supposed to refresh tiles. Session total 85, `by:Loaded` 21.
- **Two clean non-reproductions now.** Treat the original `gen=13` observation as unexplained rather
  than ongoing, and **do not spend more time hunting it without a fresh sighting.** If it recurs, the
  attribution bucket names the culprit immediately.

#### B2b. Draw timing for verbose logging — **DONE** (`f650eb2` + `6e07472`, merged `6dd6534`)
- **Reframed by the maintainer 2026-08-29 and the evidence backed it:** every UI defect this project
  has settled was settled by log counts, not tests — B2f (10 decode starts / 5 stranded -> 4 / 0),
  B2h (discarded decode work 50% -> 21%), B2g (19 auto-downloads inside 20 ms). The missing
  capability was *measurement*, not a test host.
- **Two gaps measured before building, both confirmed by the agent:** `Stopwatch|ElapsedMilliseconds`
  had **zero matches across the whole solution** — every before/after ever produced here was a
  *count*, never a duration — and the image path (`ImageLoader`, `AttachmentHolder`, `ChatBubble`,
  `AttachmentViewModel`) had **zero `AppLog` calls**, dark since B2e deleted `[attach-diag]`.
- [x] Shipped: `BlueBubbles.Core/Diagnostics/` — `DurationSeries` (count/total/min/max/percentiles),
  `PerfSession`, `PerfSummaryFormatter`, `PerfStats` facade. All maths in **Core** so it is reachable
  by the suite (the F5 pattern); the view layer only supplies samples. Call sites in
  `AttachmentHolder`, `AvatarControl`, `ChatViewModel`, `AttachmentViewModel`. Dumped from
  Settings > About and automatically on window close. 590/590 (573 + 17).
- **Free when off, which was the non-negotiable.** `PerfStats.Timestamp()` returns a raw `long` 0
  sentinel rather than allocating a `Stopwatch`; every recorder returns early. Verified by me:
  removing the `!IsEnabled` guard from `Count` fails `Facade_IsInertWhileVerboseLoggingIsOff`
  (589/590).
- **B2h's fix survived the refactor** — checked, because `AvatarControl` is where it lives:
  `OnLoaded` still calls `QueueRelayout`, not `RefreshLayout` directly. The split into a timing
  wrapper + `RefreshLayoutCore` is behaviour-neutral, and with logging off `NoteTrigger` returns
  early so no trigger string is ever built. Whole diff is 716 insertions / **6 deletions**.
- **A coverage hole I found and closed (`9963d59`).** My own mutation — deleting `Array.Sort` from
  `Percentile` — **survived at 590/590**. Cause was a too-kind fixture, not a code bug: the median
  test used `{90,10,50,70,30}`, which puts **50 at both insertion index 2 and sorted index 2**, so
  the assertion passed either way *while carrying a comment claiming it pinned the sort*. Changed to
  `{90,10,30,70,50}`; the same mutation now fails `DurationSeries_MedianOfOddCountIsTheMiddleSample`.
  **A test whose comment claims coverage it does not have is worse than no test** — it stops anyone
  looking.
- [ ] Human-only: the Settings > About "Perf Summary" button has never been clicked. The on-close
  dump exercises the identical `PerfStats.Dump()` path and is proven.
- Out of scope, still open: this does **not** make the `AffectsConversationList` subscriber link
  testable. W1a-2 and B8 both depend on that one line and it remains compilation-and-review only.

#### B2n. Attachment downloads failed silently — **CLOSED 2026-08-29: logging gap, not a download bug**
- [x] **Fixed the actual defect** (`8ae0c85`, merged `7cff53d`): `AttachmentViewModel` caught the
  exception, counted it, set an error state and **never logged**. Now logs the exception type and
  raw message at **Warn** (not behind verbose) — `Describe()` rewrites the text for the user and
  would have hidden the cause.
- **Cause measured on a second cold-cache run, 2026-08-29 — all 8 failures identical:**
  `HttpRequestException: Response status code does not indicate success: 500`, 8 distinct
  attachments. That is the documented iCloud-purged case (the record exists, the file is not on the
  Mac).
- **The app already handles it correctly, end to end** — verified in source rather than assumed:
  `Describe()` matches the 500 specifically and says *"This attachment isn't on the Mac yet. Retry to
  pull it down from iCloud."*; `AttachmentHolder.xaml:133` shows a **Retry** button; `OnRetryClick`
  -> `RetryAsync()` -> `DownloadInternalAsync(force: true)` -> `ForceDownloadAttachmentAsync`, which
  is the server's force endpoint that makes the Mac fetch from iCloud first.
- **So this was never a broken feature — only an invisible one.** Recorded as a negative result
  rather than turned into work.
- **Rate tracks scroll depth**, not health: 19 of 53 (36%) on the first run, **8 of 65 (12%)** on the
  second. Older photos are likelier to have been offloaded to iCloud.
- [ ] **Open product decision (maintainer's call): should auto-download force automatically on a
  500?** Head-engineer recommendation is **no** — force makes the Mac pull from iCloud, and
  auto-download fires for anything scrolled past, so it would haul down photos the user never looked
  at. The current design puts that cost behind a deliberate click. Middle ground if it becomes
  annoying: auto-force only when the user opens an attachment full-screen.

#### B2m. Avatar decodes are far slower than anyone assumed — newly measurable
- [ ] **First real numbers, from B2b's rollup on a normal launch:** `avatar.decode` n=14,
  **total 6530 ms**, median 233.8 ms, **p95 1047.7 ms, max 1077.9 ms**. For cached contact photos
  that is much larger than expected, and nobody has ever had this number before.
- [ ] Also measured: `avatar.relayout.by:Loaded` = **25 of 54** relayouts. Nearly half are triggered
  by container realization with **no property change at all** — a coalescing opportunity the current
  one-tick window does not catch.
- `avatar.decode.stale` = 4, which **matches B2j's "four discarded decodes per launch" exactly**,
  now measured rather than hand-counted — a good cross-check that the instrumentation agrees with
  the earlier manual analysis.
- [x] **Second capture, 2026-08-29, different session and a cold attachment cache — it holds.**
  n=14, **total 6096 ms**, median 301.0 ms, p95 973.2 ms, max 982.0 ms. Two independent sessions
  agree within ~7% on the total, so this is a real, repeatable cost, not a one-off. `stale` was 3
  this time (4 previously).
- [ ] **THIRD capture disagrees by 35% — hold this entry.** 2026-08-29 later run, same build, cold
  cache: n=13, **total 3939 ms**, median 131.4 ms, p95 691.1 ms. So the series is 6530 / 6096 /
  **3939**. Two agreeing captures looked solid and were not — **do not optimise against this until
  there is a repeatable measurement.** Find what varies first (which chats are visible, how many
  distinct contacts, warm vs cold OS file cache).
- **This may still be the largest single measured cost at startup** — even the low reading is 3.9 s
  of decode work against `thread.open->messages-built` at ~10 ms median. Worth chasing; not worth
  guessing at.
- `avatar.relayout.by:Loaded` was 21 of 85 this session (25%) vs 25 of 54 (46%) previously — the
  ratio moves with usage, the pattern does not.
#### B2c. `GetMessagesByGuidsAsync` omitted `.Include(m => m.Attachments)` — **FIXED** (`93713a9`, merged `a84354f`)
- [x] One line, plus `GetMessagesByGuids_IncludesAttachments`. 573/573. Negative control run:
  removing the `.Include` again fails that test and only that test (572/573).
- **Confirmed latent, not live — checked rather than assumed.** Both production callers are in
  `ChatViewModel` (`:1053` reconcile/soft-delete sweep, `:1320` reply-context backfill). The reply
  path feeds `EntityPreview`, which reads the **`HasAttachments` scalar column**, not the navigation
  collection — so nothing was reading zero attachments. The trap was that this query returned
  entities *shaped like* `LoadMessagesAsync`'s (which includes at `:37-38`, as does
  `LoadMessagesAfterAsync` at `:66-67`) while silently carrying an empty collection.
  `LoadReactionsAsync` (`:348`) omits it correctly — reactions have no attachments.

#### B2d. `ChatBubble.Unloaded` nulls `_currentVm` but not `_renderedContentForVm` — **CLOSED: not a bug, and "fixing" it would cause one**
- **Traced properly 2026-08-29 instead of being carried forward on low confidence.**
  `_renderedContentForVm` guards "build attachments / URL preview **once per message VM**"
  (`ChatBubble.xaml.cs:165-169`). `Unloaded` (`:62-72`) nulls `_currentVm` and unsubscribes, but
  **does not destroy the visual tree** — the built content is still there.
- **Recycled to a DIFFERENT vm:** `DataContextChanged` sets `_currentVm = newVm`, and
  `_renderedContentForVm != newVm`, so `BuildContent` runs. Correct.
- **Re-bound to the SAME vm:** `_renderedContentForVm == vm`, so `BuildContent` is skipped — and
  that is exactly right, because the content was never torn down. **Nulling the field on unload
  would force a rebuild and re-load the image, reintroducing the "appear, disappear, appear"
  flicker the guard was added to prevent** (see the comment at `:162-164`).
- The only path that must clear it — `DataContext` becoming a non-VM — already clears **both**
  fields together (`:58-59`).
- **Do not re-open without a constructed misrender case.** The two fields are deliberately not
  symmetric.

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
- [x] **W1a-2. Single announcer for messages — DONE** (`724e548` + `90ba5d9`, merged `891224a`).
  570/570. **The entry's premise — that this could not be done without a behaviour change — was
  wrong, and the reason is instructive.** The blocker was never the announcement; it was that
  `MessagesPersisted` carried a chat GUID and nothing else, so a backfill of year-old history and a
  brand-new message were indistinguishable and every subscriber had to assume the worst. Giving the
  event a *kind* (`MessagesPersistedEventArgs`, `MessagePersistKind { NewOrUpdated, ServerTrueUp }`,
  with `AffectsConversationList` as the single definition of "this can change a tile") let **every**
  write path announce with no visible behaviour change. 9 announce sites now: 4 `NewOrUpdated`,
  5 `ServerTrueUp`.
  - **"Raise inside `MessagePersistenceHelper`" was refuted on evidence, and rightly.** The helper
    is `internal static` with no DI and takes an `int chatId`, not a GUID, so announcing from it
    needs an extra query per persist purely to feed an event; and three of its entry points
    (`MarkDeleted`, `MarkParentHasReactionsAsync`, `SaveAttachmentsAsync`) have **no commit
    boundary** — the caller saves afterwards — so an event raised there hands a subscriber the
    *pre-change* state. What shipped instead: the **consequence** is decided in exactly one place,
    while the **raise** stays with whichever component owns the committed transaction.
  - Also rejected, correctly: folding `IncomingMessageProcessor.cs:113` into
    `SaveIncomingMessageAsync` would fire before `HandleNewMessageAsync` updates the chat's preview
    row, so the list would reload against stale chat state. A real ordering regression avoided.
  - **Enumeration correction — the fourth in this program.** There were **5** silent persist paths,
    not 4: `MessageWindowReconciler` is reached from two callers (`MessagesService.cs:224` and
    `SyncService.cs:720` via `:172`). Verified by me.
  - Verified by me, not accepted from the report: no DI cycle (`ChatsService` takes only
    dbFactory/api/settings, so `MessagesService -> IChatsService` is one-way); the characterization
    commit `724e548` is green on unmodified production code at **563/563**, and those same tests
    still pass at 570 — that is the behaviour-preservation proof; and mutating
    `AffectsConversationList` to `false` (the B1 regression shape, opposite to the agent's own
    mutation) fails `OnlyNewOrUpdated_AffectsTheConversationList`. One test pins both directions.
  - [x] **Human-verified, 2026-08-29:** the conversation list and open thread both update correctly
    in real use — a live inbound message, a delete, a group rename and typing indicators were all
    exercised in one session. Every message write flows through this event, so this was the standing
    risk; it is now closed by observation.
  - Gotcha worth keeping: the agent reverted a mutation with `git checkout -- <file>` and lost the
    **entire uncommitted refactor** of that file, because the production commit had not been made
    yet. Caught by reading the file back. **Commit before mutating.**
- [x] **W1b. HandleEntity and ChatParticipant — DONE** (`e14695b` + `399c0b8`, merged `60dbbaa`).
  One writer each, in `BlueBubbles.Core/Services/HandlePersistenceHelper.cs`: `ApplyServerFields`
  is the single field list, with `EnsureHandleAsync`, `LinkParticipantAsync`,
  `LinkParticipantsAsync` and `RemoveParticipantsMissingFrom` beside it. 507/507, clean build and
  launch. Verified by me: the only remaining constructions are inside the helper, plus one
  **transient** `HandleEntity` in `ChatViewModel.cs:888` that is never added to a context.
  - **Both audit enumerations were over-counted — the third time this has happened.** Actual: **5**
    persisting HandleEntity writers (6 constructions, one transient) and **5** ChatParticipant
    writers plus 1 removal site. W1a was wrong the other way (5 stated, 6 actual). **Stop quoting
    audit counts as settled in briefs.**
  - **The justification, measured:** the three surviving field lists wrote **2, 4 and 9 of 9**
    columns. Whether the cache held a contact's `Country`/`Color`/`DefaultEmail` depended on which
    path saw them first. That is B2's shape already present, not hypothetical.
  - **No client-owned fields here** — verified column by column: every scalar on `HandleEntity` maps
    1:1 to a `Handle` property, and `ChatParticipant` is a pure `(ChatId, HandleId)` join. So no
    `ChatFieldMerge`-style ownership split. What *is* protected is the no-clobber rule, via
    `refreshExisting`, which only the sync paths pass.
  - Verified by me, not accepted from the report: mutating `if (isNew || refreshExisting)` to
    `if (true)` fails `SparsePayloadAfterFullSync_DoesNotBlankStoredHandleMetadata` (506/507) — the
    B6-shape hazard is defended by a named test. Removing the `.Local` dedup fails
    `PayloadNamingTheSameParticipantTwice_LinksItOnce`. Restoring Core to pre-refactor `9f3636f`
    reproduces the agent's control exactly: **6 pass, 2 fail**, the two failures being precisely the
    two declared deltas.
  - **Two intentional behaviour deltas:** creating writers now store the full field set (strict
    superset, create-only); and `LinkParticipantAsync` de-duplicates against `DbSet.Local`, which
    fixed a **live defect** — a payload naming the same participant twice threw
    `InvalidOperationException` on a composite-key conflict.
  - [ ] Not verified: no live-server run, so the real participant-removal path is unexercised.
  - Carried into W1c: `ChatsService.ApplyChatUpdateAsync` computes stale participants from
    `entity.ChatParticipants` including rows added moments earlier in the same call, relying on EF
    fix-up to populate `Handle`. A null guard was added inside `RemoveParticipantsMissingFrom`
    rather than restructuring the flow. **W1c owns that flow — fix it there.**
- [x] **W1c. ChatEntity — DONE** (`0e6a31c` + `033a67a`, merged `0a247d0`). One writer, in
  `ChatFieldMerge.cs`, split into two named entry points rather than one flag-driven method:
  `InsertFromServer` (server record in hand, no client opinion) and `InsertForLiveMessage` (a
  message just arrived, so the client owns unread and delete state). 562/562.
  - **Constructions: 4, not 13** — `ChatsService.cs:195/:237`, `SyncService.cs:108/:525`. The
    audit's 13 counted *field-assignment* sites, a different and less useful unit for a
    single-writer refactor. Verified by me: the only remaining `new ChatEntity` in production are
    the two inside `ChatFieldMerge` itself.
  - **The defect, confirmed and now closed:** `ApplyServerOwnedFields` writes 11 fields; the two
    bypasses wrote 5. Chats created on those paths landed without `AutoSendReadReceipts`,
    `AutoSendTypingIndicators`, `DateDeleted`, `LockChatName`, `LockChatIcon` and
    `LastReadMessageGuid`. B2's shape. Characterization control reproduced by me against
    `e589504`: **560 pass / 2 fail**, the failures being exactly the two bypass paths.
  - **The B6 trap was not flattened.** Both bypasses hardcoded `HasUnreadMessage = true` — a
    *client* decision — while the merge deliberately treats a missing `hasUnreadMessage` as "no
    opinion" (`if (server.HasUnreadMessage is { } hasUnread)`), which **is** the B6 fix. Routing the
    inserts naively through the merge would have left the column at its `false` default and stopped
    new chats showing as unread. `InsertForLiveMessage` applies it after the merge instead.
    Verified by me: deleting that line fails 4 tests, named
    `LiveMessageChatCreate_IsUnread_EvenWhenThePayloadIsSilent(payloadUnread: null/False)` and the
    delta equivalent. Adding a client-only field to the merge fails 3 pre-existing tests.
  - **Resurrect question answered — no hazard.** Zero `HasQueryFilter` matches in the codebase, so
    the `Guid` lookups already see soft-deleted rows, and `Chat.Guid` is uniquely indexed
    (`BlueBubblesDbContext.cs:40`). The insert branch cannot run against a live row holding
    client-only state. **This closes the audit's open question — do not re-open it.**
  - **A report claim I checked and had to reject:** the agent reported that the `SyncService`
    bypass also seeded `OldestSyncedMessageDate`, and recommended recording "insert-time client-only
    field initialisation" as a category. **It does not.** `git show e589504:...SyncService.cs` shows
    that insert setting exactly Guid, ChatIdentifier, DisplayName, Service, Style and
    HasUnreadMessage — the same six as the `ChatsService` one. The two bypasses *were* structurally
    identical. `OldestSyncedMessageDate` is written elsewhere (`SyncService.cs:182`,
    `MessagesService.cs`), never at insert. The shipped code is correct and matches prior behaviour;
    only the report was wrong. **No such category exists — do not create one.**
  - [x] **Participant staleness fixed properly** (handed over from W1b): `ApplyChatUpdateAsync` now
    computes the stale set from the server participant list *before* any rows are added, so it no
    longer depends on EF fix-up having populated `Handle` on rows created in the same call. W1b's
    null guard remains as cheap defence but is no longer load-bearing.
  - [ ] Not verified: visual confirmation that a newly created chat renders with an unread badge.
    Proven at the data layer only.
- Note from W1a for W1b/W1c: `MessageWindowReconciler` is now *policy* over the writer (which rows
  are gone), not a field writer. The same split should fall out for chats.
- [ ] Expected side effect: the locking exposure largely dissolves. "Which of three writers needs the
  mutex" is hard; "my one writer takes it" is not. Do **not** attack the 34-of-42 figure directly
  first.
- [ ] Not measured: whether the unlocked saves actually collide at runtime. SQLite's default
  rollback-journal locking may have been absorbing it. Worth instrumenting, not worth assuming.

#### W2. Transport leakage cleanup — **DONE** (`f004462`, rebased `d4792ac`, merged)
- [x] All 7 named targets cleared. New transport-neutral abstractions live in `BlueBubbles.Core`:
  `Models/ConnectionStatus.cs` (`ConnectionState`, `ConnectionBanner`, `ConnectionStatusPolicy`),
  `Models/ChatUpdateKind.cs`, `Models/TypingState.cs`, `Services/ITypingIndicatorService.cs` (which
  now owns the `started-typing`/`stopped-typing` names). 548/548 = 507 + 41 new tests.
- **Core-not-Windows was the right call, and for a better reason than mine.** I framed it as
  "Core gets test coverage". The agent's reason is stronger and already in repo memory: the test
  project targets `net8.0` and references only Core, so a view-layer type is not *less* tested, it
  is **unreachable** — and every abstraction added here carries branching logic (a 4-arm state map,
  a banner policy with a syncing override, an event-name classifier, a status-code boundary).
- **Measured leakage: 48 refs / 12 files -> 30 / 7.** Verified by me on the branch: exactly 30/7.
  Of the 30 remaining, **16 are the deliberate exclusions** (`App.xaml.cs` DI 9, `SetupViewModel` 4,
  `ServerManagementSettingsPage` 3). **The audit's "43 across 10 files" is not reproducible from
  source** without knowing its exact pattern — do not cite it as a baseline again.
- Verified by me, not accepted from the report: mutating `IsParticipantChange` to drop
  `ParticipantLeft` fails `IsParticipantChange_OnlyForMembershipEvents(kind: ParticipantLeft)`
  (547/548). Confirmed `ChatsService.cs`/`SyncService.cs`/`MessagesService.cs` are untouched, so
  there was no W1b collision. Clean build 0 warnings / 0 errors and the exe launches and stays up
  with `.pri` staged.
- **Two brief corrections from the agent, both accepted:** the `SocketState` banner cluster is
  **4 files, not 3** (`SettingsViewModel.cs` declared `[ObservableProperty] SocketState
  ConnectionState`, so the page could not stop naming the type until the VM changed), and the
  `_socketService is ObservableObject` downcast existed in **two** view models, not one.
- `SettingsViewModel` went 4 -> 6 references: it absorbed the API dependency out of
  `AboutSettingsPage` code-behind. That is the trade this entry asked for.
- [x] **Human-verified by the maintainer, 2026-08-29:** typing indicators transmit (seen on the
  phone while typing on the PC), a group rename from the phone reaches the details pane and the
  chat list, and the app connects and renders normally throughout. The connection banner's
  disconnected/syncing states were not deliberately forced, so those specific visuals remain
  unexercised — the `Connected` path is confirmed.
- [ ] **Still deferred, unchanged:** wire DTOs used directly as the domain model — every
  `Core/Models` type except `SocketEvents.cs` carries `[JsonPropertyName]` and the ViewModels bind
  against `Message`/`Chat`/`Handle`/`Attachment`. That is the real portability debt.
- [ ] Adjacent, left alone: `BackupSettingsPage` and `AboutSettingsPage` still resolve services from
  the container in their constructors; `NewChatViewModel` and `SetupViewModel` still hold
  `ISocketService` directly.

#### W3. Dead transport scaffolding — **DONE, deleted** (`bc30609`, merged `6e58ca1`)
- **Decision (maintainer, 2026-08-29): FaceTime and Find My are not planned.** Dead, not staged.
  Re-adding is cheap — thin wrappers over documented server endpoints, with the Flutter client as
  the protocol reference.
- [x] Deleted: `Models/FindMyDevice.cs`, `Models/FindMyFriend.cs`, `IFaceTimeService.cs`,
  `IFindMyService.cs`; 3 `SocketEvents` constants; the `SocketService` registrations and the
  `_socket.On` block; the `ActionHandler`/`IActionHandler` events and case arms; 7 API methods on
  `IBlueBubblesApiService`/`BlueBubblesApiService`; and the stub members in two test files.
  **247 deletions / 2 insertions** — the only additions are two stale comment headers
  (`// -- iCloud / FindMy (7) --` -> `// -- iCloud (3) --`) that would otherwise have survived the
  zero-reference check. Verified by me post-rebase: **0 hits** across all three projects, all four
  files gone, W1c's files untouched, file encoding clean.
- [x] **Test count 562 -> 560**, accounted for exactly by the two deliberately deleted tests:
  `ActionHandlerTests.FtCallStatusChanged_FiresEvent` and
  `ActionHandlerTests.AliasesRemoved_FiresEventWithList`.
- **MY EARLIER TEXT HERE WAS WRONG — corrected.** This entry previously claimed
  `IMessageAliasesRemoved` was "a constant with no handler at all". It had a **full chain**:
  `ActionHandler.cs:74` case arm -> `HandleAliasesRemoved` (`:218`) -> a public `AliasesRemoved`
  event (`:25`, `IActionHandler.cs:16`) -> a second live test at `ActionHandlerTests.cs:196`. My
  grep missed it because the handler uses the C# identifier `IMessageAliasesRemoved`, which does not
  contain the hyphens of the wire name I searched for. **Lesson: when hunting a socket event, grep
  the constant identifier as well as the wire string.**
- **Scope decision (head engineer, 2026-08-29): the aliases chain went too, and this is the one
  judgement call worth flagging.** `imessage-aliases-removed` is *not* FaceTime or Find My, so it
  sits outside the literal wording of the maintainer's decision. It was removed because it is dead
  by the same standard — **zero production subscribers**, verified; its only consumer was its own
  test — and because this entry has scoped all three events together since it was filed. **If the
  alias-removal notification is wanted later it is a small revert**, and the server still emits the
  event. Say so and it comes back.
- Confirmed: no DI registration referenced either interface, and no implementation of either existed.
- Process note from the agent, worth keeping: `build-and-run.ps1`'s `dotnet run` step can return
  exit 0 without the app staying up, which reads identically to a crash from the script's output.
  Distinguish them with a log delta (`Session start` + zero `[ERROR]`/`[FATAL]`) or `HasExited`,
  not the exit code. Same trap as the "Launch failed." note in repo memory.

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
