# BlueBubbles for Windows

A native Windows client for [BlueBubbles](https://bluebubbles.app) — send and receive iMessages
from your PC by talking to a BlueBubbles server running on your Mac. Built with **WinUI 3** and
Fluent design to feel at home on Windows 11. No Flutter, no Dart.

> **Independent project** — not affiliated with Apple, Microsoft, or the BlueBubbles team. Free and
> non-commercial. See [Disclaimer](#disclaimer).

<!-- TODO: add screenshots here (drop PNGs in a docs/ or screenshots/ folder and reference them) -->

## What it is

BlueBubbles for Windows is a from-scratch C# / WinUI 3 client that speaks the BlueBubbles server
API (REST + Socket.IO). It connects to **your own** BlueBubbles macOS server and gives you a fast,
native Windows messaging experience that works with your existing server setup. This is only compatible with the Private API used in your BlueBubbles server. **Applescript methods have been deprecated.**

## Features

- **Real-time messaging** — texts and attachments, delivered live over Socket.IO.
- **iMessage features** via the BlueBubbles Private API: reactions (tapbacks), replies / threads,
  message edits, unsend, typing indicators, and read receipts.
- **Conversation list** — pinned chats, search, archive, and unread badges.
- **Group chats** — participants, group photo, rename, add / remove people, leave.
- **Rich content** — inline image / video / file attachments and link previews.
- **Local contacts** — import a vCard (`.vcf`) to resolve phone numbers and emails to names and
  photos, entirely on your PC (nothing is uploaded).
- **Native Windows touches** — custom title bar, light / dark / system theme, system-tray icon,
  single-instance, launch-at-startup, toast notifications, and session restore.
- **Flexible connection** — sign in with Google to auto-discover your server URL via Firebase
  (handles Cloudflare-tunnel URL rotation), or enter the URL + password manually; optional
  direct LAN / localhost connection for lower latency on the same network.
- **Customizable** — colorful avatars and bubbles, dense tiles, 24-hour time, avatar size,
  send delay, and more.

### Not yet implemented

Planned but not in the app yet (the data/service layer exists; the UI doesn't): FindMy map,
FaceTime answering, scheduled-message UI, audio-message recording, message / screen effects, and
stickers / Digital Touch.

## Requirements

- A running **[BlueBubbles server](https://bluebubbles.app)** on a Mac — this is a *client*; it needs
  your server to talk to.
- **Windows 10 (build 19041+)** or **Windows 11**.
- Your server's URL + password, or a Google account linked to your server's Firebase project for
  auto-discovery.

## Install

Download the latest **`BlueBubbles-Setup-<version>-x64.exe`** from the
[Releases page](https://github.com/ldinino/bluebubbles-windows/releases) (once a release is
published) and run it — use the **arm64** build on ARM PCs.

- Per-user install, **no admin prompt**, to `%LocalAppData%\Programs\BlueBubbles`.
- The build isn't code-signed, so Windows SmartScreen shows a one-time *"unrecognized app"* prompt —
  click **More info → Run anyway**. This is expected for unsigned open-source apps; see
  [INSTALL.md](INSTALL.md) for the details and how to remove it.

There is no Microsoft Store / MSIX version — just a plain, free installer.

## Build from source

Requires the **.NET 8 SDK** and the **Windows App SDK** components (Visual Studio 2022 with the
WinUI / Windows App SDK workload, or the matching `dotnet` workloads).

```powershell
# from the repo root

# build, test, and run the app locally. Defaults to a FULL CLEAN build so what you
# run is guaranteed to be your current source (the WinUI 3 XAML compiler's incremental
# build can otherwise silently keep stale compiled XAML).
./build-and-run.ps1
./build-and-run.ps1 -Fast      # quick incremental build for C#-only edits (may use stale XAML)

# or run the pieces by hand
dotnet build BlueBubbles.Windows.slnx
dotnet test  BlueBubbles.Windows.Tests/BlueBubbles.Windows.Tests.csproj

# build the installer (needs Inno Setup: winget install JRSoftware.InnoSetup)
./publish.ps1                  # x64
./publish.ps1 -Platform arm64  # ARM
```

`build-and-run.ps1` and `publish.ps1` share their clean/build logic via `build-common.ps1`,
so a local debug build and the shipped installer come from the same fresh tree.

The app version is the single `<Version>` in
[`BlueBubbles.Windows.csproj`](BlueBubbles.Windows/BlueBubbles.Windows.csproj). See
[INSTALL.md](INSTALL.md) for packaging details.

## Architecture

A two-project split keeps the protocol and logic independent of the UI:

- **`BlueBubbles.Core`** — protocol, services (REST API, Socket.IO, sync, contacts, notifications),
  models, and an EF Core (SQLite) local cache. No UI references.
- **`BlueBubbles.Windows`** — the WinUI 3 app: views, view models (CommunityToolkit.Mvvm), and
  reusable controls.
- **`BlueBubbles.Windows.Tests`** — xUnit test suite.

Dependency flow is one-directional: **Views → ViewModels → Services → Models**, and real-time socket
events are marshaled onto the UI thread.

## How it connects

This client never talks to Apple directly. It talks **only to your BlueBubbles server**, which runs
on **your Mac** signed into **your Apple ID**. The server is what bridges to iMessage — this app is
just a nicer window into it.

## Acknowledgments

Built on the work of the **[BlueBubbles](https://github.com/BlueBubblesApp)** project — the server,
the protocol, and the official clients this one mirrors. Huge thanks to that team; without their
server there is nothing for this client to talk to.

## Disclaimer

This is an independent, non-commercial hobby project. It is **not affiliated with, endorsed by, or
sponsored by** Apple Inc., Microsoft Corporation, or the BlueBubbles project.

*Apple*, *iMessage*, and related names are trademarks of Apple Inc. *Microsoft*, *Windows*, and
*WinUI* are trademarks of Microsoft Corporation. All trademarks are the property of their respective
owners and are used here only to describe interoperability.

The software is provided **as-is, without warranty of any kind** (see [LICENSE](LICENSE)). It is
intended for **personal, non-commercial use** with a server and Apple ID that you own and control.
Please don't sell it, charge for it, or use it commercially.

## License

Licensed under the **Apache License 2.0** — see [LICENSE](LICENSE). (The same license as the upstream
BlueBubbles project this builds on.)
