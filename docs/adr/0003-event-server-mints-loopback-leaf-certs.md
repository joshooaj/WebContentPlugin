# 0003 — Event Server mints the loopback leaf certs; deployment CA trusted via LocalMachine stores

Date: 2026-10-31
Status: Accepted
Amends: ADR-0002 (the CA/trust lifecycle in its Decision §1 and Consequences; the
loopback-termination, fetch-pipeline, and per-target override parts of ADR-0002 stand).

## Context

ADR-0002 trusted a **per-session** CA by writing it to the CurrentUser Trusted Root store.
Live probing showed two blockers:

1. `X509Store.Add` into the user root store raises crypt32's modal "Security Warning" consent
   dialog. There is no silent path into the user trust store; an unattended Smart Client session
   hangs on the prompt.
2. A Smart Client machine may serve several Windows users; per-user trust would have to repeat
   per login and still prompts. User-root trust is the wrong scope.

The alternative is a **fixed CA present in the machine's trust stores**. Because Smart Client runs
non-elevated, the plugin can never write to `LocalMachine\Root` (writes need elevation; the
installer or an admin shell is the only legitimate writer). If the CA's **private key** also sat
in `LocalMachine\My` on every client, its ACL would have to be opened to interactive users (the
listener signs leaves in-process) — any user/process on that box could then forge certificates for
any site. Name constraints were evaluated as the mitigation: a hand-built `nameConstraints`
extension (RFC 5280 §4.2.1.10, OID 2.5.29.30) verifies byte-identical to openssl's encoding, and
net472 crypt32 enforces it on the SChannel path the browser uses (`NotNameConstraintValid` for
out-of-scope leaves). But permitted subtrees are static while proxied targets are runtime
allowlist config: any DNS/IP subtree set at install time breaks legitimate public-HTTPS targets.

## Decision

Split the CA's two halves by trust boundary:

1. **One long-lived CA per deployment, created on the Event Server.** The Service-environment
   background plugin loads it from `LocalMachine\Personal` by common name on start and creates it
   on first run (the Event Server service runs as LocalSystem, which may write the machine store;
   the CAPI key lives in a fixed machine key container readable only machine-privileged accounts).
   Client machines never hold the CA private key.
2. **Leaves are minted by the Event Server on request.** A new message pair
   (`WebContent.Proxy.LeafRequest` / `WebContent.Proxy.LeafResponse`, same JSON-string-on-the-wire
   rule as #54) carries the client's base64 **SubjectPublicKeyInfo**: the client generates the
   leaf key pair (CAPI `AT_KEYEXCHANGE` provider — Schannel rejects signature keys) and the
   Event Server signs that public key with the CA. No private key ever crosses the wire.
3. **The allowlist IS the minting policy; no name constraints.** The Event Server mints only for
   origins present in the same workspace allowlist union that gates fetches (`NotAllowed`
   otherwise, fail-closed like the fetcher). Constraining subtrees was rejected as above.
   Residual risk: whoever owns the Event Server machine's machine-level key store can mint certs
   for any site — that principal already controls every proxied fetch, so the risk adds nothing
   material.
4. **Clients trust the CA public certificate via `LocalMachine\Root` as a one-time elevated
   admin step** (`Import-Certificate -CertStoreLocation Cert:\LocalMachine\Root` with the CA DER
   the LeafResponse carries). Not a WiX custom action: the CA does not exist until the Event
   Server plugin first starts, so the client MSI cannot carry it (ordering), and non-elevated
   runtime writes to Root are by design impossible. The listener checks for the CA's presence
   (thumbprint from the LeafResponse) and logs actionable guidance when it is missing; until
   trusted, tunnels still serve but the browser shows its certificate error — fail-closed,
   documented.
5. The CurrentUser-root install/remove lifecycle of ADR-0002 (`ProxyLoopbackTrust`) is removed.

## Consequences

- Leaf issuance adds one Event Server roundtrip per (host, browser session); leaves are cached
  per host exactly as before, and every proxied byte already requires the Event Server, so no
  new dependency or failure mode appears beyond "LeafRequest refused/not trusted", both logged.
- The CA outlives browser sessions and ES restarts: same certificates across sessions means
  leaf-cache semantics stay simple, and uninstall cleanup on the ES machine (delete from
  `LocalMachine\My`) is a documented manual step alongside the client-side `Root` removal.
- Client-side minting code (`ProxyLoopbackAuthority`'s authority-creation half) survives only as
  the Event Server's minting backend; the client half shrinks to key generation (SPKI export),
  leaf import, and local exchange-key ownership.
- A machine that has not run the trust step shows Chromium certificate errors for proxied https
  targets — the same fail-closed posture as any untrusted CA, with plugin log guidance.
