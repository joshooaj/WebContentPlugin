# ADR-0004: Scope proxied browser state and listener ownership to the proxied boundary

- **Status:** Accepted (point 5 settled by #90's measurements, 2026-11-03)
- **Tickets:** [joshooaj/WebContentPlugin#87] (symptom and interim fix), [joshooaj/WebContentPlugin#90] (browser-process lifetime probe), [joshooaj/WebContentPlugin#91] (enablement) — builds on ADR-0001, ADR-0002, ADR-0003

Proxied views run a loopback listener whose port is baked into the WebView2 browser arguments, while the proxied user-data folder is deterministic per workspace item. WebView2 shares one user-data folder, and one browser process, across host processes only when the environment options match exactly, so a second Smart Client process on the same machine fails initialization and shows a blank view (#87). We decided to *enable* that second process rather than fragment the profile: one shared proxied profile per workspace item, with ownership made explicit by a claim record instead of implicit in process race order. Cross-process profile sharing (cookies, cache, logins) is then free, which fragmenting the profile would throw away and laboriously rebuild.

The sharing is fenced by the **proxied boundary**: the (Windows user, XProtect identity, VMS server) tuple. The proxied user-data folder and the listener claim are both keyed on the full tuple, so proxied browser state and proxy fetching are never shared across it. This matters most for the server leg: two Smart Clients logged into *different* VMS servers on one machine must never share a proxied profile or a fetch channel, because the joined process's traffic rides the owner's Event Server messaging connection and the owner's session identity. Today the server enters the profile key only accidentally (the identity value differs per server only when the identity source happens to differ); the boundary makes it explicit.

## Decision

1. **Keep one shared proxied profile per browser id.** One browser process per proxied profile per machine, shared by every Smart Client process inside the boundary, not one per process.
2. **Key the proxied profile and the listener claim on the full proxied boundary** (Windows user, already implicit in the per-user data root, plus XProtect identity plus VMS server). Crossing the boundary is impossible by construction, so a client can never ride another identity's or another server's proxy session.
3. **Keep the ephemeral loopback port; publish a claim.** The owning process records browser id, port, owning process id, and config fingerprint under the boundary-scoped data root, written atomically. A joining process reads the claim and composes the identical `--proxy-server` argument rather than binding its own listener. A claim whose owner process is gone or whose port is unresponsive is stale.
4. **Accept the within-boundary consequence.** Inside one boundary every proxied fetch rides the owner process's Event Server connection, and #69 diagnostics show one session per proxied profile rather than per client process. This is deliberate, not an oversight.
5. **Handoff (measured by #90).** The browser process survives the owner host's clean exit AND crash while any attached host remains, so at handoff the views are alive but their baked-in port is dead: the rule is stale claim (dead pid / unresponsive port) -> one survivor re-claims a fresh listener, the claim epoch bumps, attached processes release their proxied WebViews, and the browser process recycles to carry the new port. A fresh init respawns a destroyed collection in ~13 s measured. #87's in-view ownership error covers the window. Until enablement lands, #87's error is also the whole contract.

## Consequences

- Two Smart Client processes on the same machine, same user, same server both get working proxied views sharing cookies and cache (once #91 lands).
- #69 diagnostics granularity is per proxied profile, not per process.
- The claim file is shared mutable cross-process state: it needs atomic creation and staleness detection, and the proxied profile folders need an owner for disk cleanup, which nothing does today.
- A comparable keying gap exists for **non-proxied** profiles (same username on two servers can collide via the username fallback); fixing it is outside this decision.
- The claim's port target is the TLS-terminating listener of ADR-0002; a joined session's traffic is still intercepted and allowlisted by the owner's listener, so #65 rule triggers and #83 block reporting keep working, attributed to the owner's session.

## Alternatives considered

- **Per-process user-data folders with cookie replay (CookieManager hydrate/dehydrate):** full isolation, but no shared cache, localStorage, or passwords; a web app keeping login state in localStorage would re-log-in per process, and a plugin-owned cookie jar with merge rules is new state to guard. Rejected: the isolation it buys is already provided by the boundary.
- **Deterministic port per browser id:** arguments match without a claim file, but two processes cannot both bind one port, so ownership still has to be decided; the claim file just makes the race explicit, discoverable, and stale-detectable. Rejected as a bare mechanism.
- **Machine-level proxy helper with its own VMS connection:** decouples the listener from any client's lifetime and makes the argument trivially constant, but breaks per-identity fetch attribution and adds an installed component. Rejected: heavier than the boundary design buys back.
- **Degrade only, never enable:** cheap, but the shared-browser-process model is supported and nearly free once options match, so the blank view is a self-inflicted mismatch rather than a platform wall. Kept only as the interim contract (#87).

[joshooaj/WebContentPlugin#87]: https://github.com/joshooaj/WebContentPlugin/issues/87
[joshooaj/WebContentPlugin#90]: https://github.com/joshooaj/WebContentPlugin/issues/90
[joshooaj/WebContentPlugin#91]: https://github.com/joshooaj/WebContentPlugin/issues/91
