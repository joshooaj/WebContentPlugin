# ADR-0001: Key the WebContent browser profile to the XProtect login identity

- **Status:** Accepted
- **Date:** 2026-08-02
- **Tickets:** [joshooaj/WebContentPlugin#17] — browser profile/history shared across XProtect accounts using a shared Windows account

## Context

The Smart Client runs under a single Windows logon. The Web content plugin creates WebView2
controls with a user data folder (UDF) under `%APPDATA%\SmartClient.WebContentData`, with a
`ProfileName` of `""` (shared default) or the view-instance id. That folder is scoped to the
**Windows user**, so two different XProtect accounts logging in from the same shared Windows
account currently inherit each other's WebView2 browsing history, cached cookies and saved
credentials.

The security concern: "Guard" is the local Windows account. User1 and User2 are two XProtect
accounts that both log in as Windows account "Guard". User1 signs in to web content; User2
later opens the same machine and inherits User1's browser session and credentials.

### Spike: can WebView2/Edge provide cryptographic isolation per XProtect user within one Windows account?

No. WebView2/Chromium stores protected data (cookies, saved passwords, logged-in state) protected
with Windows DPAPI (`DataProtectionScope.CurrentUser`). DPAPI keys are derived from the Windows
logon, not from any XProtect account. Multiple browser profiles or user data folders under the same
Windows logon remain within that single DPAPI trust boundary. There is no WebView2 API that yields
a separate cryptographic secret per profile within one Windows logon.

Consequence: **the Windows account is the security (cryptographic) boundary.** Separate XProtect
users under one Windows account cannot be cryptographically isolated by the plugin alone.

## Decision

1. **Key the WebView2 user data folder by the logged-in XProtect login identity.** Each XProtect
   user gets its own user data folder (`%APPDATA%\SmartClient.WebContentData\<key>`). The key is
   the user's `LoginSettings.UserIdentity`, falling back to `FullyQualifiedUserName`, then
   `UserName`, sanitized to a filesystem-safe segment. This is **data separation**, not
   cryptographic isolation.
2. **Adopt the Windows account as the security boundary.** Because WebView2 cannot encrypt per-
   XProtect user within one Windows logon, cross-XProtect isolation on a *shared Windows account*
   cannot be achieved cryptographically. The plugin therefore *documents* the requirement: each
   person or each distinct XProtect identity that must be isolated must log in with a distinct
   Windows account.
3. **Preserve deliberate sharing at the documented boundary.** A shared WebView2 profile across
   multiple operators remains possible by design: it only occurs when those operators log in with
   the same XProtect login identity on the same Windows machine account. That is the supported,
   documented way to share a single browsing profile (for example on a shared control-room
   operator), as opposed to each operator using a distinct identity.

## Consequences

- Different XProtect accounts on the same Windows account no longer inherit each other's history,
  cookies or credentials on a fresh Smart Client session — they start from a **fresh**, empty profile.
- Existing persisted browsing data in the shared `SmartClient.WebContentData` root is no longer the
  default location; users see a clean profile once after upgrade. This is intentional for the security
  fix. The old shared data folder is left in place and is not removed automatically.
- The "Isolate browser sessions" per-instance setting continues to work **within** a given XProtect
  user's folder: isolated instances get their own WebView profiles under that user's UDF.
- The default-profile novelty: instances that are not "isolated" still share the user's default
  profile, preserving existing single-user behaviour.
- **Deliberate sharing requires a shared (Windows account, XProtect identity) pair.** To share web
  content history intentionally across operators, log those operators in as the same XProtect user
  on the same Windows machine account. Do not rely on profile isolation to protect two different
  XProtect identities that must remain separate.

## Alternatives considered

- **Profile `Name` per XProtect user instead of a separate UDF**: WebView2 multi-profile shares one
  UDF and does not encrypt per profile; it also keeps every profile on disk under one folder. A
  separate UDF per user gave clean removal and matched the documented Microsoft guidance to
  "create a new UDF per user". Rejected for the primary split.
- **Per-instance profile names only**: covers **view instances**, not users; a different XProtect
  user would still land in the same user data folder. Insufficient.

[joshooaj/WebContentPlugin#17]: https://github.com/joshooaj/WebContentPlugin/issues/17