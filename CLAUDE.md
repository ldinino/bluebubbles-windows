# CLAUDE.md

Project context for Claude Code. This file is **tracked in git** and auto-loaded on every
machine (desktop, web, phone), so it is the canonical, portable source of truth for how to work
in this repo. (Local `~/.claude` auto-memory does **not** travel — durable facts belong here.)

## What this is

A first-class, native **WinUI 3 (.NET 8, C#)** iMessage client for Windows that ports the
BlueBubbles Flutter app. It talks to a **BlueBubbles macOS server** over **REST + Socket.IO**
(the server is what bridges to iMessage; this app never talks to Apple directly).

Authoritative tech facts live in `BlueBubbles.Windows/BlueBubbles.Windows.csproj` — read them
there, don't hardcode values that drift: TFM `net8.0-windows10.0.26100.0`, min Windows
10.0.19041, `<Platforms>x86;x64;ARM64</Platforms>`, and the single `<Version>` (currently
`0.20.1`).

## Architecture

Two-project split keeps protocol/logic independent of UI:

- **`BlueBubbles.Core`** — protocol, services (REST API, Socket.IO, sync, contacts,
  notifications), models, EF Core (SQLite) local cache. **No UI references.**
- **`BlueBubbles.Windows`** — the WinUI 3 app: views, view models (CommunityToolkit.Mvvm),
  reusable controls. Custom `Program.cs` entry point for single-instancing.
- **`BlueBubbles.Windows.Tests`** — xUnit suite.

Dependency flow is one-directional: **Views -> ViewModels -> Services -> Models**. Socket
events are marshaled onto the UI thread.

## Hard rules (load-bearing constraints)

- **Ships fully unpackaged** — no MSIX, no `Package.appxmanifest`, no code-signing cert. It's a
  self-contained publish wrapped by an Inno Setup installer. **Never** reintroduce MSIX
  scaffolding or **package-identity-only APIs** (`Package.Current`, `PasswordVault`,
  `Windows.ApplicationModel.StartupTask`, `Windows.Storage.ApplicationData.Current`) — they
  throw when running unpackaged. Use the established identity-free equivalents already in the
  codebase: DPAPI (`CredentialService`), `HKCU\...\Run` registry (`StartupTaskService`),
  file-based JSON (`SettingsService`), assembly version (`AppInfo`),
  `Environment.GetFolderPath(LocalApplicationData)`. (History: `docs/msix-removal.md`.)
- **KEEP `<EnableMsixTooling>true</EnableMsixTooling>` in the app csproj.** Despite the name it
  is **not** MSIX rot — it activates the WinUI 3 targets that stage `resources.pri` into the
  *unpackaged* publish output. Removing it ships a publish folder with no
  `BlueBubbles.Windows.pri`, and the installed app crashes instantly on launch
  (`0xc000027b`). A green build does **not** prove launch — actually run the exe when touching
  build/packaging config.
- **Private API is the only API.** Every outgoing message sends `method: "private-api"`
  unconditionally. Never fall back to `"apple-script"`, never reintroduce the six Private-API
  enable booleans that were collapsed in Phase 6.5. The server's `private_api` flag is stored
  as `ServerPrivateAPI` for capability warnings only, not for method selection. Preferences like
  `PrivateSendTypingIndicators` / `PrivateMarkChatAsRead` control *behavior*, not *method*.
- **Flutter source is a protocol reference only.** The Flutter app (in this repo) tells you
  *what* the server expects — endpoint paths, JSON field names, socket events, auth. Copy that
  faithfully. Do **not** copy its client architecture, tangled settings, conditional logic, or
  UX; build the WinUI 3 side from scratch with proper .NET patterns. When Flutter has complexity,
  ask whether it's protocol-required or just client baggage (usually baggage).
- **Async image loads in recycled WinUI containers need a generation counter.** Any
  `BitmapImage.SetSourceAsync` inside a control a `ListView` can recycle must clear the source
  (`= null`) and bump a generation counter before the await, then check it after — otherwise a
  stale decode overwrites the correct avatar/image.
- **Publishing must wipe `obj`/`bin` first.** The WinUI 3 XAML compiler's incremental build can
  silently keep stale `.xbf`, so a UI fix can "not ship" even though it's committed. The shared
  clean lives in `build-common.ps1` (`Clear-BuildOutputs`). Keep all `.ps1` files **pure-ASCII**
  (no em-dashes/smart quotes) — Windows PowerShell 5.1 misparses no-BOM UTF-8.

## Versioning & release

Single source of truth: the `<Version>` property in `BlueBubbles.Windows.csproj` (3-part
`Major.Minor.Patch`). It flows automatically into the assembly FileVersion/AssemblyVersion and
the About page. Bump **only** that one value:

- **Patch** (+1 third octave): bug fix / debugging an existing problem.
- **Minor** (+1 second octave): a small new feature.
- **Major** (first octave): do **not** touch without explicit permission.

Cut releases via the manual `release.yml` GitHub Actions workflow (`workflow_dispatch`); ship
the **CI** build, not a local `publish.ps1` build. **x64 only** — arm64 is blocked on the
vendored x64 `Microsoft.WindowsAppRuntime.Insights.Resource.dll`.

## Build / run / test

From the repo root (PowerShell):

```powershell
./build-and-run.ps1            # full clean build + tests + launch (unpackaged); matches publish.ps1
./build-and-run.ps1 -Fast      # incremental C#-only build (NOT safe after XAML edits)
./publish.ps1                  # self-contained publish + Inno Setup installer -> dist\
dotnet test .\BlueBubbles.Windows.Tests\BlueBubbles.Windows.Tests.csproj -c Debug
```

`build-and-run.ps1` and `publish.ps1` share clean/build logic via `build-common.ps1`, so a local
debug build and the shipped installer come from the same fresh tree. The app runs as a plain
process with no package identity — toast activation and single-instancing are handled at runtime
(`AppNotificationManager.Register()` + `AppInstance`), not via a manifest.

## Notification activation gotcha

Because the app is unpackaged, a toast click launches a fresh process that **redirects** the
activation to the running primary via `AppInstance.RedirectActivationToAsync`. Two non-obvious
requirements:

1. The `AppInstance` from `FindOrRegisterForKey` must be kept alive in a **`static`** field
   (`Program.cs` `_keyInstance`) or its `Activated` subscription is GC'd and toast clicks die
   silently.
2. Deep-links must go through `ShellPage.OpenChat` -> `ConversationListPage.SelectChatByGuid`.
   Setting `ConversationListViewModel.SelectedConversation` does **not** open a thread.

## Where the deeper docs live

- `docs/BlueBubbles-WinUI3-Design-Spec.md` — the **binding reference** for architecture, UI, and
  behavior.
- `docs/PLAN.md` — phase-by-phase implementation record (Philosophy, Distribution, and the
  "Critical Flutter Source Files" table mapping each concern to its Dart source).
- `docs/PUNCHLIST.md` — the live TODO (open bugs, backlog, release plan).
- `docs/msix-removal.md` — why MSIX was removed and what's load-bearing.
- `BlueBubbles.Windows/.github/instructions/*.instructions.md` — detailed WinUI 3 / security /
  accessibility / performance / code-quality rules.
- `README.md` / `INSTALL.md` — user-facing overview and installer docs.
