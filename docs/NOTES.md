# Notes

## Gate 0 audit: Corten/rustpush inputs (observed 2026-10-23)

Source inspection of immutable revisions only. Nothing was built or run; no Windows
runtime, extractor, or live Apple gate was exercised.

### Pins

| Item | Revision | Evidence |
| --- | --- | --- |
| Corten (`lrhodin/corten-matrix`) | `c381916f0d37dbbe3b23a99171debb4860067a2c` = tag `1.3.5` | `git ls-remote --tags` |
| rustpush (`OpenBubbles/rustpush`) | `cb2361c2a750ffa2047c1a64eda44c8fb8364218` | `third_party/rustpush-upstream.sha` at the Corten pin |
| rustpush `b3c96c23e1aae78d1229ebaccb12027624f1eaff` | One commit after `cb2361c`, descendant; the only changed file is `src/test.rs` | `git diff --name-only`; not Corten's pin |
| `apple-private-apis` | `b8598d2c1656ae73fbe0f1be1b18222ea7cadb0a` (rustpush submodule; same at both rustpush revisions) | `git ls-tree` |
| `OpenAbsinthe-Stub` | `1f8dc73a311e7b4d94a868972a6816c8a2c14e44` (rustpush submodule) | `git ls-tree` |
| `clearadi-stub` | `663f33f9d71b0df5436d04611755142df6db6a35` (apple-private-apis submodule) | `git ls-tree` |

### Evidence by component

| Component | Pinned source / artifact | Implementation available publicly? | Windows evidence | Unresolved dependency |
| --- | --- | --- | --- | --- |
| Corten source build | [Makefile L55-56](https://github.com/lrhodin/corten-matrix/blob/c381916f0d37dbbe3b23a99171debb4860067a2c/Makefile#L55-L56) errors unless `uname -s` is `Darwin`; [L122](https://github.com/lrhodin/corten-matrix/blob/c381916f0d37dbbe3b23a99171debb4860067a2c/Makefile#L122) builds `--no-default-features --features nac-apple-framework` | Yes, macOS only | None. Build lane refuses non-macOS hosts | None needed for macOS; not a Windows lane |
| Wrapper defaults | [`pkg/rustpushgo/Cargo.toml` L22-28](https://github.com/lrhodin/corten-matrix/blob/c381916f0d37dbbe3b23a99171debb4860067a2c/pkg/rustpushgo/Cargo.toml#L22-L28): default `cleanroom-registration` = `open-absinthe/native-nac-rust` + `*/remote-clearadi` | Declared only; the Makefile disables this default | None | Which build supplies the declared features (see next rows) |
| NAC, public overlay | [`rustpush/open-absinthe`](https://github.com/lrhodin/corten-matrix/tree/c381916f0d37dbbe3b23a99171debb4860067a2c/rustpush/open-absinthe): `native-nac-rust = []` is a documented no-op ([Cargo.toml L25-30](https://github.com/lrhodin/corten-matrix/blob/c381916f0d37dbbe3b23a99171debb4860067a2c/rustpush/open-absinthe/Cargo.toml#L25-L30)); `nac.rs` has only the macOS `AAAbsintheContext` backend and returns "no NAC backend available" otherwise (L226, L252, L274); `from_validation_data` always errors | Only the macOS framework path | None. Non-macOS yields the unsupported-backend error | Functional portable NAC is not in public source |
| NAC, upstream rustpush | `open-absinthe` submodule -> `OpenAbsinthe-Stub@1f8dc73`; README: "closed source... placeholder"; `nac.rs` L99/103/108 `todo!()` | No (stub) | None | Closed upstream implementation |
| Anisette / ClearADI, public | rustpush -> `apple-private-apis@b8598d2` -> `clearadi-stub@663f33f`; README: "closed source... placeholder"; `ProvisioningSession::new/finish`, `generate_otp`, `gen_2fa_code` are `todo!()`. Wrapper Cargo.lock resolves `clearadi` 1.0.0 as a path dependency | No (stub). `omnisette` `anisette_clearadi.rs` is public glue that calls the crate | None | Closed `clearadi` crate |
| Anisette in Corten's public source build | Makefile L122 disables `remote-clearadi`; comment says omnisette's built-in macOS AOSKit Anisette is used | macOS only | None | n/a |
| Local vs remote (ClearADI glue) | `anisette_clearadi.rs` L258-301: provisioning calls go to Apple GrandSlam (`gsa.apple.com` lookup, `midStartProvisioning`, `midFinishProvisioning`); OTP is generated locally (L243) by the closed crate | Glue public; crypto closed | None | Behavior of the closed crate is not observable. No third-party Anisette server appears in this path |
| Release 1.3.5 | [Workflow](https://github.com/lrhodin/corten-matrix/blob/c381916f0d37dbbe3b23a99171debb4860067a2c/.github/workflows/build-release-binaries.yml) is `workflow_dispatch` only. Jobs check out private `mackid1993/OpenCider` with a deploy key (`.github/scripts/checkout-opencider.sh`), run `build-macos-slice.sh` / `build-linux-zig-slice.sh` via `run-private-slice-build.sh`, then assemble a universal macOS binary. Stages include `fairplay-stubs`, `clone-public-rustpush`, `overlay-private-rust`, `apply-rustpush-patches`. Anonymous `git ls-remote` of OpenCider: "Repository not found" | No. Build scripts, Rust overlay, features, and patches are private | None. The release has no Windows asset | Private OpenCider revision, overlay, and feature set; source of the portable NAC/ClearADI in the Linux binary |
| Release assets | `corten-matrix-linux-amd64` sha256 `3e888d95...ff099` (72657368 B); `-linux-arm64` `dd105892...886f9` (68111976 B); `-macos` `2686e580...a7c77` (119771104 B). Published 2026-10-09T04:41:16Z by the release API; digests are GitHub-reported | Binaries only; no provenance attestation seen | None. Linux ELF and macOS Mach-O executables, not a Windows library | Cannot be tied to public source (see above) |
| FairPlay slots | Corten Makefile `FAIRPLAY_CERTS` (10 numbered names) copies `certs/legacy-fairplay/fairplay.{crt,pem}` into `certs/fairplay/<name>.{crt,pem}` without transformation; the same 10 names appear in rustpush `.github/workflows/build.yml` and `src/activation.rs` at **both** `cb2361c` and `b3c96c2`. Legacy blobs: `fairplay.crt` git blob `ec3853db2d3b80e8639a44948065fa5c298476ca`, `fairplay.pem` `a1ba622279b9b1a452ab2c2e84962670be55d0d2` (identical at both revisions). The private release workflow also has a `fairplay-stubs` stage | Yes, recipe and inputs public at the Corten pin. Slot names are not a blocker | None | Runtime activation success is untested |
| Extractor / hardware key | Corten README: Linux needs a key from the current extractor (`tools/`, pre-binary-release extractors incompatible); macOS needs no key. The public `HardwareConfig` struct and key enrichment are no-ops in the public overlay (`enrich_missing_enc_fields`) | Extractor binaries are published; the consumer of the key (portable NAC) is not | None | Key schema consumed by the private NAC is unobservable |
| Windows tooling in Corten | Corten `AGENTS.md` references `dev\windows-dev-env.bat` and `dev\windows-bindings.bat`, but no `dev/` directory exists at `c381916` | No | None | Doc references files absent from this revision |

### Secondary candidate (bounded look)

| Item | Evidence | Windows evidence | Unresolved |
| --- | --- | --- | --- |
| `jasonlaguidice/imessage` at `595ce7e9bb588fbcf7cbe9906a0b79b7b0869c41` (2026-10-11) | Its `rustpush/open-absinthe` is a real public Unicorn emulator of Apple's `IMDAppleServices` (`nac.rs` downloads that binary from a third-party GitHub URL at runtime, L457/L611), fed by an extracted hardware key. `build.rs` sets `has_xnu_encrypt` only for x86_64/aarch64 Linux targets; `_enc` derivation returns an error without it. Makefile selects `--features hardware-key` on non-macOS. Pin file `third_party/rustpush-upstream.sha` = `48bcc271b220ff219513822a99aadd8c0f469e36` | Repo ships `dev/windows-*.bat` for MSVC + UniFFI bindings; no evidence NAC works on a Windows target, and the `build.rs` gating suggests it does not | Windows NAC path; Anisette provider at its pin (not inspected); redistribution/provenance of the downloaded Apple binary |

### Premises

1. Confirmed. Pin file at `c381916` contains `cb2361c...`.
2. Confirmed. Makefile L55-56 and L122; `ValidationCtx::new` returns "no NAC backend available" unless macOS + `native-nac`.
3. Confirmed, with an addition: the manifest advertises both `native-nac-rust` and ClearADI, but the public source supplies neither. `native-nac-rust` is a no-op and `clearadi` is a stub with `todo!()`. A build enabling `cleanroom-registration` from public inputs would compile against stubs and panic or fail at runtime. Corten's own public build avoids this by disabling the default.
4. Confirmed. 1.3.5 has Linux amd64/arm64 and macOS assets. They are built by a manually dispatched workflow from private OpenCider. The relationship between those binaries and the public source-build lane cannot be verified from public inputs.
5. Confirmed. The recipe is byte-identical in slot names and sources at `cb2361c` and `b3c96c2`; neither was combined with the other.
6. Confirmed by inspection of PIVOT (its "Local References" point to `CLAUDE.md`, `README.md`, `BlueBubbles.Windows/`, `SyncService.cs`, `PUNCHLIST.md` that are absent from this seed). The proposed PIVOT edits are in the PR description.

### Conclusion: blocked on Corten's portable implementations

No reproducible Windows x64 input set exists from Corten `c381916` / rustpush `cb2361c`. The public source path is macOS-only. The portable NAC and ClearADI behind the Linux binaries sit in private OpenCider, and the public rustpush submodules for both are non-functional stubs. This does not show that every Windows rustpush path is impossible: the Jason fork's emulator is a public NAC candidate, but its build gates it to Linux and it needs an Apple binary fetched at runtime.

Evidence that would resolve the blocker, any one of:
- A public, pinned source or Windows artifact for the functional NAC and ClearADI that Corten's Linux releases use, with license/provenance.
- A statement from upstream that Windows x64 is a supported or planned target.
- A public NAC and Anisette implementation (for example the fork's emulator plus a located Anisette provider) that builds for `x86_64-pc-windows-msvc` and passes a local Windows NAC probe.

Draft upstream question (not posted):

> Corten 1.3.5 release binaries for Linux are built from private OpenCider, while the public tree at `c381916` builds only on macOS and the public `open-absinthe` / `clearadi` submodules are stubs. Is there any public source, build recipe, or artifact for the portable NAC and ClearADI used by the Linux releases, and is Windows x64 (MSVC) a target that could be supported or is known not to work?
