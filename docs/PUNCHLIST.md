# Punchlist

> Completed work lives in `docs/PUNCHLIST-ARCHIVE.md` — moved out to keep this file focused on
> what's left.

---

## Open bugs

*(none)*

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

#### F5. FaceTime calls — non-modal toast + breakout call window
Research done (server + Flutter reference read); **not started**. Do not begin until the open
bug list is clear.

**Reality check first (read before designing anything):**
- This is **FaceTime only** — audio or video. The server has no cellular/SMS-call bridge, so
  "phone calls" means FaceTime Audio.
- **We never carry the media.** `POST /facetime/answer/{uuid}` makes the *Mac* answer, generates
  a **FaceTime web link** (`facetime.apple.com/join#...`), admits us, then the Mac leaves the
  call. The actual A/V session runs in a **browser** (WebRTC). Our "call window" is a
  companion/control surface, not a call client. Upstream just `launchUrl`s the link.
- **There is no decline endpoint.** The API is answer + leave, nothing else. Upstream's "Ignore"
  button is a purely local dismiss. So the desired behavior — X just silences and the call rings
  out on its own — is not a compromise, it's literally the only thing the API supports. Do not
  wire the X to `leave`; leaving an unanswered call is untested and could kill the ring.

**What the server actually exposes** (`bluebubbles-server`, all FaceTime routes sit behind
`PrivateApiMiddleware`, and `FaceTimeInterface` hard-requires **macOS Monterey+**):

| Method | HTTP | Path | Returns |
|--------|------|------|---------|
| Answer | POST | `/facetime/answer/{call_uuid}` | `{ data: { link } }` |
| Leave | POST | `/facetime/leave/{call_uuid}` | no data |
| New session (outgoing) | POST | `/facetime/session` | `{ data: { link } }` |
| Availability | GET | `/handle/availability/facetime?address=` | `{ data: { available } }` |

Socket events — **two mutually exclusive modes**, selected by the server-side `facetime_calling`
config toggle (labeled "Experimental" in the server UI):
- **Off (default / legacy):** `incoming-facetime` only. Payload is a **stringified JSON**
  `{ caller, timestamp }` — no call UUID, therefore **not answerable**. Notify-only.
- **On ("FaceTime Calling"):** `ft-call-status-changed`, a real object:
  `{ uuid, status_id, status, ended_error, ended_reason, address, handle, image_url,
  is_outgoing, is_audio, is_video, url? }`. Status IDs: `0` unknown, `1` answered, `3` outgoing,
  `4` incoming, `6` disconnected. The server suppresses `1`/`3` and de-dupes `4` per session, so
  in practice we receive **`4` (ring) and `6` (call ended)** — `6` is the signal to tear down the
  toast/window.

**What we already have** (do not rebuild):
- `SocketEvents.IncomingFacetime` / `FtCallStatusChanged` registered in
  [SocketService.cs](BlueBubbles.Core/Services/SocketService.cs#L104) — including the
  string-then-deserialize dance the legacy event requires.
- `ActionHandler.IncomingFaceTime` / `FaceTimeStatusChanged` events raised, but **nothing
  subscribes** — the events currently dead-end.
- `AnswerFaceTimeAsync` / `LeaveFaceTimeAsync` / `GetFaceTimeAvailabilityAsync` on
  [BlueBubblesApiService.cs](BlueBubbles.Core/Services/BlueBubblesApiService.cs#L619).
- `IFaceTimeService` exists as an **interface only** — no implementation, no DI registration.

**Proposed shape (keep it simple for v1):**
- [ ] `FaceTimeService` (Core) implementing `IFaceTimeService` — owns active-call state keyed by
      call UUID, subscribes to the two `ActionHandler` events, normalizes both payload shapes
      into one `FaceTimeCall` model, resolves the caller through `ContactResolverService`, and
      raises ring / ended. Register in DI.
- [ ] **Non-modal incoming toast** via the existing unpackaged `AppNotificationManager` path.
      Use `AppNotificationScenario.IncomingCall` so it persists and rings instead of
      auto-dismissing. Buttons: **Answer** / **Decline**; dismissing (X) is silence-only and
      sends nothing to the server. Green/red button styling needs `useButtonStyle="true"` on the
      root `<toast>`, which the builder does not expose — construct raw XML and pass it to
      `AppNotification(string)`.
      *Explicit non-goal: never block or steal focus from the main window. That is the whole
      point of this item.*
- [ ] **Toast activation routing.** Reuse the existing unpackaged redirect path, but add a
      **separate** argument namespace from chat deep links — the current route is
      `ShellPage.OpenChat` -> `ConversationListPage.SelectChatByGuid`, which is wrong for a call.
      Watch the `Program.cs` `static _keyInstance` gotcha (dropping the reference silently kills
      toast clicks).
- [ ] **Breakout call window** — a second WinUI 3 `Window` (the app is currently single-window;
      verify nothing assumes `MainWindow` is the only one, and that closing it doesn't tear down
      the app). Opens **immediately in a "connecting" state**: answering is slow server-side
      (waits for the answered event with a 30s timeout, +4s settle, then link generation), so
      call `AnswerFaceTimeAsync` with `LongTimeout` and never block the UI on it.
      v1 contents: caller name/avatar, audio-vs-video, elapsed timer, live status from
      `ft-call-status-changed`, **Open in browser** (the returned link), **Copy link**, and
      **Leave** (`POST /facetime/leave/{uuid}`). Auto-close on `status_id == 6`.
- [ ] **Capability gating + honest messaging.** Requires Private API, macOS Monterey+, *and* the
      server's `facetime_calling` toggle. If we only ever see legacy `incoming-facetime`, the
      call is not answerable — show a notify-only toast and say why, rather than an Answer button
      that fails. `ServerPrivateAPI` / server version are already stored for capability warnings.
- [ ] Settings: master on/off for call toasts, plus honoring Do Not Disturb / the existing
      notification policy.

**Decisions to make before coding:**
- **Browser hand-off vs. embedded WebView2 for the actual call.** Recommend **hand-off for v1**
  (launch the default browser). WebView2 would mean a new Evergreen-runtime dependency in the
  Inno Setup installer plus camera/mic permission prompts inside our process — real work, and it
  buys us nothing the browser doesn't already do.
- Outgoing calls (`POST /facetime/session` + `GET /handle/availability/facetime`) — worth a
  "FaceTime" button in conversation details, but treat as a follow-up, not part of v1.
- Ring sound: rely on the toast's looping call audio, or play our own via the existing
  `NotificationSoundResolver`?

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

**Future minor** — audio message support (F2), scheduled-send enhancements (F4), FaceTime calls
(F5), client updater (U1).

No version bump: repo hygiene (H2). Stretch goal (no schedule): arm64 (S1).
