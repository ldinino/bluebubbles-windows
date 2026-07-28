# Security Policy

This is an independent, non-commercial hobby project maintained by one person. It is **not**
affiliated with Apple, Microsoft, or the BlueBubbles team. There is no security team and no
guaranteed response window — but security reports are taken seriously and handled as a priority
over everything else on the punchlist.

## Supported versions

Only the **latest release** is supported. There are no backported fixes; the remedy for any
issue is always "update to the newest version."

| Version | Supported |
| ------- | --------- |
| Latest release | Yes |
| Anything older | No |

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Use GitHub's private reporting instead:
[**Report a vulnerability**](https://github.com/ldinino/bluebubbles-windows/security/advisories/new).
That opens a private channel visible only to the maintainer.

Helpful things to include, if you have them: the affected version, what an attacker gains, the
steps to reproduce, and whether you want credit in the published advisory.

Please give a reasonable window to ship a fix before disclosing publicly. Fixes are published as
a normal release plus a [GitHub Security Advisory](https://github.com/ldinino/bluebubbles-windows/security/advisories).

## Scope

**In scope** — this Windows client: how it stores your server credentials, what it writes to disk
or to logs, how it talks to your BlueBubbles server, and the installer.

**Out of scope** — the [BlueBubbles macOS server](https://github.com/BlueBubblesApp/bluebubbles-server)
and the [BlueBubbles mobile app](https://github.com/BlueBubblesApp/bluebubbles-app); report those
to the BlueBubbles project. Also out of scope: the unsigned-binary SmartScreen prompt (a known,
documented consequence of not paying for a code-signing certificate — see [INSTALL.md](INSTALL.md))
and anything that requires an attacker to already have administrator rights on your PC.

## How this app stores your credentials

Your BlueBubbles server password is encrypted with **Windows DPAPI**
(`DataProtectionScope.CurrentUser`) and stored in `%LOCALAPPDATA%\BlueBubbles\credential.bin`.
It can only be decrypted by your Windows user account on that machine. It is **never** written to
`settings.json`, never written to the log files, and never included in a URL that gets logged.

Note that DPAPI protects the password from *other user accounts* on the same PC, not from malware
already running as *you*. Nothing on a desktop OS can protect a stored secret from that.

## Past advisories

- **[GHSA-7r7p-r4ph-w8m5](https://github.com/ldinino/bluebubbles-windows/security/advisories/GHSA-7r7p-r4ph-w8m5)**
  — *Server password stored in cleartext in `settings.json`* (Medium, CVSS 5.5). Affects 0.19.4
  through 0.22.4; **fixed in 0.22.5**. Those installers have been removed from the Releases page.
  Updating to 0.22.5 or later migrates the password into the encrypted store and deletes the
  plaintext copy automatically.
