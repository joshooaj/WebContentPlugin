# 0002 — Terminate proxied HTTPS at the loopback listener; validate target TLS on the Event Server

Date: 2026-10-31
Status: Accepted

## Context

The workspace proxy (#52–#55) pins each proxied WebView2 browser to a loopback forwarding
listener (`--proxy-server` for every scheme) and replays allowlisted requests from the Event
Server network, because the Smart Client machine cannot reach the camera/VMS-network targets
directly. Ticket #56 adds HTTPS targets with an admin "accept untrusted certificate" override
stored per allowlisted target.

Chromium always sends `CONNECT host:port` for `https://` URLs routed through an HTTP proxy —
there is no absolute-form HTTPS proxy request. A blind CONNECT tunnel is useless here: the
client machine has no network route to the target, so the tunnel cannot be opened from the
client, and if TLS were tunneled to the browser the Event Server would never see the encrypted
traffic — a per-target validation override on the Event Server fetcher would be meaningless
(the browser would show its own certificate error that plugin code cannot bless).

The alternatives considered:

- **CONNECT tunnel + browser-side trust** (`--ignore-certificate-errors` variants): the
  override would have to reach Chromium, no Event Server-side chain validation happens, and
  the acceptance criteria for a validated fetch with a scoped override cannot be met.
- **Never tunnel; forward `https://` in absolute form**: not producible by Chromium through an
  HTTP proxy; page-level URL rewriting would be fragile against in-page links, scripts, and
  redirects.
- **Event-Server-side trust only**: impossible without terminating TLS somewhere in the plugin
  chain first.

## Decision

Terminate TLS at the loopback listener and keep the security-critical validation hop on the
Event Server:

1. An allowlisted `CONNECT` is answered with `200 Connection established` and then terminated
   by the listener with an ephemeral certificate for the target host, issued from a
   per-session CA that is installed into the CurrentUser Trusted Root store for the life of
   the proxied browser and removed afterwards. The browser therefore sees a genuine
   `https://target` origin (no mixed-content or cert errors).
2. The decrypted inner requests (origin-form + `Host`, resolved against the CONNECT origin)
   ride the existing `ProxyFetchQuery` → MIP messaging → Event Server fetch pipeline
   unchanged — the wire contract does not change.
3. The Event Server fetcher performs the real TLS leg to the target with **full chain
   validation by default** (no callback set), and applies the per-target override only via a
   per-request `HttpWebRequest.ServerCertificateValidationCallback` — never
   `ServicePointManager` globally — when the fetched origin carries the stored
   `acceptUntrusted` flag on its allowlist entry.
4. A non-allowlisted `CONNECT` stays refused (403), and validation failure without the flag
   produces a dedicated `TlsValidation` fetch error with an actionable message.

## Consequences

- Two TLS legs exist for an https target: browser ↔ loopback (local IPC, ephemeral cert,
  trust scoped to one session's certificate store entries) and Event Server ↔ target (the
  real, validated leg). "Accept untrusted certificate" therefore scopes exactly to the
  target origin and never to the whole system or to the browser's cert checking.
- The per-session CA/leaf plumbing (`SslStream` server auth, `CertificateRequest`) is new
  plugin surface; leaves are cached per host and both store writes and removal are logged.
  Store access is CurrentUser (no elevation).
- WebSocket (`wss://`) rides CONNECT too: the TLS-terminated inner request carrying an
  `Upgrade:` header is refused with a clear 405 — relaying WebSocket through the fetch
  pipeline stays out of scope.
- Config format: `proxyTargets` entries gain an `acceptUntrusted` member; MC admin page rows
  gain an `accept-untrusted` suffix token. Version skew degrades fail-closed (an old Event
  Server ignores the unknown member and validates normally; a new Event Server reading old
  JSON sees the flag absent = off).
- The browser must trust the ephemeral CA. WebView2 honors the CurrentUser Trusted Root
  store, so no Chromium flags are needed; the CA install/remove lifetime is tied to the
  proxied browser's listener.
