---
description: 'Security requirements for secrets management, input validation, permissions, and secure coding'
applyTo: '**/*.cs'
---

# Security

These rules apply to **every feature and change**. They are not optional add-ons.

---

## Rules

- **Never hard-code secrets** (API keys, passwords, connection strings) — use environment variables, Windows Credential Manager, or Azure Key Vault.
- Validate and sanitize **all external input** (user input, file content, network responses).
- This app runs **unpackaged** (no package identity). Protect secrets at rest with **DPAPI** (`System.Security.Cryptography.ProtectedData`, scoped to `CurrentUser`) — see `CredentialService`. Do **not** use `PasswordVault`/`Windows.Security.Credentials`, which require package identity and throw when unpackaged.
- Follow the **principle of least privilege** — the unpackaged app already runs full-trust, so guard sensitive operations in code rather than relying on a manifest capability model.
- Keep NuGet packages up to date — run `dotnet list package --outdated` regularly.
- The app is distributed **unsigned** via the Inno Setup installer (`publish.ps1`). There is no MSIX, no certificate, and no code-signing step — do not reintroduce one. (MSIX signing is what previously broke toast-notification activation.)
- When using `HttpClient`, always validate TLS certificates and use HTTPS.
- Never log sensitive data (PII, tokens, passwords).

## Anti-patterns

- Storing secrets in `appsettings.json` committed to source control.
- Disabling TLS validation for debugging and forgetting to re-enable it.
- Using `Process.Start` with unsanitized user input.
- Broad `try { } catch (Exception) { }` that swallows errors silently without any logging.

## Validation

- Validate by building and running unpackaged (`./build-and-run.ps1`) — see **Build, Run & Deploy** in `AGENTS.md`.
- Check for hard-coded secrets: search for `password`, `apikey`, `secret`, `connectionstring` in `.cs` files.

### Verification Checklist

- [ ] No secrets are hard-coded

## Must Read & Research

> **Agent Rule:** Before any security-related change (auth, input handling, permissions, HTTP), you **must** fetch and review these references using `fetch_webpage`. Apply what you learn.

| # | Reference | When to consult |
|---|---|---|
| 1 | [.NET Security Best Practices](https://learn.microsoft.com/en-us/dotnet/standard/security/) | Any code handling credentials, tokens, or sensitive data |
| 2 | [Secure coding guidelines for .NET](https://learn.microsoft.com/en-us/dotnet/standard/security/secure-coding-guidelines) | Input validation, exception handling, type safety |
| 3 | [Data Protection API (DPAPI)](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection) | Encrypting secrets at rest for an unpackaged app |


