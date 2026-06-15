# Release Notes Style Guide

This defines the format for the body of every GitHub Release
(https://github.com/ldinino/bluebubbles-windows/releases). Apply it to every release —
patch, minor, or major — so the release history reads consistently. The release *title*
("BlueBubbles X.Y.Z") is set automatically by `release.yml`; only the body needs writing.

## Template

```markdown
## BlueBubbles for Windows X.Y.Z

<One or two sentences summarizing the release for an end user.>

> Independent, non-commercial project. **Not affiliated with** Apple, Microsoft, or the
> BlueBubbles team.

### New
- **Feature name.** What it does and how to use it, in plain language.

### Fixes
- **Short description of the bug.** What was wrong and what changed, written from the
  user's perspective.

### Notes
- **x64 only.** The build is unsigned, so the first launch shows a one-time Microsoft
  Defender SmartScreen prompt — choose **More info -> Run anyway**.
- **Install / update:** download and run `BlueBubbles-Setup-X.Y.Z-x64.exe` below. A
  `payload-manifest-x64.txt` (SHA-256 of every shipped file) is attached for verification.

**Full changelog:** https://github.com/ldinino/bluebubbles-windows/compare/vPREV...vX.Y.Z
```

## Rules

- **Title** is always `## BlueBubbles for Windows X.Y.Z`.
- **Intro** is 1-2 sentences, plain language, describing what the release *is* or *does*
  for the user — not internal framing ("this PR...", "fixes issue #...").
- **Disclaimer is mandatory, verbatim, on every release.** Place it immediately after the
  intro, as a blockquote, before any section headers:

  > Independent, non-commercial project. **Not affiliated with** Apple, Microsoft, or the
  > BlueBubbles team.

- **No emoji.** Not in headers, not in bullets, not anywhere.
- **`### New`** — only when the release adds user-facing features. Omit entirely for
  fix-only releases.
- **`### Fixes`** — bug fixes. Omit if there are none.
- **`### Notes`** — always present. Always include the two boilerplate bullets above
  (x64-only/SmartScreen, install/update) with the version substituted in. Add any
  release-specific caveats (compatibility notes, behavior callouts, "this is
  reversible", etc.) as additional bullets in this section.
- **Full changelog link** — always include, comparing the previous tag to this one. Omit
  only for the very first release (no previous tag exists).
- **Bullet style** — bold lead-in naming the change, followed by a plain-language
  explanation. Describe what the user will see or experience, not which file or class
  changed.

## History

Releases v0.19.4-v0.21.1 predate this guide and were retrofitted to match it (disclaimer
added, emoji removed, structure aligned) for consistency.
