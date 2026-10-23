# iMessage-Win

Minimal starting point for a native Windows iMessage client. No application,
backend, dependency set, or build/test commands have been established here yet.

## Direction

- Replace the BlueBubbles Mac relay with an OpenBubbles/rustpush-backed client.
- Windows x64 first; distribution remains unpackaged, using identity-free APIs.
- C#/WinUI 3 is the existing baseline, not a requirement to preserve the old UI.
  Frontend changes need a separate decision; Rust does not imply a web frontend.
- [Corten-Matrix](https://github.com/lrhodin/corten-matrix) is the canonical
  integration reference. Learn its protocol lifecycle, not its Go/Matrix app
  architecture. Do not introduce Matrix as a runtime dependency.
- No always-on Mac or public Anisette service is desired; neither independence
  nor Windows compatibility has been proven for the selected implementation.

## Next Feasibility Work

1. Identify and pin the actual rustpush/Corten revisions, features, extractor,
   FairPlay inputs, and functional NAC implementation or Windows artifact.
2. Build an isolated Windows x64 harness; verify local state, startup/shutdown,
   and the chosen boundary. DLL versus local process is not decided.
3. Only then test authorized activation, account verification, IDS registration,
   send/receive, persisted identity after restart, and renewal/recovery. History,
   convergence, UI integration, and migration follow their own checks.

Prior research inspected source/docs only. No Windows build, live authentication,
registration, messaging, renewal, or iCloud sync gate has passed.

## Research Gotchas

- Corten declares a clean-room NAC default, but its inspected public OpenAbsinthe
  overlay labels `native-nac-rust` a no-op placeholder. Locate the functional
  implementation; a manifest feature or Linux binary is not Windows proof.
- Rustpush's public CI provisions numbered FairPlay slots from its legacy pair.
  Maintainers report that recipe works; absent `certs/fairplay` alone is not a
  blocker. Follow pinned upstream bytes/filenames; runtime success is untested.
- Hardware bundles, FairPlay activation, NAC/IDS validation, and Anisette account
  authentication are distinct. Extraction does not replace account sign-in or
  iCloud Keychain trust. Corten selects ClearADI, but local Windows operation
  still needs verification. Preserve stable provisioning state across failures.
- License compliance is settled for this personal project. Track dependency and
  artifact provenance without reopening that decision.

## Requirements Worth Keeping

- Retain sounds, icons, conversation-avatar UX, and bubble right-click actions.
  Broader redesign is welcome; Liquid Glass-inspired WinUI is an interest only.
- Remote data is authoritative; pin/mute/archive/avatar/bookmark preferences are
  client-owned. Do not import BlueBubbles ROWID watermarks into native sync.
- Make live/history ingestion idempotent and deletion-aware; commit checkpoints
  with their data. Persist outbound intent and distinguish submission from
  confirmation. Keep old cache migration reversible.
- Protect hardware/account/session/key material, including dependency-written
  files. Never commit or log secrets or collect them through chat/agent tools.
  Local probes may use ignored `private/` or `state/`; ignores are not encryption.
- Verify small changes with focused checks; do not claim runtime success from
  source inspection or a green build. Keep this seed small, without old backlog
  documents, speculative scaffolding, or wholesale imports.

## Selective Reference Only

The original app, assets, tests, and full research remain in the sibling
`../bluebubbles-windows` checkout. Baseline commit:
`0872f73501e74afd679d3085ab3e08beb7281e9f`.
Read `../bluebubbles-windows/docs/PIVOT.md` only when deeper evidence is needed;
that research file was untracked when this branch was prepared. Do not treat the
old relay architecture, instructions, or plans as requirements for this rewrite.