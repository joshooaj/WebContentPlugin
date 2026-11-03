# Verification matrix — regression safety net (ticket #16)

Release-gating verification contract for the WebContent Smart Client plugin. Every area is
either pinned by the automated suite (preferred) or by an explicit live-host check with a
named log marker. An area is **verified** only when all of its rows pass.

## How to run

| Gate | Command | Pass criterion |
|---|---|---|
| Plugin build | `Invoke-psake -taskList Build` (raw equivalent: `dotnet build src/WebContent/WebContent.csproj -c Debug`) | exit 0, no sbom-tool warnings (the GenerateSbom target passes `-D true`, so stale `_manifest` dirs self-overwrite) |
| Regression suite (all automated rows) | `Invoke-psake -taskList Test` (builds first; raw equivalent: `dotnet test src/WebContent.Tests`) | exit 0, zero failed |
| UI probe suite (live-host rows) | `Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~ProbeProxied' }`; bare `TestUI` runs the whole `tests/ux` project | probes go red on failure, never silently skip; needs the deployed drop on both machines and the per-row env flags |
| One-command gate script | `.\verify.ps1` (add `-IncludePluginBuild` to also run the plugin build) | exit 0, prints `Release gate: PASS` |
| CI | `Tests` job in `.github/workflows/ci.yml` (runs on PRs and pushes) | job green before merge to `main`; the installer only publishes from `main` |
| Deploy | `Invoke-psake -taskList DeployLocally, DeployToVms` (see `AGENTS.md`) | local drop hash-identical to `output\WebContent\`; ES restarts and logs its ready marker |
| Manual host checks | Steps below, run against a deployed plugin per `AGENTS.md` | each row's expected outcome and log marker observed |
| Full installer build | `.\build.ps1 [-Version x.y.z]` (also caches the WebView2 runtime and builds `src/Setup/setup.wixproj`) | exit 0; SBOM guard green; #70 sequencing guard throws unless the MSI force-closes the Milestone clients before RemoveFiles/InstallFiles with RemoveExistingProducts late |

Manual-check logs: Smart Client → `%ProgramData%\Milestone\XProtect Smart Client\MIPLog.txt`
(no `Logs\` subfolder); Management Client → `Logs\MIP.log` under its own ProgramData folder.
`MIPLog.txt` can be absent mid-session or when client logging is disabled; a missing log file
is not evidence the plugin never ran.

## Matrix

| Area | Origin | Automated checks (xunit) | Manual host checks |
|---|---|---|---|
| Browser/view lifecycle | #10 | `BrowserRegistryTests` (reuse until reclaimed, bounded create/release/reclaim cycles), `CallbackOwnershipTests` (tokens stale after owner ends) | See MC-1 below: close/reopen cycles reuse the browser, no duplicate message handling |
| Configuration resilience | #11 | `ConfigResilienceTests` (legacy booleans, zoom clamp incl. NaN/Infinity, unparsable GUIDs, null providers, case-insensitive keys, corrupt `WorkspaceConfig` JSON → legacy fallback), `WorkspacePropertiesTests` (JSON-blob and legacy paths produce canonical values) | See MC-2 below: out-of-range admin values clamp at runtime |
| Address handling | #14 | `AddressNormalizationTests` (host specs → canonical `https://`, garbage/non-http rejected, cross-flow canonical consistency via `Uri.AbsoluteUri`) | See MC-3 below: invalid input is refused with log markers |
| Workspace provisioning | #12 | `WorkspaceViewItemSyncTests` (stale/foreign view items need re-provision), `WorkspaceDefinitionTests` (`SelectVisible` filters displayable + permitted; unpermitted stay silently skipped), `WorkspaceDefinitionFingerprintTests`, `WorkspaceInstancePropertiesTests` (seed recomputation) | See MC-4 below: tabs recover after transient config-read failure without client restart |
| License handling | #13 | `PluginLicenseHygieneTests` (deleted license classes stay deleted; no plugin source references `VideoOS.Platform.License`, `LicenseManager`, `ReservedLicenseManager`, `PluginLicenseType`) | See MC-5 below: no license/trial UI or prompts in either client |
| Long-session stability | #15 | `BrowserRegistryTests.Repeated_Create_Release_Reclaim_Cycles_Keep_Tracking_Bounded`, `CallbackOwnershipTests`, `BrowserProfileKeyTests` (profile isolation) | See MC-1 and MC-6 below |
| Session state (last-URL restore) | #9 | `WorkspaceSessionStateTests` (record/overwrite per instance key, fingerprint guard makes a definition edit unreadable, reset, blank-fingerprint standalone exclusion, restore precedence recorded → persisted → home, definition-update detection from fingerprints) | See MC-7 below: workspace switch restores the page, restart resets to home |
| Configuration error visibility | #9 | `WorkspaceDefinitionTests` (`ConfigurationError` classifies malformed JSON, blob-without-items, no-home-address item, nothing configured; malformed JSON reported even when legacy properties would rescue it; `SelectVisible` keeps named-but-broken tabs and still skips unnamed/unpermitted) | See MC-8 below: broken tab appears with a WorkSpaceNotification warning |
| SBOM integrity | #25 | `SbomGenerationGuardTests` (PostBuildEvent keeps a quoted absolute `-bc "$(MSBuildProjectDirectory)"` component path and `-D true`; the original bug let the CWD-relative `-bc .\` point at the build drop, so zero packages were detected) | `.\build.ps1` throws unless `output\WebContent\_manifest\spdx_2.2\manifest.spdx.json` lists referenced packages; CI Build job shows `SBOM OK` and no "no packages detected" annotation |
| Proxy configuration roundtrip | #52 | `ProxyConfigurationTests` (definitions saved before the proxy existed mean proxy-off; toggle+targets survive the serialized roundtrip), `ProxyPolicyTests` (admin entries canonicalize to scheme/host/port origins; garbage rejected; exact-origin matching; duplicates/overlaps collapse), `WorkspacePropertiesTests` (JSON-blob and legacy paths emit canonical `ProxyEnabled`/`ProxyTargets`, hand-crafted target gibberish skipped) | See MC-9 below: admin toggle round-trips to the Smart Client properties page; invalid target save refused with log marker |
| Proxy fetch messaging (Event Server fetcher) | #53 | `ProxyFetchContractGuardTests` (message ids never end in "Command" so SecurityLevel.Normal clients can send; payload types stay plugin-namespace `[Serializable]` with public fields — wire shape itself is pinned by `ProxyFetchWireCodecTests` since #54; frame-kind/error-code constants stable), `ProxyFetchFramerTests` (meta frame Sequence=-1 then base64 chunk frames 0..N-1 with IsLastChunk; failure is a lone frame), `ProxyFetchCorrelationTests` (meta+chunks→complete body; out-of-order buffered; failure frame surfaces error code; foreign correlation ids ignored; deadline silence → `ServerUnavailable` not target-leg `Timeout` since #58), `ProxyFetchTransportTests` (real HttpListener: status+headers captured, size cap aborts, connect/read budgets, refused connection maps to Unreachable; #55: method/form body/Cookie+custom headers replayed at the target, 302+Set-Cookie relayed instead of followed, repeated Set-Cookie values captured separately), `EventServerLoadGuardTests` (plugin.def load env includes `Service`; the definition exposes `BackgroundPlugins` with a Service-targeted plugin and declares both fetch message ids in `PluginDefinedMessageIds`) | See ES-1 and MC-10 below: Event Server loads the fetcher, and the Management Client harness fetches a page through it with failures logged |
| Loopback forwarding proxy (Smart Client view consumes the fetch seam) | #54, write verbs #55 | `ProxyForwardCodecTests` (absolute-form proxied request parsing; origin-form/non-http refused; CONNECT recognized by authority target; outbound request-header filter keeps Cookie/Authorization/custom headers and drops hop-by-hop+Host+Content-Length; forwarded-response rendering: status line, content headers relayed, hop-by-hop dropped, Content-Length restated from the reassembled body; refusal pages), `ProxyForwardPolicyTests` (decision table via `ProxyPolicy`: allowlisted GET/HEAD/POST/PUT/DELETE/OPTIONS/PATCH forward, other origins 403, TRACE/junk verbs 405, CONNECT 502, malformed 400 — address-bar navigation follows the same rule), `ProxyForwardConnectionTests` (full stream flow: forward+relay, POST method/form body/Cookie to the fetch seam, 302+Set-Cookie relayed back, repeated Set-Cookie rendered as separate lines, 403 without touching the fetcher, oversized declared body 413, chunked body 411, Unreachable→502, Timeout→504), `ProxyBrowserArgumentsTests` (every http/https/ws scheme pinned to the listener, `<-loopback` bypass off — never direct), `ProxyWorkspaceProfileTests` (enabled+targets → listener allowlist; duplicates collapse; enabled-with-no-targets still proxies and allows nothing; disabled → no profile), `ProxyServerAllowlistTests` (ES refuses origins no proxied workspace allowlists, proxy-off workspaces don't count, closed before first resolve, stale-on-error keeps serving the last snapshot) | See SC-1 below: with the drop deployed on both the Event Server machine and the Smart Client machine, and the client's route to the target killed, the proxied page still loads — proof the traffic source is the server, not the client. See SC-2: a login POST round-trips with its session cookie in a live Smart Client session |
| HTTPS targets: target TLS verdict, per-target override, loopback leaf mint (ADR-0002/0003) | #56 | `ProxyFetchTlsDecisionTests` (https targets get full chain validation on the Event Server; an untrusted chain refuses with `TlsValidation` unless the allowlist entry carries the per-target accept-untrusted override — never a global bypass), `ProxyPolicyTests` (target-line grammar: `https://host:port accept-untrusted` in either token order; unknown tokens or a second origin reject the whole entry), `ProxyServerAllowlistTests` (the `acceptUntrusted` flag round-trips with its entry through the workspace JSON), `ProxyLeafContractTests` (LeafRequest carries ONLY the client-generated SPKI; the allowlist union is the minting gate — `NotAllowed` fail-closed like the fetcher, `BadRequest` for malformed origin/key, `NoAuthority` when the deployment CA cannot load/create; minted leaf chains to the deployment CA with host SAN + serverAuth EKU; single-frame correlation with deadline), `ProxyLoopbackTlsTests` (leaves cached per host:port, one mint exchange per origin; refused mint fails the tunnel with the server's code; seeded `AcceptTunnelAsync` + real Schannel server handshake on a channel-minted leaf), `ProxyLoopbackClientKeyTests` (exported SPKI is public-only; `AttachLeaf` yields a private key Schannel accepts; failed attach keeps the key usable), `ProxyFetchReplyGuardTests` (leaf answers unicasted to the requester endpoint, never broadcast), `EventServerLoadGuardTests` (leaf message ids declared) | See SC-3 below: both-machine drop plus the one-time elevated CA import into `LocalMachine\Root`; a self-signed https target fails closed WITHOUT the override (proxy error page names the exact per-target switch) and the full login flow completes WITH it |
| WebSocket CONNECT tunnels through the proxy (raw bytes both directions, outside the fetch chunking path) | #57 | `ProxyForwardPolicyTests` (absolute ws:// upgrade to an allowlisted http origin tunnels; CONNECT to an http-twin-allowlisted origin accepted as the ws-tunnel posture live Chromium actually uses — https entries keep precedence for the #56 TLS posture; wss:// and in-tunnel upgrades refused 501, never fetched — the 501 posture was lifted by #60, see the #60 row); missing Sec-WebSocket-Key / wrong version 400; h2c upgrades stay ordinary forwards), `ProxyWebSocketHandshakeTests` (upgrade recognized only for GET+Connection upgrade token+Upgrade websocket; rebuilt origin-form handshake: uri-derived Host, browser's Sec-WebSocket-Key byte-identical, explicit Connection/Upgrade pair, header-injection gate; 101-only admission; head terminator), `ProxyTunnelContractTests` (six directional ids; one JSON-string payload shape for the whole protocol; Data/Close kinds; limits), `ProxyForwardConnectionTests` (upgrade head pipelined bytes ride into the tunnel first; the target's verbatim 101 written to the browser before the first pump byte; both pumps; either side ending ends both; in-tunnel wss refused 501 through the tunnel stream — superseded by #60's real in-tunnel tunneling), `ProxyTunnelServerTests` (in-process ws target: 101 admits and both pumps echo; non-101 refused HandshakeRefused; dead target Unreachable; ES cap rejects; duplicate id rejected; client bytes reach the target in order through the serialized writer; idle reclaim closes and notifies; CloseAll), `ProxyTunnelCorrelationTests` (client registry: ordered pump queue, open deadline completes as failure, half-open + idle reclaim return ids for close notification, orphan open-ack closes its handle, teardown closes all), `EventServerLoadGuardTests` (tunnel ids declared in PluginDefinedMessageIds) | See SC-4 below: the ws-echo page in a proxied Smart Client browser shows its token echoed (client-side proof) while the ws server journals WS-HANDSHAKE + WS-ECHO (server-side proof the upgrade arrived as a tunnel, not a fetch) |
| Explicit proxy error pages in the view, no direct fallback (#58), friendly cause categories + collapsible technical detail on those pages (#66) | #58, #66 | `ProxyForwardConnectionTests` (each failure kind renders its own page with the failing side named: `ServerUnavailable` → 503 page naming the VMS proxy component and forbidding direct fallback; server-side `NotAllowed` → 403 allowlist posture distinct from target failure; `Unreachable` → 502 "VMS server could not reach the target"; `Timeout` frame → 504 target-leg; `TooLarge` → page naming the size cap), `ProxyErrorPageTests` (the #66 wording layer: every `ProxyFetchErrorCodes` value maps to its OWN distinct cause category - collapsing distinct causes fails the test; headline + cause chip + explanation carry the #58 side-attribution sentences verbatim; technical detail collapsed by default with the canonical allowlist entry that would admit the origin, the byte cap, the verbatim refusal text, and the Copy diagnostics affordance), `ProxyFetchCorrelationTests` (deadline silence = `ServerUnavailable`, never the target-leg `Timeout` code; `Fail` completes the exchange with the channel reason) | See SC-5 below - **all live rows PASSED 2026-11-01 (records 3 + 4 + 5)**: stopped Event Server + reload renders `Web Content proxy: 503` naming the component with the `ServerUnavailable` MIPLog marker (run `20261101-082437`), and an off-allowlist start URL renders the 403 page as the workspace's first document with the home address restored afterwards (run `20261101-084616`). Recovery is asserted in the same row, not narrated: a fresh Smart Client session after the restart gets the real document back (run `20261101-130629`). **The #66 friendly page re-passed the start-URL row live 2026-11-02 (record 6, run `20261102-232047`)**: headline + cause chip rendered, technical section collapsed by default, Copy diagnostics button present, canonical allowlist entry in the diagnostics - all asserted, not screenshot-only. Both-machine drop in place (record 3) |
| Off-allowlist **https** refusals render the friendly page at the VIEW layer (Chromium transports https only as CONNECT and DROPS a refused CONNECT's body - the proxy's own 403 page can never reach the screen for those) | #88 | `ProxyViewRefusalTests` (the gate answers https only - plain http is never double-answered and stays on the proxy's absolute-form path; an off-allowlist https origin gets the #66 page: `NotAllowed` cause chip + the verbatim "not in the workspace proxy allowlist" sentence + the canonical **https** entry that would admit it; the http twin of an allowlist entry never unlocks its https sibling; allowlisted https origins/ports/wildcard subdomains fall through untouched; wildcard apex and the SSRF floor refuse at the view exactly as at the listener; null/empty entry sets are the fail-closed reading; a divergence-guard row pins the view gate and the forward CONNECT gate to identical verdicts), `ProxyAllowlistGateAgreementTests` extended (the view gate joins the four-gate matrix: https-only scheme rule on top, verdicts identical to the ES fetch gate / forward gate / leaf-mint gate incl. the VMS floor and both entry shapes), `ProxyForwardConnectionTests` (root-cause pin: the refused CONNECT still gets the complete 403 + friendly page written as the CONNECT answer body on the wire - Chromium drops that body, the live half is the SC-5 record 7 page; the fail-closed CONNECT refusal and the #69 block report stay untouched) | See SC-5 below - **the https start-URL row PASSED live 2026-11-03 (record 7, run `20261103-020728`)**, both-machine drop `E17995E1…5479BDFC`: the refusal page renders committed AT `https://neverssl.com/` with all #66 assertions, the browser's own log carries the `view-level https refusal ... No CONNECT was opened` marker, and the ES diagnostics ring records the origin; the http sibling row re-passed green in the same run set (`20261103-020505`). The ws/wss CONNECT asymmetry (native Chromium page) is documented in README |
| Server-side deployment + rollout docs for proxied connections (#59) | #59 | `scripts/deploy-event-server-plugin.ps1` refuses a drop whose `plugin.def` lacks the `Service` environment before touching the server; stop-wait/stop, copy, per-file SHA-256 verification against the source, restart-wait, and the two rollout follow-ups (client restart, fetcher-ready marker) are printed by the script itself | See DP-1 below: the script validated live end-to-end against the Event Server machine (11 files hash-identical, `Proxy fetcher ready` after restart); README/log-path/version claims checked against the lab; fresh-both-machine blocked-egress smoke PASSED with both machines at the same hash-verified drop |
| wss:// websockets tunnelled through a terminated TLS tunnel (Event Server dials a validated TLS leg, replays the rebuilt handshake, admits only 101) | #60 | `ProxyForwardPolicyTests` (the entry scheme IS the posture: an in-tunnel upgrade GET against an allowlisted https origin — or absolute wss:// against an https entry — becomes `WebSocketTunnel`, never 501; an http twin alone is 403 for the upgrade; the `WebSocketTunnelRefused` posture is deleted outright), `ProxyForwardConnectionTests` (in-tunnel upgrade opens the byte tunnel, the target's verbatim 101 rides back ON the terminated tunnel, then both directions pump decrypted frames over the tunnel stream; open failure renders an explicit 502 page on the tunnel and the tunnel keeps serving), `ProxyTunnelServerTests` (in-process `TlsWsTarget` with a self-signed SAN certificate: an https-scheme tunnel target gets a TLS leg authenticated against the target host before the handshake is replayed, 101-only admission, frames echo over both pumps; untrusted chain → explicit `TlsValidation` error naming the origin and the Event Server as the failing side; production resolver is null = SslStream full chain validation), `ProxyWebSocketHandshakeTests` (rebuilt handshake pins the https origin's Host for https-scheme tunnel targets) | See SC-6 below - **both live rows PASSED 2026-11-01** (runs `20261101-174151` + `20261101-174657`): with the probe certificate imported into the Event Server's trust store, a wss-echo page in a proxied Smart Client browser shows `WS-OK:<token>` while the wss server journals TLS-HANDSHAKE + WS-HANDSHAKE + WS-ECHO; the fail-closed row's target journals the TLS leg dialed + negotiated but never the upgrade - the Event Server refused the chain there |
| wss tunnel legs honour the per-target accept-untrusted override (the #56 flag reaches the tunnel leg, scoped to its own origin) | #61 | `ProxyServerAllowlistTests` (`TunnelTlsValidationCallback`: null = SslStream default full-chain validation for every unflagged posture - no flag, explicit false, the http twin of another origin, a never-resolved closed gate - while the flagged entry's callback accepts chain errors; the flag rides wildcard entries to subdomains with scheme/port exactness, never the apex or another port), `ProxyTunnelServerTests` (allowlist-WIRED resolver end to end: the self-signed `TlsWsTarget` opens and pumps frames only when its entry carries the flag; without it the same wiring fails with the SAME explicit named-side `TlsValidation` error; two self-signed targets with ONE flag → the flagged tunnel opens, the neighbour's TLS dial never replays the upgrade - the flag is never a global pass), `EventServerTunnelTlsWiringGuardTests` (the ES constructs the tunnel server with the resolver, never the null #60 slot; the resolver delegates to the single scoped allowlist lookup; the fetcher source never touches `ServicePointManager`) | See SC-6 below - **override row PASSED 2026-11-02** (run `20261102-113540`), and the two #60 twin rows re-passed on the same drop (fail-closed `20261102-114705`, trusted-echo `20261102-115053`) |
| MC workspace config tab shell (Settings \| Proxy \| Diagnostics \| Status; Status and Diagnostics behind the SecurityActions edit-rights gate) | #74 | `WorkspaceTabShellGuardTests` (the XAML declares exactly the four tabs in shell order with stable AutomationIds; Status and Diagnostics each default `Collapsed` so the gate fails closed before any fill; EVERY pre-existing settings/proxy control still lives on the Settings tab under the same AutomationId the ux probes read; no placeholder tab is left since #84 - both live read tabs are guarded down to their buttons' exact handlers instead), `WorkspaceTabPolicy` decision table (Status AND Diagnostics render only on an explicit `Granted` - `Denied`/`LookupFailed` hide the whole tab) | See MC-11 below - **admin half live row PASSED 2026-11-02** (run `20261102-012719`): four tabs render on the live MC with Status granted to the admin, walking the tabs never enables Save, and the Settings fields read back identically; the View-only denial half stays manual |
| Proxy diagnostics substrate (block reports, session lifecycle, Event Server aggregation, status query) | #69 | `ProxyDiagnosticsAggregatorTests` (blocked ring dedup + accumulating counts + recency eviction at the 200-origin cap, new-entry-only transition signal; session open/heartbeat/close and the idle sweep; fetch/tunnel traffic counters and req/min; workspaces stay isolated), `ProxySessionRegistryTests` (client-side per-view registry: open/close, heartbeat payloads, bounded), `ProxyBlockReporterTests` (report carries the workspace identity + canonical origin; blank or workspace-less reports drop; a throwing send seam never reaches the browser flow), `ProxyDiagnosticsWireTests` (every diagnostics payload round-trips as one JSON string; an unset session is an explicit null member; a frame shaped by another build is refused, never half-bound), `ProxyForwardConnectionTests` (each client-visible refusal reaches the reporter — local 403, tunnel-open fault, dead fetch channel — while an ES-answered refusal is never re-reported, so counts do not double), `ProxyFetchContractGuardTests` (four new payloads stay plugin-namespace `[Serializable]` public-field types inside the whitelist; the new ids declared in `PluginDefinedMessageIds`) | See ES-2 below - **load half observed live 2026-11-02**: the restarted Event Server logs `Proxy fetcher ready (..., diagnostics=WebContent.Proxy.BlockReport/WebContent.Proxy.SessionEvent/WebContent.Proxy.StatusRequest)` on a drop hash-identical to the Smart Client machine's. The ingress markers (session open, block recorded) need a proxied Smart Client session, which only an interactive desktop can drive |
| MC Status tab: live proxy sessions (polling tied to the open tab, read-only, role-gated by the #74 grant) | #76 | `ProxyStatusViewTests` (failure postures: a lost query is never rendered as an empty table, the Event Server's own code survives, a blank code falls back to `ServerUnavailable`; zero sessions is an honest success with no rows; rows arrive as pre-formatted display strings with `(unknown)` placeholders; rate differencing over the ES's cumulative counters needs two sightings, impossible deltas (non-later snapshot, backwards counters, unknown ids) answer zero, `Reset` drops the baseline; culture-invariant byte/rate/per-minute/duration formatting incl. NaN/Infinity/negative clamps), `WorkspaceTabShellGuardTests` (the Status tab is no longer a placeholder: exactly one `IsReadOnly` DataGrid with add/delete off, every button on the tab wired to `OnStatusRefresh`, zero configuration input controls - walking it can never dirty the pane; probe-selectable surface ids pinned) | See MC-12 below - **live row PASSED 2026-11-02** (run `20261102-074407`): opening the Status tab starts the pull loop (MIP.log markers at cadence), this machine's proxied Smart Client view renders as a session row within the cadence, leaving the tab flattens the markers, re-selecting resumes, and Save never enabled through the whole probe |
| MC structured allowlist grid on the Proxy tab (scheme/host/port/accept-untrusted rows, inline refusal, wildcard banner) | #81 | `ProxyAllowlistEditorTests` (row validation IS the shared grammar — mid-label wildcard, `*x`, `*.`, path, inner space, port out of range, port-with-blank-host all refused while exact hosts/IPv6/`*.tail`/the lone `*` pass; blank rows are not entries; canonicalization of case and scheme-default ports; only the lone `*` is the full-host hatch; both stored shapes render forever with corrupt records dropped and counted; a legacy `host`-shape workspace opens and saves back to structured `pattern` records without losing entries or flags; invalid rows refuse the build BY NAME and never vanish; duplicate rows collapse with any flagged duplicate flagging the entry; touching the flag never changes validation), `WorkspaceTabShellGuardTests` (the Proxy tab carries the grid with the six pinned columns in order and every per-row AutomationId, the warning banner starts `Collapsed`, rows are added/removed only by explicit buttons with `CanUserAddRows`/`CanUserDeleteRows` off, the proxy section is asserted ABSENT from the Settings tab, and neither `txtProxyTargets` nor any admin-facing `accept-untrusted` token survives in the page or its strings) | See MC-14 below |
| Proxy allowlist wildcards: ONE shared matcher for all three gates, structured entry shape, wildcard SSRF floor | #73 | `ProxyPolicyTests` (wildcard grammar: leftmost-label `*.tail` + the lone `*` hatch only - mid-label wildcards, `*x`, `*.`, bad labels, non-http schemes rejected; matching: subdomains covered, apex never, label-boundary safe, scheme/port exact, case-insensitive; SSRF floor: literal IPv4 (dotted-quad + numeric shorthand), IPv6 (bracketed/colon), `localhost`, and the VMS's own hosts are never wildcard-matched while explicit exact entries may still name them; seeded pseudo-random host/IPv4 property pins over 300+200 rounds), `ProxyAllowlistGateAgreementTests` (the same (entries, request-origin) matrix drives the Event Server fetch gate, the client forward gate, and the leaf-mint gate to identical verdicts; legacy `host` records and new `pattern` records gate identically), `ProxyServerAllowlistTests` (pattern and legacy shapes gate identically at the ES; VMS floor fed through a resolver seam and kept across resolver faults; the accept-untrusted flag rides wildcard entries under the same floor), `ProxyForwardPolicyTests` (client gate consumes the shared matcher in every posture), `ProxyWorkspaceProfileTests` (pattern entries and the floor hosts travel together through the seeded profile property), `ProxyConfigurationTests` (the stored entry gains `pattern`; readers accept BOTH shapes forever, no migration write-back - a read never rewrites the stored shape), `WorkspaceTabShellGuardTests` (boolean-attribute pins learned the hard way in the MC-13 live walk) | See MC-13 below - **live rows PASSED 2026-11-02** (run `20261102-073648`): wildcard/hatch lines save and read back verbatim through the live Management Client (structured `pattern` shape round-trip); `http://*.neverssl.com` admits `http://online.neverssl.com/` (a `HTTP 200 - 3961 bytes` banking in run `20261102-071520`) while the apex refuses `NotAllowed` in 0.0s; the `http://*` hatch admits the apex (200 in run `20261102-073010`) yet the literal IP and the VMS's own host both refuse `NotAllowed` in 0.0s - refused before any socket opens; allowlist restored to its four production lines with verified readback |

| Rule triggers: a matched proxied fetch raises the fixed `WebContent proxy request` analytics event on the Event Server bus | #65 | `ProxyTriggerPolicyTests` (url glob grammar: absolute or the lone `*` hatch only; scheme http/https/`*`; host exact — empty labels refused — leftmost-label `*.tail`, or lone `*`; port explicit 1-65535 or absent = the url's own scheme default, so an explicit non-default port never matches an absent-port row; anchored path glob where `*` spans `/` and `?` is exactly one char, query/fragment never participate; method verb tokens; status class `1xx..5xx` or exact 100-599; seeded-Random property pins over the glob engine with a wildcard-safe alphabet on the pattern side), `ProxyTriggerMatcherTests` (store closed before the first resolve; rows evaluate against their own workspace only; first matching row wins — one fetch raises at most one event; absent sections tolerated silently; a refused row is audited by reason without taking down the workspace's other rows; a resolver failure after a good resolve keeps serving last-known-good rows; `Prime` logs what the configuration actually loaded), `ProxyConfigurationTests` (raw `proxyTriggers` rows survive the serialized roundtrip — grammar is decided only on read; a pre-#65 definition reads `ProxyTriggers=null` with no error; empty `method`/`status` members optional; a workspace without rows never grows an empty section), `ProxyFetchWireCodecTests` + `ProxyDiagnosticsWireTests` (#65-revised skew contract: the request wire's members are explicitly optional on read so an older Smart Client's frame decodes as an unattributed fetch, while a frame that names no url stays an unusable null; the tunnel wire keeps the strict inferred contract), `EventServerTriggerWiringGuardTests` (event header carries ID/Timestamp/Message=the one fixed event type with per-row context in CustomTag; the raise goes through `EnvironmentManager.SendMessage` with a TYPED platform `AnalyticsEvent` + `RelatedFQID` — never raw `TransmitMessage`, which the live transmit layer refuses for system Command ids; the plugin observes `Server.NewEventIndication` and detaches at Close; the store is primed in `Init` and logs; the self-test probe stays behind the env/flag switch; attribution chain: stamped request → diagnostics session map → fail closed) | See ES-3 below - **live gate PASSED 2026-11-02** (run `20261102-141512`, drop `WebContent.dll` SHA-256 `2B974839…`): the flag-file probe raised with a real workspace source and the SAME event id came back through the plugin's NewEventIndication observer (`d9ea7200…` on all three markers); the two refusal postures were learned live - raw `TransmitMessage` of a system Command id (`Discarding message Server.NewEventCommand`) and a sourceless event (`ArgumentException: Invalid event`) |
| MC exports the deployment proxy CA certificate over MIP messaging (provenance-independent, read-only, size-capped single frame) | #75 | `ProxyCaExportTests` (responder postures: authority present → Success with base64 DER + thumbprint + validity window; absent → `NoAuthority` with the actionable "does not exist yet" sentence, never an empty answer; expired/not-yet-valid and malformed entries refused by code, never silently skipped past; the fixed-CN finder opens `LocalMachine\Personal` with `ReadOnly | OpenExistingOnly` only — a source guard pins that no write/create flag can enter the export path — and several copies prefer the private-key one; wire codec round-trips the payloads as one JSON string with optional-member tolerance; correlation deadline silence → `ServerUnavailable`; the MC view builder NEVER yields a save for a success that carries no bytes — it renders the error copy instead; size caps: a REAL 2048-bit CA's DER frames under `MaxAuthorityDerBytes` (8 KiB) and its full response wire form stays under `MaxResponseWireChars` (16 KiB), the caps themselves pinned far under the proven ~87K-char single fetch-chunk headroom), `ProxyFetchContractGuardTests` extended (both new payloads stay plugin-namespace `[Serializable]` public-field types; `WebContent.Proxy.CaExportRequest`/`…Response` declared in `PluginDefinedMessageIds`; ids never end in "Command" so SecurityLevel.Normal clients can send) | See MC-15 below - **live row PASSED 2026-11-02** (run `20261102-154554`): the button answers for a CA this build did NOT create (legacy-script provenance, thumbprint `E4D254D5…`), and the `.cer` the Save As dialog writes is byte-identical to the certificate in the Event Server machine's live store |
| MC Diagnostics tab: recently-blocked origins from the ES ring, one-click allowlist promotion into the Proxy grid (tab-gated polling on the Status query pair, same Manage gate, fail-closed) | #84 | `ProxyDiagnosticsViewTests` (failure postures mirror the Status tab - a lost query is never an empty table, the ES code survives, unknown workspace and no-channel read their own sentences; zero blocked origins is the honest empty message naming the in-memory/reset semantics; rows arrive pre-formatted newest-first with an origin-ordinal tiebreak, hits floored at 1, future timestamps clamp to "just now", never negative; the allowlist annotation runs the SAME shared `ProxyPolicy.IsRequestAllowed` matcher as the three gates - a wildcard entry or the VMS floor hosts marks a row already covered and drops its button; `CertUntrusted` rows never get the button, every other certified posture does; an unparsable origin names itself and refuses promotion), `ProxyAllowlistEditorTests` promotion section (the promotion IS a grid row: exact scheme+host+port, accept-untrusted never set, default ports canonicalise, dedupe is by canonical entry not typing, junk outside the exact-entry grammar refuses without touching the grid, the promoted row saves through the ordinary build as a structured record, `ValidCanonicalEntries` covers unsaved edits and skips broken rows), `WorkspaceTabShellGuardTests` (Diagnostics starts `Collapsed` + the identical Granted-only gate theory; the live tab carries exactly one read-only grid, its buttons carry only the two pinned handlers, the promote button hides via its `CanPromote` converter rather than disabling, no configuration input control anywhere on the tab, and the in-memory/resets copy is pinned as prose) | See MC-16 below - **live row PASSED 2026-11-02** (run `20261102-182628`): a live Smart Client refusal renders `http://172.16.128.210:18131 | not allowed by administrator` within the cadence while only the Diagnostics tab arms the marker loop; one click lands the exact origin in the Proxy grid UNSAVED, annotates the row `on allowlist`, hides its own button, arms Save, and the ordinary save discipline (audit line + reloaded-pane readback) stores it; after a pane reload the row re-annotates from stored config; allowlist restored with verified readback |

| Per-workspace proxy response cap (5 MB default, hard 20 MB ceiling, clamp-on-read, MC Proxy-tab field) | #85 | `ProxyResponseCapStoreTests` (never-resolved store / unknown workspace / absent member / malformed 0-negative all answer the 5 MB default, never a refusal; a stored cap answers verbatim; a past-ceiling stored value clamps to 20 MB and gets exactly one audit line naming the workspace per refresh; a resolver failure after a good resolve keeps last-known-good caps; flap resilience across alternating refreshes; the 60 s window means one resolve per burst; `ForResponseCapBytes` factories clamp identically), `ProxyConfigurationTests` cap section (pre-#85 definitions read `ProxyMaxResponseBytes=0`; a stored value round-trips VERBATIM even past the ceiling - the clamp is on every read, never a silent rewrite at save time; the default stores 0 and the blob never grows the member - `EmitDefaultValue=false`), `ConfigResilienceTests` cap section (blank/whole-MB-inside-1..20 accepted, everything else refused BY NAME with the rejected text - no silent zoom-style clamp in the UI; MB→bytes mapping stores 0 for the default; bytes→display clamps honestly both ways; the admin ceiling and the contract's `MaxAllowedResponseBytes` are pinned in lockstep), `ProxyFetchCorrelationTests` (the client receive guard bounds allocation against the 20 MB PROTOCOL ceiling, not the old 5 MB default - a hostile `ceiling+1` declaration still errors, while a 6 MB framed answer reassembles), `WorkspaceTabShellGuardTests` (the Proxy tab carries `lblProxyResponseCap`/`txtProxyResponseCapMB`/`lblProxyResponseCapHint`, the field's explicit AutomationId and `OnProxyResponseCapTextChanged` handler pinned) | See MC-17 below - **live rows PASSED 2026-11-02** (run `20261102-210935`): the default posture refuses a REAL 10 MB download with `TooLarge ... exceeded the 5242880 byte response cap`; saving 12 MB flips the SAME download to `HTTP 200 - 10485760 bytes fetched` inside the 60 s ES refresh window (which also proves the raised client receive guard); `99999` refuses the whole save by name with zero new cap-audit lines while the ES keeps serving the stored cap; restoring 5 re-arms the refusal |
| Per-workspace proxy header policy: add/override/strip rows on BOTH fetch legs, forbidden-header guardrail enforced at save AND at runtime | #86 | `ProxyHeaderPolicyTests` (the canonical forbidden set: exact hop-by-hop/Host/Content-Length/Transfer-Encoding/TE/Upgrade names + every `Proxy-*` prefix, case-insensitive; RFC 9110 token-name grammar, CR/LF in a value refused; the apply model: add/override replaces all same-name entries case-insensitively, rules apply in configuration order with LAST rule winning, forbidden rules never applied even when hand-fed past the store), `ProxyHeaderPolicyStoreTests` (60 s refresh / last-known-good / Prime-at-Init; forbidden and malformed stored rows refused with one audit line per refresh naming workspace + raw text; corrupt blobs = empty policy, never a refusal), `ProxyHeaderEditorTests` (the MC build refuses the WHOLE policy by name with nothing partial returned; strip-without-value is valid; strip does NOT unlock a forbidden name; Load drops unrenderable stored rows counted for the log), `ProxyConfigurationTests` header section (round-trips keep forbidden names VERBATIM on the wire - readers refuse, nobody censors; empty grids store no section, pre-#86 wire shape preserved), `EventServerHeaderPolicyWiringGuardTests` (request rules applied before the fetch, response rules before the frame and only for a real upstream status, store Prime at Init, all three block sets derived from the one canonical set), `WorkspaceTabShellGuardTests` (both grids on the Proxy tab: name-keyed lookups, per-AutomationId column pinning for both legs, the PropertyChanged binding guard across every Proxy-tab grid) | See MC-18 below - **live rows PASSED 2026-11-02** (run `20261102-224611`): a two-rule policy per leg stores through the audit line AND order-sensitive reloaded-pane readback; four forbidden-row cycles (request+response, add+strip) each pin the live Problem text, the notice naming the row verbatim, the refusal marker, and zero new audit lines; empty grids store the empty policy |
| Per-workspace proxy rule triggers: MC Proxy-tab "Rule triggers" grid (URL/method/status), whole-save grammar guardrail sharing the Event Server's `ProxyTriggerPolicy` matcher grammar | #78 | `ProxyTriggerEditorTests` (blank rows skipped not refused; url-required / mid-host-wildcard url / out-of-grammar status each refused by code; the WHOLE build refused by name with nothing partial returned; configuration order preserved and null members kept compact (any-method `*` and blank status store as null); stored rows with unrenderable method verbs dropped at Load and counted for the log; the fixed event type and the method dropdown set pinned to the matcher's constants; the grid's validation and the Event Server's matcher ACCEPT AND REFUSE THE SAME ROWS - `ProxyTriggerPolicy`'s normalizers are the single grammar both call), `WorkspaceTabShellGuardTests` (the trigger grid + Add button + hint carry their AutomationIds on the Proxy tab, not the Settings tab; every row's Event cell is a non-editable TextBlock; the XAML method array never drifts from `ProxyTriggerEditor.ProxyTriggerMethods`) | See MC-19 below - **live rows PASSED 2026-11-03** (run `20261103-031310`): a two-row trigger list (an exact-status row + an any-method wildcard-host row) stores through the audit line AND order-sensitive reloaded-grid readback with the fixed event type shown in the read-only Event column on every row, typed AND reloaded; three invalid-row cycles (mid-host-wildcard url / status `600` / a method with no url) each pin the live Problem text, the notice naming the row verbatim, the `invalid proxy trigger row(s)` MIP.log marker, and zero new audit lines; an emptied grid stores no trigger section (`0 row(s) []`) |
| Installer upgrade with Milestone clients running (MSI force-closes Smart Client / Management Client instead of failing 1603 on locked plugin dlls) | #70 | `build.ps1` structural guard (parses `wix-wid.xml` out of the versioned `.wixpdb`: the `Wix4CloseApplications_*` action is scheduled after InstallInitialize and before RemoveFiles/InstallFiles, RemoveExistingProducts is after InstallFiles, and the three kill targets — `Client.exe`, `VideoOS.Administration.exe`, `VideoOS.OAuth2TokenGenerator.exe` — are in the `Wix4CloseApplication` table) | See IN-1 below - **all live rows PASSED 2026-11-03** on the VMS machine with the clients launched in a real interactive session and `WebContent.dll` proven LOCKED first: uninstall, fresh install, and an upgrade — each with `Client.exe` + `VideoOS.Administration.exe` running — answered exit code 0, killed the clients, and left a hash-verified drop |
| OS-configured (WinINET) proxy inheritance for non-proxied views, and the deliberate contrast for proxied views (loopback pin wins; no admin-facing proxy-URL setting exists because the OS owns that layer) (#67) | #67 | `ProxyBrowserArgumentsTests` (the pin itself: every scheme routed to the loopback listener, `<-loopback` bypass off - nothing falls back to the system path); the inheritance claim is a live-host fact, pinned by the SC-8 probe rows below (`ScOsProxyInheritanceProbeTests`: WinINET snapshot/apply/restore, a logging forward-proxy harness on loopback, live `Win32_Process` command-line reads of the plugin's browser processes) | See SC-8 below: routing OFF + the per-user WinINET proxy pointed at the harness - the non-proxied view renders the harness marker page for an RFC 2606 `.invalid` host (which can never resolve, so a served page can only have ridden the OS proxy) with the browser's absolute-form GET in the journal and NO proxy switch on any browser command line; routing ON + the SAME OS proxy + the SAME url - the plugin's 403 page renders, the harness journals nothing for the origin, and the live browser command lines carry `--proxy-server=http=http://127.0.0.1:<listener>;...` plus `--proxy-bypass-list=<-loopback>`. **BEAST 2026-11-03: both rows PASSED** (`20261103-035256`, `20261103-040006`) |

## Manual host checks

Run after deploying new build output (`Invoke-psake -taskList Build, DeployLocally, DeployToVms`;
the deploy tasks kill the `client`/`VideoOS.Administration` hosts and restart the Event Server
service, and the local copy needs no elevation). Verify each deployed file by hash, not timestamp.

### MC-1 View open/close/reopen cycles (#10, #15)

1. Open a Web Content workspace; note the browser id in `Initializing view item`/`Adding WebView to panel`.
2. Close the view item (`Closing view item`), reopen it, repeat 5×.
3. Expected: the same browser id is reused (`Adding WebView to panel for browser <same id>`); the page behaves normally once per reopen (single title load, single address bar); no growing `Closing browser`/`Disposing browser` churn per cycle.
4. Failure signature: duplicate page interactions after reopen, or a new browser id per reopen without an isolate action.

### MC-2 Configuration clamp at runtime (#11)

1. In Management Client set a workspace zoom to an extreme (UI allows range; simulate legacy corruption by importing an exported settings pack with `ZoomLevel` outside 25–500, or rely on the automated pin for the corruption path).
2. Expected: Smart Client renders with zoom clamped to 25–500; no Smart Client exception dialog; no `Failed to load workspace properties` log.

### MC-3 Address refusal (#14)

1. Type `javascript:alert(1)` or `not a valid url` into the address bar and press Enter.
2. Expected: navigation does not happen; log marker `Rejected invalid address input for view item`.
3. Feed a workspace a corrupted saved address; at runtime the marker is `Refused to navigate browser <id>: invalid address`.

### MC-4 Provisioning recovery after transient failure (#12)

1. Start the Smart Client while the Management Server/database is briefly unavailable (or restart it mid-login).
2. Expected log once the read fails: `Workspace provisioning attempt failed (reason=..., attempts=N, retry=Xs)` with exponentially growing `retry`, capped at 64 s.
3. Expected after the server recovers, without restarting the client: `Recovered workspace provisioning and loaded N workspaces.` and the tabs appear.
4. A permission-only-empty startup logs `No workspace tabs were provisioned during startup` once, then retries (max 3 empty retries).

### MC-5 No licensing surfaces (#13)

1. Load the plugin in Smart Client and Management Client on a host with any license state.
2. Expected: no trial banner, no purchase link, no license item in the Management Client tree, no license warnings in MIP logs. The MIT-licensed plugin never queries or reserves licenses (pinned statically by `PluginLicenseHygieneTests`).

### MC-6 Long-session hygiene (#15)

1. In one Smart Client session, navigate between views/workspaces containing Web Content for ~30+ minutes with repeated open/close.
2. Expected: `client` process private working set plateaus (no steady climb across cycles); no `ExceptionHandler` entries from stale callbacks after close; audio notification clicks act exactly once (`SmartClientMessageButtonClickedIndication` receiver registered once per live browser, unregistered on close).

### MC-7 Workspace session state restore (#9)

1. Open a Web Content workspace tab; navigate away from the home URL to another page on the same tab.
2. Switch to a different workspace tab, then return. Expected: the browser shows the last visited page (not the home URL).
3. Restart the Smart Client and open the same workspace. Expected: the browser starts at the admin-defined home URL (session state is in-memory only). If the workspace has `RememberLastAddress` on, the pre-existing opt-in instead restores the persisted LastAddress across restarts — that is the admin's explicit choice, not session state.
4. In Management Client, edit the workspace definition (e.g. change the home address), then log in again. Expected: the browser starts at the **new** home URL even if `RememberLastAddress` is on; MIP log marker: `saved navigation state reset so the browser starts at the new home address`.
5. A standalone (manually placed) Web Content view item keeps the old behavior: no session tracking, restoration only when `RememberLastAddress` is on.

### MC-8 Configuration error notification on broken tab (#9)

1. Create (or take) a workspace whose `WorkspaceConfig` property is unusable — corrupt the JSON directly in the database (or clear the property and the legacy Address/HomeAddress properties of a workspace item).
2. Restart the Smart Client. Expected: the workspace tab **still appears** (no silent disappearance) and shows a WorkSpaceNotification warning whose text names the workspace and says it "has a configuration error" (corrupt-JSON vs no-browser-config wording).
3. Expected log marker in MIPLog.txt: `Configuration error for workspace '<name>'`.
4. Expected browser content: the tab renders the fallback home page. Open the workspace in the Management Client: a **red banner** appears on the details page naming the configuration defect, and the **Save button becomes enabled on its own** (no need to nudge any setting — ticket #27 dirty-after-fill); one Save rewrites a clean definition, and re-logging in clears the Smart Client notification. MC log marker: `loaded with a configuration error (MalformedJson)`.

### MC-9 Proxy config roundtrip and refusal (#52)

1. In Management Client, open the workspace's **Proxy** tab (#81), enable "Route web traffic through the VMS server proxy" and add one target row (scheme `https`, host `10.0.0.5`, port `8443`); Save.
2. In Smart Client, open the workspace tab's properties page. Expected: the Proxy section reports the enabled state and lists the allowlisted target (canonical form).
3. Toggle the proxy off, Save, and check the target rows are still there (off-toggle preserves allowlisted targets; MC-9 pin for the wipe bug found in review).
4. With the proxy enabled, type an invalid entry into a grid row (a mid-label wildcard `foo.*.bar.com`, or a valid host with port `99999`) and Save: the row is flagged inline, the save is refused with a notice naming the row(s), and the rows are not dropped; MC log marker: `Rejected save for workspace '<name>' (FQID: ...): invalid proxy target row(s): …`. Enabled with no target rows at all is refused likewise (`proxy enabled but no target allowlisted`). MC-14 drives the grid half of this live.

### ES-1 Event Server fetcher load and answer (#53)

1. Deploy the new drop with `Invoke-psake -taskList DeployToVms` (it stops and restarts
   `MilestoneEventServerService` around the copy; the service only loads MIP plugins at service
   start; plugin.def gained the `Service` load environment).
2. Expected marker in the Event Server's `Logs\MIP.log` (ProgramData for the Event Server):
   `Proxy fetcher ready (concurrency=3, request id=WebContent.Proxy.FetchRequest)`. A startup
   failure logs `Proxy fetcher failed to start: ...` instead.
3. No `MessageCommunicationSettings.config` whitelist edit is needed — and since #54 no custom
   payload type is ever bound by a host process: every wire payload is a plain JSON STRING
   (`ProxyFetchWireCodec`). The platform rehydrates `ExternalMessage.Data` via
   `new DataContractSerializer(Type.GetType(DataClass))`; Smart Client's plugin loading leaves
   `WebContent.dll` invisible to that type resolution, `Type.GetType` returns null, the platform
   logs `ExtenalMessage.Deserialize ():Unable to deserialize class:... , Exception:Value cannot
   be null.` and hands the receiver `Data == null` — a silent black hole (MC and the Event Server
   resolve plugin types fine, which is why only Smart Client failed). Never put plugin types on
   the wire.
4. Response routing is unicast: the fetcher answers on `Message.ExternalMessageSourceEndPoint`,
   which the messaging layer fills with the requester's endpoint FQID on every received message.
   (The receive callback's `source` parameter is null for our requests — it only carries an
   explicitly passed identity. Answering to a null destination instead filter-routes response
   frames to EVERY client subscribed to `WebContent.Proxy.FetchResponse`: refusals time out
   silently on the requester, fetched bodies leak to unrelated hosts, and processes without
   WebContent.dll log `Unable to deserialize class:...ProxyFetchResponsePayload`. This was the
   #54 live-host failure: both clients timed out despite a healthy fetcher.)

### ES-2 Event Server proxy diagnostics ingress (#69)

1. Deploy both halves from one drop (`Invoke-psake -taskList Build, Deploy`). Since #65 the fetch
   REQUEST payload tolerates members missing from an older Smart Client's frame (it decodes as an
   unattributed fetch instead of dying), but every other wire payload is still strict about its
   member set, so a Smart Client plugin and an Event Server plugin from very different builds can
   still fail to decode each other's frames — ship from one drop. Restart desktop clients
   afterwards: an Event Server restart disposes every running client's `MessageCommunication`.
2. Load marker in the Event Server's `logs\MIP.log`:
   `Proxy fetcher ready (concurrency=3, request id=WebContent.Proxy.FetchRequest, diagnostics=WebContent.Proxy.BlockReport/WebContent.Proxy.SessionEvent/WebContent.Proxy.StatusRequest)`.
   The diagnostics clause only appears once the four new ids in `plugin.def` are accepted and all
   three filters registered. **Observed 2026-11-02** on the deployed drop (`WebContent.dll` SHA-256
   `21365D5DBE27…` hash-identical on both machines).
3. Ingress markers, produced by any live proxied view:
   - `Proxy diagnostics session open: workspace='<item id>' user='<platform identity>' machine='<client machine>'`
     when a proxied view's loopback listener starts — the user comes from the platform endpoint
     identity table, never from the client's own claim;
   - one `Proxy block recorded: workspace='<item id>' origin='<canonical origin>' reason=<category>
     client='<machine>'` per NEW blocked origin; the dedup refreshes that follow stay quiet so a
     refusal storm cannot bury the log.
   The row that produces them on demand:
   `Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~ScProxyErrorProbeTests.ProbeProxiedStartUrlOutsideAllowlistShows403Page' }`
   — the probe makes an off-allowlist origin the workspace home, so the block is a real client-side
   gate refusal, and it restores the workspace home address afterwards.
4. **Not yet run (2026-11-02):** the agent session that shipped #69 cannot drive FlaUI — every
   input call dies with `Win32Exception: Access is denied` on a non-interactive desktop — so the
   step 3 markers are still unverified live. The status-query answer (a
   `WebContent.Proxy.StatusRequest` answered with a snapshot) has its sender since #76 landed in
   the Management Client drop (2026-11-02): the Status tab sends one query every ~3 s while it is
   the selected tab and logs one MC marker per pull (`Proxy status snapshot for workspace '...'
   answered: N live session(s), M blocked origin(s).` - the blocked clause joined the line with the #84
   Diagnostics tab; the MC-12 probe counts the unchanged prefix). The MC↔ES round trip went live with MC-12's probe (run
   `20261102-074407`): the deployed ES answered the tab's queries and this machine's proxied view
   rendered as a session row; its remaining semantics (unknown workspace answers `Success=false` +
   `BadRequest`, client-side deadline silence completes as `ServerUnavailable`) stay pinned by the
   unit suites.


### ES-3 Rule-trigger event raise and observation (#65)

1. Deploy the Event Server half (`Invoke-psake -taskList Build, DeployToVms`) and verify the drop
   by hash. Restart desktop clients afterwards (the service restart disposes their
   `MessageCommunication`).
2. Load markers in the Event Server's `logs\MIP.log`: the ready line now ends with
   `triggers=WebContent proxy request`, preceded by `Workspace rows refreshed: N row(s) across
   M workspace(s).` — the eager prime of the trigger store. `0 row(s) across 0 workspace(s)` is
   the expected state until the Management Client trigger editor (#78) writes rows; the matcher
   stays closed and simply never fires.
3. Self-test probe (the raise-path round trip on demand): create a `trigger-selftest.flag` file
   next to the deployed `WebContent.dll` and restart the service. Expected, in order:
   - `Analytics event raised: <guid> "WebContent proxy request"` — the documented
     `EnvironmentManager.SendMessage` + typed-`AnalyticsEvent` route (live-proven: raw
     `MessageCommunication.TransmitMessage` of a system Command id is refused by the Event Server
     MessageBroker, `Discarding message 'Server.NewEventCommand' as sending Indication and Command
     messages are not allowed`, and surfaces to the plugin as `Unable to Transmit`);
   - `Trigger self-test probe raised: <guid> "WebContent proxy request" source=<workspace name>`;
   - `Proxy trigger event seen on the bus: <guid> "WebContent proxy request" (selftest)` — the
     SAME guid as the raise line, arriving through the plugin's `Server.NewEventIndication`
     receiver after the Event Server's own event processor accepted the event. A sourceless
     analytics event does NOT survive that processor (`ArgumentException: Invalid event`), which is
     why the probe carries a real workspace item as the event source, exactly like a match event.
   Remove the flag and restart again: the markers must go silent.
4. **Observed 2026-11-02 (run `20261102-141512`):** all three markers with
   `d9ea7200-5c25-4b65-ab74-5d23613a31f4` on a drop hash-identical to the build output
   (`WebContent.dll` SHA-256 `2B97483955E8…`); clean flagless restart at `20261102-141621` logged
   only the prime + ready lines.
5. **Not yet run:** the full end-to-end (a served fetch matching a stored row, and an
   administrator's Analytics Event definition + rule reacting to it) waits on the #78 trigger
   editor; the event definition/rule walk in Management Client stays a manual row then.

### MC-10 Management Client proxy-fetch harness (#53, hardened by #54)

1. Restart Management Client with the new drop. Open a Web Content workspace definition — the
   proxy section now has a "Test proxy fetch" row (URL textbox + button).
2. Enter a URL whose origin is allowlisted on some proxy-enabled workspace (e.g.
   `http://10.0.0.5:8080/`) and click "Fetch via Event Server". Expected: status line shows 200
   with body size and duration within a few seconds.
3. Failure cases: enter an origin no proxied workspace allowlists (e.g. `https://www.example.com/`)
   → `NotAllowed` refusal, Event Server log `Proxy fetch request ... refused: The origin '...' is
   not in the proxy allowlist of any proxied workspace.` (#54 server-side gate). Enter
   `http://10.255.255.1/` on an allowlisted origin (unreachable) → the result shows `Unreachable`
   after the ~5 s connect budget, and the Event Server log gains `Proxy fetch failed
   (code=Unreachable, target=...)`. Enter `not a url` → `BadRequest` refusal logged as
   `Proxy fetch request ... refused: the url was not an absolute http(s) uri`. Successes are
   deliberately not logged (fetches become high-frequency once views use the proxy).
4. If the Event Server has no plugin loaded, the harness shows the not-connected message without
   sending anything.

### MC-11 Workspace config tab shell (#74)

1. Restart Management Client with the new drop. Select a Web Content workspace. Expected: the
   detail pane is a four-tab shell — **Settings | Proxy | Diagnostics | Status** — opening on
   Settings; every settings field (name, home address, display/behavior/privacy/advanced toggles)
   is on Settings and behaves exactly as before (MC-9 steps still pass without any tab click).
   Since #81 the proxy routing toggle, the allowlist grid and the test-fetch harness live on the
   **Proxy** tab instead — Settings must realize no proxy control at all.
2. The Proxy tab is live since #81 (structured allowlist grid - MC-14), the Diagnostics tab since
   #84 (read-only blocked-origin grid - MC-16), and Status since #76 (MC-12). Click through all
   tabs:
   the shell's **Save must never become enabled** (selecting a tab holds no inputs and fires no
   change events); returning to Settings shows the same values as before the walk.
3. Status-tab role gate: with a full administrator the Status tab is visible. Assign a test role
   WITHOUT 'Manage Web Content Workspace' (but with View) and log in as that user: the workspace
   opens (read rights) but the **Status tab is absent entirely**. Denied/failed permission
   lookups hide the tab (fail closed); MC log marker when denied:
   `does not grant the Manage security action to this user`.
4. Automatable (admin half): `McWorkspaceTabsProbeTests.ProbeWorkspaceTabsShellRendersAndStaysReadOnly`
   (probe category, read-only — it never clicks Save and never mutates live state); artifacts
   `mc-tabs-{settings,proxy,diagnostics,status,final}.png` + `mc-tabs-probe-results.txt` under
   `%TEMP%\webcontent-mc-tabs-probe\<run>`. The view-only denial half stays manual (needs a
   second role/user).

   Record (MC-11 admin half) - **live row PASSED 2026-11-02**, run `20261102-012719`, drop
   `WebContent.dll` SHA-256 `57F6C12B…1C8B6` hash-verified in the local MIP root: the four tabs
   rendered (Status included — the admin's Manage grant round-trips through the live security
   system), Settings kept every control on the default tab (txtName 'Demo Workspace', the four
   live allowlist lines read back identically after the walk), each placeholder tab rendered its
   marker, and **Save stayed disabled through the whole tab walk**. Screenshots confirm the shell
   visually in the MC detail pane. Re-run after the #81 grid migration: green run
   `20261102-105728` on drop `F1F5D7E8…` - the Proxy-tab half of the walk now reads the
   structured grid.

### MC-12 MC Status tab live proxy sessions (#76)

1. Restart the Management Client with the new drop. `Invoke-psake -taskList Build, DeployLocally`
   is enough — #76 is MC-side only and the Event Server's #69 drop already answers
   `StatusRequest`. Select a Web Content workspace as a user holding its Manage action: the
   Status tab shows the session grid (`dgStatusSessions`), a summary line, and a "Refresh now"
   button — no placeholder.
2. Selecting the tab starts the ~3 s pull loop, and **every pull logs exactly one line** in the
   Management Client's `Logs\MIP.log`:
   - `Proxy status snapshot for workspace '<item id>' answered: N live session(s).`
   - or, on a failed pull, `Proxy status query for workspace '<item id>' failed (code=...)` —
     and the UI banner names the kind: unknown workspace ("does not know this workspace any
     more") vs no channel ("no proxy messaging channel to it").
3. Open any proxied Smart Client view: within a cadence the session appears as a row — XProtect
   user from the platform sender identity, client machine from the Open event, duration/idle,
   requests + req/min from the ES's correlation traffic, and send/receive bitrates differencing
   the ES's cumulative byte counters across two consecutive polls (a first sighting shows
   `0 B/s` — the honest no-baseline zero, never the session's whole life as a spike).
   Zero sessions is rendered as its own quiet message, not a failure banner.
4. **Leave the tab: the marker count goes FLAT** — the timer is tied to tab selection, and a
   closing role gate or a workspace-item switch tears it down with the rate baseline. Re-selecting
   re-arms it. Nothing is persisted; there is no push channel.
5. Automatable: `McProxyStatusProbeTests.ProbeStatusTabPollsLiveSessionsAndStopsWhenLeft`
   (probe category, read-only — drives Management Client AND Smart Client on one machine:
   cadence markers appear while the tab is open, this machine's proxied view surfaces as a
   session row within 25 s, the marker count stays flat across 10 s of tab-away, re-selecting
   resumes the pulls, Save never enables); artifacts `mc-status-{quiet,live}.png` +
   `mc-status-probe-results.txt` under `%TEMP%\webcontent-mc-status-probe\<run>`. The role-gate
   denial half stays manual (MC-11 step 3 — needs a second restricted user).

   Record (MC-12) - **live row PASSED 2026-11-02**: 626 unit tests green,
   drop `3D90CE93…` hash-verified on both machines (Management Client + Event Server). The first
   live attempt (run `20261102-040119`) died at the FIRST mouse call (`Win32Exception: Access is
   denied` in `CollapseTreeToRoot` — the driving session had gone disconnected; desktop input is
   refused there, the same wall ES-2 step 4 documents) — before any tree click or Save, so no
   workspace state was touched. Meanwhile the MC-13 walk found the `dgStatusSessions`
   tri-state-`Disabled` BAML bug (#76) that would have kept this page from loading at all on that
   drop; fixed and pinned (`f142514`), rebuilt, redeployed, and the re-run (run `20261102-074407`)
   is green end to end: cadence markers appear on tab-open, this machine's proxied Smart Client
   view surfaces as a session row within 25 s ('1 live proxy session(s) - 2.0 requests/min'),
   the marker count stays flat across 10 s of tab-away, re-selecting resumes the pulls, Save
   never enables. The tabs walk re-ran green too (run `20261102-074319`), its Status step now
   expecting the live grid. Artifacts under `%TEMP%\webcontent-mc-status-probe\20261102-074407`.
   Re-run with:

   ```powershell
   Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~McProxyStatusProbeTests' }
   Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~McWorkspaceTabsProbeTests' }   # its Status step now expects the live grid
   ```

### MC-13 Proxy allowlist wildcards live at the Event Server gate (#73)

1. Restart Management Client with the new drop (both machines need it: the ES gate and the MC
   save path both changed). Allowlist a wildcard line on a proxy-enabled workspace, e.g.
   `http://*.neverssl.com`, and Save. The page must re-render the line **verbatim** — that
   readback is the structured `pattern` storage record round-tripping (older builds store it as
   `host`; readers accept both forever).
2. Use the workspace page's "Test proxy fetch" row. The Event Server's allowlist snapshot
   re-resolves at most once per 60 s window, so the first fetch after a save can still answer
   with the PREVIOUS list — a stale-snapshot `NotAllowed` that flips to admission after the
   window is normal; pin verdicts only from post-window fetches.
3. Wildcard posture with only `http://*.neverssl.com` allowlisted: `http://online.neverssl.com/`
   is admitted (a 200, or a `Timeout`/`Unreachable` that NAMES the dialled target — the
   dial itself proves admission, only the matcher can answer `NotAllowed`), while the apex
   `http://neverssl.com/` refuses `NotAllowed` in 0.0s (the wildcard covers subdomains, never
   the apex itself).
4. Hatch + SSRF floor posture with `http://*` allowlisted: normal hosts are admitted (the apex
   from step 3 flips from `NotAllowed` to fetchable — one entry, two verdicts, one refresh
   window apart), while `http://10.20.30.40/` (literal IP) and `http://vms.joshooaj.com/`
   (the VMS's own host, from `EnvironmentManager`) both refuse `NotAllowed` in 0.0s. The 0.0s
   matters: a missing floor would DIAL and answer `Unreachable` after seconds. Restore the
   workspace's production lines afterwards.
5. Automatable: `McWildcardAllowlistProbeTests.ProbeWildcardAndHatchAtTheLiveEventServerGate`
   (probe category — drives the whole protocol above through the live MC, self-healing: it strips
   any earlier dead run's probe lines on save and always restores the production lines with
   verified readback); artifacts `mc-wildcard-phase-b.png` + `mc-wildcard-probe-results.txt`
   under `%TEMP%\webcontent-mc-wildcard-probe\<run>`.

   Record (MC-13) - **live rows PASSED 2026-11-02** (green run `20261102-073648`, both machines
   on drop `3D90CE93…` hash-verified): phase A readback carried `http://*.neverssl.com` verbatim;
   the subdomain was admitted (with the `HTTP 200 - 3961 bytes` success shape banked in run
   `20261102-071520` before the public target began throttling the Event Server's 5 s header
   window), the apex refused `NotAllowed` 0.0s; phase B readback carried `http://*` verbatim,
   the apex then fetched `HTTP 200` (run `20261102-073010`), and `http://10.20.30.40/` plus
   `http://vms.joshooaj.com/` both refused `NotAllowed` 0.0s — the syntax floor and the
   EnvironmentManager floor each refusing before any socket opened. The allowlist ended restored
   to its four production lines (the run also cleaned a `http://*` left behind by a run the
   desktop-disconnect killed mid-restore). Re-walked after the #81 grid migration on the same
   day: green run `20261102-105816` on drop `F1F5D7E8…` — every allowlist line is now typed
   through the structured grid (there is no free-text box left to type into) and the ES gate
   verdicts are unchanged.

   LIVE FINDING from this walk: the deployed #76 `dgStatusSessions` carried
   `CanUserResizeRows="Disabled"` — a WinForms tri-state string on a WPF-BOOLEAN property — so
   `GenerateDetailUserControl` threw (`FormatException: Disabled is not a valid value for
   Boolean`) and the ENTIRE workspace config page failed to load: no Settings controls at all,
   which is why the first two MC-13 attempts died at `txtProxyTargets`/tree-walk steps, not at
   anything #73 changed. Fixed in the `fix(mc): dgStatusSessions carried tri-state…` commit; the
   shell guard now pins every WPF-boolean `CanUser*` attribute on every DataGrid of the page to
   a real bool, and the #76/#74 MC rows should be re-walked on the fixed drop.

### MC-14 Structured allowlist grid on the Proxy tab (#81)

1. Restart Management Client on the new drop and open a Web Content workspace item. Select the
   **Proxy** tab: the whole proxy section lives there now — the routing toggle, the allowlist
   **grid** (Scheme | Host | Port | Accept untrusted | Problem | Remove), the full-host-wildcard
   warning banner (collapsed until a `*` row is in the grid), the hint line, and the #53 fetch
   harness. The Settings tab carries no proxy control at all, and the old free-text **Proxy
   targets** box is gone: the stored allowlist renders one entry per row, from either stored shape
   (legacy `host` records and #73 `pattern` records both render).
2. **Add target**, type a rejected entry (`https://foo.*.bar.com`, or a valid host with port
   `99999`): the row highlights and its **Problem** cell says why. Press Save: the save is
   REFUSED — a notice names the offending rows, the Management Client MIP.log gains
   `Rejected save for workspace '<name>' (FQID: …): invalid proxy target row(s): <rows>`, the rows
   stay in the grid flagged (never silently dropped), and re-selecting the item shows the stored
   allowlist exactly as it was — nothing partial reached storage.
3. Fix the rows and Save; re-select the item so the pane is rebuilt FROM THE STORED ITEM. The entry
   reads back with its scheme, port and **Accept untrusted** ticked — the flag lives in the stored
   record, never in a typed token. Add `http://*`: the amber warning banner comes up while that row
   is in the grid and goes back down when the row's **Remove** button drops it. Restore the
   workspace's own rows and verify.
4. Automatable: `McAllowlistGridProbeTests.ProbeStructuredAllowlistGridEditsRefusesAndStores`
   (probe category — drives all of the above through the live MC: the absent free-text box, the
   inline Problem cells, the refusal notice text, the MIP.log marker delta keyed on the probe's own
   bad host, the flag round-trip read only from a RELOADED pane, the banner on/off pair, and the
   per-row Remove affordance; restores the workspace's own allowlist with verified readback);
   artifacts `mc-grid-refused-rows.png`, `mc-grid-restored.png` +
   `mc-allowlist-grid-probe-results.txt` under `%TEMP%\webcontent-mc-allowlist-grid-probe\<run>`.

   Record (MC-14) - **live row PASSED 2026-11-02**, green run `20261102-105226` on drop
   `F1F5D7E8…` (hash-verified against `output\WebContent`): no `txtProxyTargets` control exists in
   the live pane and the hint teaches no typed token; both refused rows (`foo.*.bar.com`, port
   `99999`) carried inline Problem cells (`'foo.*.bar.com' is not a valid host or wildcard
   pattern.'`, `Port must be a number between 0 and 65535; …`), and the save was refused with the
   notice `Invalid address: https://foo.*.bar.com; http://ok.gridprobe.invalid:99999 is not a
   valid proxy target. Fix the highlighted row or remove it before saving.` - each row quoted in
   exactly the shape the ROW MODEL holds, which is also the pre-save proof that the scheme
   dropdown reached the row. The MIP.log refusal marker moved 17 -> 18 keyed on the probe's own
   bad host; the refused rows stayed in the grid flagged; the follow-up save's audit line
   (`Stored proxy targets for workspace 'Demo Workspace': routing on; …`) carried only the fixed
   set, and the reloaded pane agreed. The `accept-untrusted` flag round-tripped: stored as
   `https://gridprobe.invalid:8443 accept-untrusted`, read back ticked from the RELOADED pane and
   stated by the audit line. `http://*` raised the warning banner (read via the banner's TextBlock
   - the Border itself has no UIA peer), and the row's Remove button dropped both row and banner.
   The workspace's own four lines were restored with audit-line + reloaded-pane double
   verification. The live walk caught three defects the unit layer could not see: the scheme
   ComboBox and the accept-untrusted CheckBox lost their edits to the DataGrid's deferred source
   update until the bindings said `UpdateSourceTrigger=PropertyChanged` (the admin saw the picked
   value while the row stored the old one - now pinned by a guard test over every `Mode=TwoWay`
   binding in the grid); the storage de-dupes so probe leftovers double up (the harness now
   treats its own host/hatch rows as not-baseline); and the wildcard banner probe must read the
   TextBlock, not the peerless Border.

### MC-15 Export the deployment CA certificate over MIP messaging (#75)

1. Deploy the drop to BOTH machines (`Invoke-psake -taskList Build, Deploy` — the Event Server half
   matters: the responder lives in the background plugin there). Confirm the Event Server loaded it:
   `logs\MIP.log` gains `Proxy fetcher ready (…, export=WebContent.Proxy.CaExportRequest, …)`.
2. In Management Client select the plugin's **Web Content Workspaces** node: the overview page carries
   the **Export proxy certificate** button. Press it: the pane shows the "asking the Event Server"
   line, then a **Save the proxy authority certificate** Save As dialog with default name
   `WebContent-Proxy-Authority.cer` and a `*.cer` (DER) filter.
3. Expected (success half): MC `Logs\MIP.log` gains `Proxy authority certificate answered: thumbprint
   …, valid to …, N DER bytes.` then `Proxy authority certificate exported to '<path>' (thumbprint
   …)`; the Event Server log gains the paired `Exported the deployment proxy authority certificate
   (thumbprint …, valid to …)`; the saved file parses as X.509 (first byte `30`, NOT PEM text) and its
   thumbprint equals the store entry's. The pane then states the saved path + thumbprint and the
   LocalMachine\Root / AD-GPO follow-up (README "HTTPS targets").
4. Expected (no-CA half): on a machine whose `LocalMachine\Personal` has no `XProtect WebContent
   Plugin Loopback Proxy` certificate, the button opens NO dialog and the pane banner says the proxy
   authority certificate does not exist yet (with the provisioning pointer); no file is written —
   the ticket's empty-file prohibition. Proven live server-side by driving the deployed dll's
   responder against an empty finder: it answers `NoAuthority` with exactly that sentence.
5. Provenance independence: the live proof used a CA created by the OLD provisioning script, not by
   this build — the responder resolves by fixed CN only. Read-only: the export path opens the store
   `ReadOnly | OpenExistingOnly`; the live responder probe left the store count unchanged (6→6).

   Automatable: `McCaExportProbeTests.ProbeProxyCaExportSavesTheEventServerAuthorityAsDer` (probe
   category — reads the ES store FIRST over WinRM through `EventServerAuthority.ReadDeploymentAuthority`
   (read-only) so the assertion is against the live store, not a stale view; drives the button, the
   Save As dialog, the file-name box and the commit; then asserts the saved bytes are BYTE-IDENTICAL
   to the store copy, the thumbprint matches, the DER parses, and both MC MIP.log markers carry this
   run's own artifacts path). Artifacts `mc-ca-export-overview.png`, `mc-ca-export-save-dialog.png`,
   `mc-ca-export-result.png`, `exported-authority.cer` + `mc-ca-export-probe-results.txt` under
   `%TEMP%\webcontent-mc-ca-export-probe\<run>`.

   Record (MC-15) - **live row PASSED 2026-11-02**, green run `20261102-154554` on drop `FACA878B…`
   (hash-verified on the local install folder AND the Event Server copy): ES ready marker at
   14:51:49 carried `export=WebContent.Proxy.CaExportRequest`; the store held exactly one authority
   (thumbprint `E4D254D58CB1BDA6E894C0F484F3C5AA718D2377`, 816 DER bytes, to 2036-10-30), and the
   legacy script's own `C:\Users\Public\WebContent-Proxy-Authority.cer` (written 2026-10-31, before
   this build existed) carries the same thumbprint - the provenance AC. Pressing the button paired
   ES 15:46:15.598 `Exported the deployment proxy authority certificate (thumbprint E4D254D5…, valid
   to 2036-10-30 15:33:01Z)` with MC 15:46:16.213 `Proxy authority certificate answered: … 816 DER
   bytes` and 15:46:18.967 `exported to '…\20261102-154554\exported-authority.cer'`; the written file
   was byte-identical to the live store copy (asserted, not eyeballed). The no-CA posture, the
   read-only store open (count 6→6), and the DER round-trip were additionally proven by driving the
   DEPLOYED dll's responder in-process on the ES machine (`verify-ca-export.ps1`). The live walk
   caught two harness truths worth remembering: the modal Win32 common dialog is not reliably under
   the client's own window list (desktop-wide window scan needed), and typing the path via
   `ValuePattern.SetValue` gets honoured only for the NAME - the dialog saves into its OWN folder, so
   the probe types the path with keystrokes and commits with ENTER.

### MC-16 MC Diagnostics tab: live blocked list + one-click promotion (#84)

1. Restart the Management Client with the new drop. `Invoke-psake -taskList Build, DeployLocally`
   is enough - #84 is MC-side only; the Event Server's deployed drop already accepts block reports
   and answers `StatusRequest` snapshots with the `Blocked` ring (#69). Open a Web Content workspace
   as a user holding its Manage action: the **Diagnostics tab is visible** (the identical gate as
   Status - MC-11 step 3 applies to it verbatim), rendering `dgDiagnosticsBlocked`, a summary line,
   and a "Refresh now" button. The summary and the empty-state copy both say out loud that the list
   lives in the Event Server's memory only and **resets when the Event Server service restarts**.
2. Selecting Diagnostics (like selecting Status) arms the ONE status query pair; every pull logs
   exactly one MC marker - `Proxy status snapshot for workspace '<item id>' answered: N live
   session(s), M blocked origin(s).` or the query-failure variant - and leaving BOTH tabs stops the
   markers. The tab never writes: walking it, "Refresh now" included, cannot enable Save (guard-
   pinned: the grid is read-only, the only handlers are `OnDiagnosticsRefresh`/`OnDiagnosticsPromote`).
3. A live Smart Client view whose subresources point at a non-allowlisted origin (the SC-7 page
   pair: origin 18125 allowlisted, asset origin 18131 not) puts a row on the grid within the
   cadence: canonical origin, shared reason label (`not allowed by administrator`), hit count,
   recency, and an "Add to allowlist" button. Rows order newest-first, ties stable by origin.
4. One click on the button: MC marker `Proxy allowlist promotion for workspace '<item id>': origin
   '<origin>' added to the allowlist grid (grid edit, not yet saved - Save writes it).`; the row
   re-annotates `on allowlist` and its button is GONE (hidden, not disabled - a double click cannot
   stack duplicates; the probe's rule for "gone" is no VISIBLE promote peer, since a collapsed WPF
   button leaves an off-screen UIA peer with a zero rect); the Proxy tab's grid now carries the
   exact scheme+host+port row **unsaved**, and Save is armed. Rows the promotion cannot help -
   `certificate not trusted`, already covered (exact or wildcard, through the same shared matcher
   incl. the SSRF/VMS floor), unparsable - never get the button in the first place.
5. Saving goes through the ordinary allowlist save discipline (MC-9/MC-14): audit line
   `Stored proxy targets for workspace '<name>' (FQID: ...)` names the promoted entry, and the grid
   re-renders it from a RELOADED pane. Re-opening Diagnostics after the reload re-annotates the row
   `on allowlist` from STORED config - the "survives a workspace re-read" acceptance.
6. Automatable: `McDiagnosticsTabProbeTests.ProbeDiagnosticsTabPromotesLiveBlockedOriginIntoTheSavedAllowlist`
   (probe category; it MUTATES the workspace allowlist briefly and restores it verbatim with the
   audit+reloaded-pane verification, run alone; drives Smart Client + Management Client on one
   machine); artifacts `mc-diagnostics-{row,after-promote,annotated}.png` + `mc-diagnostics-probe-results.txt`
   (which ends with a cell-by-cell `grid dump` of the Diagnostics grid: every promote peer UIA can
   see, with its row, rect and on/off-screen state) under `%TEMP%\webcontent-mc-diagnostics-probe\<run>`.
   The probe's own order is part of the record: it clears any earlier run's probe line from the
   allowlist BEFORE the Smart Client page load, and its teardown answers the shell's "Save changes?"
   prompt with NO (`ManagementClientUi.RejectPendingModal`), never Yes.

   Record (MC-16) - **live row PASSED 2026-11-02**, green run `20261102-182628` (3 m 20 s), drop
   `WebContent.dll` SHA-256 `A718A87B.` hash-verified in the local install folder: the ES ring
   carried 5 blocked origins (other views' refusals included); the fresh refusal rendered
   `http://172.16.128.210:18131 | not allowed by administrator | 3 | 35s ago` with its button while
   the Diagnostics tab alone drove the snapshot markers; the Invoke logged the promotion marker at
   18:27:45.653, annotated the row `on allowlist`, and its button vanished (screenshot: among the 5
   rows only 18131 shows `on allowlist` and carries no button - the other four keep theirs); the
   Proxy grid carried `http://172.16.128.210:18131` before any save; the save audit enumerated all
   seven entries including the promoted one, the reloaded-pane readback matched, and re-opening
   Diagnostics re-annotated from stored config (`1m 16s ago`); the six-line allowlist restored with
   audit+readback verified. The `CertUntrusted` no-button posture stays pinned in the unit layer
   (a live untrusted-https blocked origin would need the SC-6 certificate posture end to end).

   Failure signatures: opening Diagnostics with no markers = the tab never armed the shared timer
   (MC-12's cadence discipline is the twin); a plain `not allowed` row WITHOUT a button = the
   annotation matcher thinks an entry already covers the origin - check the grid and the floor hosts
   (unit matrix pins the matcher, the live row names the entry); Save still disabled after the
   click = the promotion bypassed the grid's row-collection chain; the promoted origin absent from
   the audit line = the promotion wrote somewhere other than the editor's grid.

   Harness truths the live rows taught (each one burned a run, MC-16):
     * **UIA containment lies.** Flattening a DataGrid row with `FindAllDescendants` surfaces
       SIBLING rows' buttons, so row-scoped Name joins are not evidence. The Grid pattern is the
       addressing authority: `dgDiagnosticsBlocked` answers `GridPattern` (6 columns for its 6
       columns) and `GetItem(row, column)` isolates one cell; the geometry check (the button's
       centre inside the row's own rect) is only the fallback when the pattern is missing.
     * **A hidden WPF button still has a UIA peer.** `Visibility=Collapsed` leaves the peer with a
       zero rect and `IsOffscreen=true` - live, the promoted row's `diagnosticsRowPromote` peer is
       still enumerable right after its annotation flips to `on allowlist`. "Peer exists" is not
       "button offered": the probe's rule is a VISIBLE button (non-zero rect, not off-screen), and
       the results file prints a cell-by-cell `grid dump` naming every promote peer with its rect
       and on/off-screen state - in the green run the four uncovered origins are
       `on-screen rect={X=2305,...}` and the two covered ones `offscreen
       rect={X=0,Y=0,Width=0,Height=0}`.
     * **Empty WPF `DataGridCell`s answer a SYNTHESIZED Name** (`Item:
       WebContent.Admin.ProxyBlockedRowView, Column Display Index: 4`), which leaks into any
       Name-join of a row - the blank Status cell showed exactly that. Cell text comes from the
       `Text` peers first, with a synthesized fallback name read as empty.
     * **Probe order is load-bearing, and teardown must not save.** Clear any earlier run's probe
       line from the allowlist BEFORE the Smart Client page load: with the line still stored the
       listener FORWARDS the subresources and the ring row arrives as `target timed out`, not
       `not allowed by administrator`. And a teardown that answers "Save changes?" with YES
       persists a mid-run promotion - the ring keeps same-origin entries until the Event Server
       restarts, so the stale row then satisfies a reason-blind row lookup. Teardown uses
       `ManagementClientUi.RejectPendingModal` (No/Cancel), and the row lookup filters on the
       reason fragment.
     * Known residue for a future sweep: `McAllowlistGridProbeTests`, `McProxyStatusProbeTests` and
       `McWildcardAllowlistProbeTests` still close with `AcceptPendingModal`, i.e. they would
       persist an interrupted edit the way MC-16's first run did.

   Record (MC-16, hardened probe) - **live row PASSED again 2026-11-02**, green run
   `20261102-193021` (3 m 07 s, the run of exactly this probe source; the identical chain went
   green one run earlier as `20261102-192322`), drop `WebContent.dll` SHA-256 `6271ED81.`
   (1 381 376 bytes) hash-verified in the local install folder - the plugin source is unchanged
   since the first green run, only the probe was hardened. The ring held 6 rows (three unrelated
   live browser refusals included); the probe's fresh refusal was row 0 - `http://172.16.128.210:18131
   | not allowed by administrator | 15 | just now` with an on-screen button - while the stale
   same-origin `target timed out` entry from the mis-ordered run sat at row 4 already annotated
   `on allowlist` (the shared matcher annotates every covering entry; the reason fragment is what
   keeps the probe on its OWN row). One Invoke logged the promotion marker, the row re-annotated
   `on allowlist`, and the cell dump (`rows=6 cols=6`) shows both covered rows' promote peers
   `offscreen rect={X=0,Y=0,Width=0,Height=0}` while the four uncovered origins kept
   `on-screen rect={X=2305,...}` buttons; the unsaved Proxy grid carried the exact origin, the save
   audit enumerated all seven entries, the reloaded pane read it back, re-opening Diagnostics
   re-annotated `on allowlist` from stored config (`41s ago`), and the six-line allowlist was
   restored with audit+readback verified.

### MC-17 Per-workspace response cap: default, raise, refusal, restore (#85)

Needs the drop deployed on BOTH machines (the cap store lives on the Event Server; the field and
the fetch harness live in the MC). The probe (`McProxyResponseCapProbeTests`, run via
`Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~McProxyResponseCap' }`)
pins five things, each by a live round trip:

1. Provisioning: the probe origin (`https://proof.ovh.net`) goes into the Proxy grid AND the cap
   field is normalized to the default in ONE save — the idempotent baseline, since an interrupted
   earlier run may have left a raised cap stored.
2. Default posture: fetching `https://proof.ovh.net/files/10Mb.dat` through the Proxy-tab harness
   answers `Proxy fetch TooLarge ... exceeded the 5242880 byte response cap.` — the refusal names
   the cap that produced it.
3. Raised cap: typing `12` + Save writes the `Stored proxy response cap ... : 12 MB.` audit line
   and the reloaded pane reads back `12`; polling the SAME fetch flips to
   `HTTP 200 - 10485760 bytes fetched` within the store's 60 s refresh window. The success also
   proves the CLIENT receive guard moved to the 20 MB protocol ceiling — a client still guarding
   at the old default would reject the 10 MB answer on the way back in.
4. Refusal: typing `99999` + Save raises the `Invalid address` notice naming the rejected text,
   the MC MIP.log gains `invalid proxy response cap '99999'`, NO new cap-audit line appears, and
   the ES keeps serving the stored cap (the fetch still succeeds) — nothing partial reached storage.
5. Restore: typing `5` + Save re-arms the TooLarge refusal at 5242880 within a refresh window —
   the cap is live in BOTH directions; then the workspace's own allowlist and cap are restored,
   verified audit + reloaded pane, teardown answers `Save changes?` with NO (`RejectPendingModal`).

Harness truths the live rows taught (each one burned a run, MC-17):

* `proof.ovh.net` answers `HTTP 301` on plain http (the harness does not follow cross-scheme
  redirects) — use the https origin and allowlist it as `https://...`; and the classic `6Mb.dat`
  is gone (404) while `10Mb.dat` (10 485 760 bytes) is the live fixed-size file.
* A refused cap edit proves storage untouched SERVER-SIDE, not by pane readback: on reselecting
  the same tree item the MC runs the platform fill through a state where its dirty chain throws
  `Not valid to set dirty state at this time!!!` — a TextBox interrupted mid-fill keeps whatever
  the user typed, so the "reloaded pane still reads the old value" claim is only honest when the
  stored value and the typed value differ ONLY via audit lines and ES behavior. Phase 5 asserts
  the cap-audit line count does NOT move and the fetch keeps succeeding instead.
* The baseline must self-heal: a stored cap equal to the probe's own raise value is leftover from
  an interrupted run and normalizes to the default, exactly like probe-host allowlist rows.
* The ES cap store refreshes at most every 60 s, and the window may have just been armed by the
  pre-change fetch — the raise phase polls the fetch itself (up to 120 s) and the poll trail is
  the evidence of the refresh cadence, not a sleep.

Record (MC-17) - **live rows PASSED 2026-11-02**, green run `20261102-210935` (6 m 14 s), drop
`WebContent.dll` SHA-256 `3627C762.` (1 388 032 bytes) hash-verified in the local install folder
and deployed to the Event Server via `DeployToVms`. Chain from the results file: baseline cap `5`
after leftover normalization; `default-cap fetch -> Proxy fetch TooLarge after 2.1s - Response body
from https://proof.ovh.net exceeded the 5242880 byte response cap.`; `stored cap '12' MB verified
from the audit line AND a reloaded pane`; three `TooLarge ... (re-polling)` rows inside the refresh
window then `raised-cap fetch -> HTTP 200 - 10485760 bytes fetched in 9.4s`; `refusal notice:
Invalid address OK '99999' is not a valid proxy response size ...`; `cap audit lines after the
refused save: 12 -> 12` with `post-refusal fetch -> HTTP 200 - 10485760 bytes fetched in 9.2s`;
`restored-cap fetch -> ... TooLarge ... 5242880 ... (cap live in both directions)`; `allowlist and
cap ('5') restored (verified from reloaded panes)`. Four earlier red runs contributed the harness
truths above (301 on http, 404 on 6Mb.dat, the fill-interrupt readback lie, and the missing
restore-phase grid edit).

### MC-18 Per-workspace header policy: store, guardrail, restore (#86)

`McProxyHeaderPolicyProbeTests.ProbePerWorkspaceHeaderPolicyStoreRefusalAndRestore` — drives the
Proxy tab's two "Advanced: headers" grids (`proxyRequestHeadersGrid`,
`proxyResponseHeadersGrid`) through a live Management Client against the live VMS:

1. Baseline: both grids read; leftover rows from an interrupted run normalize to the empty
   policy first (the MC-17 self-heal lesson) so the run is idempotent.
2. Store: an add/override row plus a strip row in EACH grid saves; verification is two-channel
   like the allowlist — the plugin's `Stored proxy header policy ... request 2 rule(s) [X-...;
   -X-...]; response 2 rule(s) [...].` audit line in the MC MIP.log AND both grids re-read from
   a RELOADED pane, ORDER-SENSITIVELY (configuration order is part of the policy: last rule for
   one name wins, here and on the Event Server).
3. Guardrail, four cycles — `Host` (request leg) and `Proxy-Connection` (response leg), each in
   the add form and the strip form: the Problem cell explains while the row is typed (`'Host' is
   reserved for the proxy connection and cannot be configured.`), Save raises the plugin's
   refusal notice NAMING the row verbatim (`Host=evil.example.com` / `-Host`), the MC MIP.log
   gains the matching `invalid proxy (request|response) header row(s)` marker, and the
   `Stored proxy header policy` audit-line count never moves — the WHOLE save is refused,
   nothing partial is stored. Strip never unlocks a forbidden name: the guardrail is on the
   name, not on the direction.
4. Storage untouched: after all four refusals the grids still display exactly the stored
   policy and the audit count is unmoved.
5. Restore: emptied grids store the EMPTY policy (`request 0 rule(s) []; response 0 rule(s) []`)
   the same verified way — the pre-#86 posture; teardown answers `Save changes?` with NO.

Harness shape (no new live truths — MC-17's rules carried over first try): `HeaderGridUi` scopes
every cell lookup to the grid element first, because BOTH grids' rows share the same per-row
AutomationIds (`proxyHeaderRowName`/`Value`/`Strip`/`Problem`/`Remove`) — a window-wide find
would interleave the two policies; within a grid, rows are addressed by document order like the
allowlist grid, and virtualization is off in both grids' XAML (pinned by the shell guard).

The live FETCH effect of the policy (headers actually added/stripped against a real target) is
unit-pinned (`ProxyHeaderPolicyTests` + `EventServerHeaderPolicyWiringGuardTests`), not
probe-pinned: the Proxy-tab fetch result label reports status/bytes/elapsed only, so no UIA read
could show headers; the ES-side application sits in the fetch leg the SC probes exercise without
header visibility either.

Record (MC-18) - **live rows PASSED 2026-11-02**, green run `20261102-224611` (3 m 26 s), drop
`WebContent.dll` SHA-256 `3889A67F.` (1 413 632 bytes) hash-verified in the local install folder.
Chain from the results file: baseline `request=[] response=[]`; `stored header policy audit
line: ... request 2 rule(s) [X-Probe-Request=probe-request-value; -X-Probe-Strip-Request];
response 2 rule(s) [X-Probe-Response=probe-response-value; -X-Probe-Strip-Response].`; four
`refusal notice (names the row verbatim): Invalid address OK The proxy header policy has invalid
row(s): Host=evil.example.com / -Host / Proxy-Connection=close / -Proxy-Connection. ...`; four
`nothing stored by the refusal: ... lines 1 -> 1`; `storage untouched through all four refusals:
audit lines 1 -> 1`; `saved header policy request=[] response=[] (verified from the audit line
AND reloaded panes, order-sensitive)`.

### MC-19 Per-workspace rule triggers: store, grammar guardrail, restore (#78)

`McProxyTriggersProbeTests.ProbeProxyRuleTriggersStoreRefusalAndRestore` — drives the Proxy tab's
"Rule triggers" grid (`proxyTriggersGrid`, between the permission headers and the test fetch)
through a live Management Client against the live VMS:

1. Baseline: the grid reads the stored rows; leftover rows from an interrupted run normalize to
   no triggers first (the MC-17 self-heal lesson) so the run is idempotent.
2. Store: two differently-shaped rows — `http://probe.triggers.example.com/videos/*` + GET + 404
   and `https://*.probe.triggers.example.com/*` + any-method + any-status — save; verification is
   two-channel like the other grids — the plugin's `Stored proxy triggers for workspace '<name>'
   ...: 2 row(s) [url=... method=GET status=404; url=... method=any status=any].` audit line in
   the MC MIP.log AND the grid re-read from a RELOADED pane, ORDER-SENSITIVELY (the Event
   Server's matcher evaluates top-down and raises at most the FIRST matching row's event). Every
   row — typed AND reloaded — shows the read-only Event raised column with the fixed event type
   `WebContent proxy request`, the one event type the plugin raises (pinned to
   `ProxyTriggerPolicy.EventType`).
3. Grammar guardrail, three cycles — one per failing member: a mid-host wildcard url
   (`http://foo.*.bar.com/`; the host grammar is exact / `*.tail` / lone `*`), status `600` (no
   6xx class, outside 100-599), and a POST verb chosen with NO url (a verb without a url is a
   refusal, not a blank row). Each: the Problem cell explains the failing member while the row is
   typed, Save raises the plugin's refusal notice NAMING the row verbatim
   (`url=... method=... status=...`), the MC MIP.log gains the `invalid proxy trigger row(s)`
   marker, and the `Stored proxy triggers` audit-line count never moves — the WHOLE save is
   refused, nothing partial is stored. This grammar IS the Event Server's grammar (the same
   `ProxyTriggerPolicy` normalizers the matcher runs over stored rows on every refresh — the
   grid-vs-matcher agreement is unit-pinned), so a row the MC refuses is exactly a row the server
   would refuse and audit anyway.
4. Storage untouched: after all three refusals the grid still displays exactly the stored policy
   and the audit count is unmoved.
5. Restore: an emptied grid stores NO trigger section (`0 row(s) []`) — the pre-#78 posture;
   teardown answers `Save changes?` with NO.

Harness shape: `TriggerGridUi` scopes every lookup to the grid element (every per-row id is shared
by all rows, and the trigger grid shares its tab with the allowlist and the two header grids).
The Method cell is a plain WPF ComboBox in the CellTemplate — three live UIA lessons: the closed
combo's selection must be read from the ComboBoxItem peers' `IsSelected` flags, and those peers
exist only once the dropdown has been opened at least once (a freshly loaded closed combo exposes
ZERO item peers — the harness expands once, reads, collapses); the collapsed popup stays inside
the combo's UIA subtree, so Name/Text scans read the popup's rows, not the selection; and the
write route is ExpandCollapse + SelectionItem.Select, escalating to focus-gated Up/Down steps over
the pinned verb order.

The live EVENT effect (a matching fetch raising the XProtect event and rules reacting) is
unit-pinned (`ProxyTriggerMatcherTests` etc.) and was ES-verified at the #65 deploy with the
`trigger-selftest.flag` probe (ES-3); the Proxy-tab fetch label cannot show events, so this probe
pins the MC UI, the guardrail, and the stored shape.

Record (MC-19) - **live rows PASSED 2026-11-03**, green run `20261103-031310` (3 m 20 s), drop
`WebContent.dll` SHA-256 `35D35764.` (1 448 448 bytes) hash-verified in the local install folder
(the ES drop already carries the #65 matcher; MC-only change, no `DeployToVms` needed). Chain
from the results file: baseline carried the two rows a debug cycle had left behind and normalized
them to `0 row(s) []` (self-heal earning its keep); `stored triggers audit line: ... 2 row(s)
[url=http://probe.triggers.example.com/videos/* method=GET status=404; url=https://*.probe.
triggers.example.com/* method=any status=any].`; `every typed row shows the read-only Event
column: 'WebContent proxy request'` and the reloaded rows read the same; three `refusal notice
(names the row verbatim): Invalid address OK The proxy trigger list has invalid row(s):
url=http://foo.*.bar.com/ method=any status=any / ... status=600 / url= method=POST status=any.
...`; three `nothing stored by the refusal: ... lines 7 -> 7`; `storage untouched through all
three refusals: audit lines 7 -> 7`; `saved triggers [] (verified from the audit line AND the
reloaded grid, order-sensitive)`.

### SC-1 End-to-end proxied page load (#54)

Requires the drop deployed on BOTH machines (Event Server machine + Smart Client machine per
`AGENTS.md`) and a target device that the Smart Client machine can reach (a monitored-segment
device or any http page — https is refused by design, see below).

1. In Management Client, enable the proxy on a workspace and allowlist the target origin in
   http form (e.g. `http://10.0.0.5:8080` — https targets would need CONNECT tunnels, which this
   forwarder deliberately refuses). Save; log in to Smart Client and open the workspace. A proxy
   toggle made mid-session is reconciled when the workspace tab is reopened (the retained browser
   is rewired and its WebView recreated — expected log: `rewired to loopback listener` or
   `rewired to direct routing`).
2. Expected Smart Client log markers (MIPLog.txt): `Proxied browser <id>: loopback listener on
   127.0.0.1:<port>, N allowlisted target(s)`. The proxied profile gets its own WebView2
   user-data folder (`...\proxied-<id>`) and is isolated even when the isolate checkbox is off:
   one browser process is shared per user-data folder, and the `--proxy-server` argument must not
   collide with other workspaces' WebViews.
3. Expected: the home page renders, and its images/CSS load — subresources go through the same
   listener. Then block the client machine's route to the target (`New-NetFirewallRule` block, a
   hosts-file entry pointing the target at 127.0.0.1, or pull the client off that segment).
   Reload: the page MUST still render, while a direct `Invoke-WebRequest http://10.0.0.5:8080/`
   from the client machine now fails. That asymmetry is the proof the traffic originates on the
   Event Server machine.
4. Address-bar navigation to a NON-allowlisted origin in the proxied workspace shows the proxy's
   403 page ("not in the workspace proxy allowlist"), and no request leaves for it (the fetcher
   is never asked). Navigation to an https:// origin shows the 502 CONNECT-refusal page — https
   is never taken direct. Since #55 a POST/HTML form submit is NOT a 405: it forwards with its
   body (see SC-2); only verbs outside the relay set (TRACE and friends) show the 405 page.
5. Event Server `Logs\MIP.log` during 3: successes are silent; failure cases (4) log the
   refusal/`Proxy fetch failed` markers from ES-1/MC-10.
6. Remove the view item from the layout, or exit the Smart Client: the listener stops with the
   browser (`Proxy listener` log entries only on faults); `netstat -an | findstr <port>` shows
   nothing listening afterwards. (A mere close of the view tab keeps a background-retained
   browser — and its listener — alive for re-attach; that is existing browser lifecycle behavior.)

   Automatable harness: `tests/ux/WebContent.UxTests/ScProxyFetchProbeTests.cs` (xunit
   `Category=probe`) launches Smart Client, logs in, selects the proxied workspace, navigates the
   allowlisted target, and writes `sc-proxy-probe-results.txt` (+ page screenshot) under
   `%TEMP%\webcontent-sc-proxy-probe\<run>`. The probe goes red — not just records — when the
   render is a proxy error page or the body is smaller than the real target page. For row 3's
   negative control, add the client-machine hosts entry `127.0.0.1 <target> #
   webcontent-sc-1-block` and run with `WEBCONTENT_SC1_BLOCKED=1`: the probe first pre-checks
   that a direct client-side GET already fails (control established BEFORE the render), then
   requires the proxied page to render while the direct route stays broken.
   Record (2026-10-31, drop `AF65D193…7FF9` on both machines, target `http://httpforever.com/`):
   rows 1–3 pass — page rendered (`title='HTTP Forever - A reliably insecure connection'`, 2103
   body chars); the blocked control rendered identically while the same process's direct GET
   failed with `actively refused it` — server-origin proof satisfied. Rows 4–6 are pinned by the
   unit table (403/405/502 policy table, listener teardown) rather than a live probe run.
   Regression re-pass on the #56 drop `683FA932…A47A9C58` (run `20261031-235301`): same title
   and body size — and the fetch probe now provisions its own allowlist entry through the
   shared Management Client phase (a recreated workspace item proved assuming the entry wrong:
   the probe went red with 403 until it self-provisioned).

### SC-2 Vendor login flow through the proxy (#55)

Requires the same both-machine drop as SC-1. The #55 wire contract carries method/headers/body,
so client and Event Server MUST run the same build — DataContractJsonSerializer ignores unknown
members, so a skewed pair does NOT fail loudly: a new client against an old Event Server silently
degrades every POST to a body-less GET (the login loops back to the form). Deploys are
simultaneous.

1. Allowlist an http target that serves a login form: a real camera page, or the probe login
   server (`tests/ux/WebContent.UxTests`, `Category=probe` serves `/login` → POST → 302 +
   `Set-Cookie` → `/dashboard` gated on the cookie) on a machine the Event Server can reach.
2. In the proxied Smart Client workspace, submit the login form. Expected: the POST is relayed
   (no 405), the 302 + `Set-Cookie` come back untouched, and the follow-up GET of the landing
   page carries the browser's `Cookie` header to the fetcher — the landing page shows the
   signed-in state, not the form again.
3. The session cookie lives ONLY in the WebView2 profile: no cookie store exists on the Event
   Server side (`UseCookies`/`AllowAutoRedirect` off — pinned by `ProxyFetchTransportTests`).
   A login form that submits with a chunked body (modern `fetch()` + `ReadableStream` style —
   ordinary forms and XHR always carry Content-Length) answers 411 "Length Required" by design.
4. Automatable harness: `ScProxyLoginProbeTests` (probe category) — it allowlists its own
   login-server origin through Management Client, drives the login via CDP, and writes
   `sc-login-probe-results.txt` (+ dashboard screenshot) under
   `%TEMP%\webcontent-sc-proxy-probe\<run>`; the probe goes red when the dashboard is not
   reached or the cookie round-trip fails.

   Record (2026-10-31, drop `6C373E4D…FFE1` on both machines, target `http://172.16.128.210:18123`
   served by the probe's vendor-style login server): pass — login form POST relayed through
   messaging with its body (`server: POST /login body=35B`), the 302 + `Set-Cookie` round-tripped
   (browser shows `title='ACME Portal - Dashboard'`, `SIGNED IN`, `cookieEcho='COOKIE-HEADER
   [[SID=…]]'` — never the form again), and the page's background `fetch()` XHR reached the target
   with the session cookie attached (`server: GET /api/status cookie='SID=…'`, rendered as
   `XHR-OK status=online cookie='SID=…'`). Full probe run ≈1 min.

### SC-3 Proxied HTTPS targets: target-TLS verdict and machine trust (#56, ADR-0002/ADR-0003)

Requires the same both-machine drop as SC-1/SC-2 (the leaf contract is new on BOTH sides: an
old Smart Client never asks for leaves, an old Event Server never answers LeafRequest — the
tunnel then fails closed with a logged mint refusal), plus the one-time machine trust setup:

1. **Event Server loads its CA; an administrator provisions it when the service cannot create
   it**: the plugin loads (or, where the service account may write the machine store, creates)
   `CN=XProtect WebContent Plugin Loopback Proxy` in `LocalMachine\Personal`, private key in the
   machine key container `WebContent-Proxy-CA`. Live installs run the service as
   `NT AUTHORITY\NETWORK SERVICE` (read-only on that store), so the README's elevated
   `scripts/provision-event-server-authority.ps1` creates the CA once and grants the service read
   access to the key. A missing/unloadable CA answers every LeafRequest `NoAuthority` (logged,
   retried per request) — tunnels stay fail-closed.
2. **Each Smart Client machine trusts the CA once, elevated** (README "deployment certificate
   authority"): export the CA DER from the Event Server and
   `Import-Certificate -FilePath <ca.cer> -CertStoreLocation Cert:\LocalMachine\Root`. The plugin
   only READS the root stores: when the CA thumbprint is absent it logs the exact import command
   and the browser shows its own certificate error — never a silent bypass, never a store write.
3. **Fail-closed row**: allowlist `https://host:port` (NO flag) against a self-signed target.
   Expected: browser tunnel TLS to the loopback listener succeeds (trusted leaf), the Event
   Server refuses the target chain, and the browser shows the `Web Content proxy:` error page
   whose body names the per-target `accept untrusted certificate` switch. The target machine
   journals the TLS attempt.
4. **Override row**: same entry plus ` accept-untrusted`. Expected: full flow (form, POST,
   302+Set-Cookie, cookie-gated dashboard, background XHR) through the terminated tunnel.
5. Automatable: `ScProxyLoginProbeTests` probe facts `ProbeProxiedHttpsTargetFailsClosedWithout-
   Override` and `ProbeProxiedHttpsLoginFlowWithAcceptUntrustedOverride` (self-signed HTTPS
   `LoginTestServer` on `UX_HTTPS_LOGIN_TARGET`, default `https://172.16.128.210:18124`; each
   row rewrites its own allowlist line — same origin with a different flag state replaces, never
   contradicts — and writes `sc-login-probe-tls-{closed,open}-*.png/.txt` artifacts).
6. Live net472 plumbing proof (2026-10-31, compiled plugin DLL reflected under PS 5.1):
   unprivileged CA load throws (NoAuthority path); an elevated process creates the CA and a
   SECOND process reloads the SAME thumbprint with a usable `PrivateKey` (the machine CAPI
   container survives the process); product `ProxyLoopbackClientKey` SPKI → product
   `MintLeafForPublicKey` → `AttachLeaf` → real Tls12 `AuthenticateAsServer` handshake
   completes; chain builds leaf→CA (UntrustedRoot until the LM\Root import exists — correct
   fail-closed); `ProxyLoopbackTrust.IsRootTrusted` reads both root stores unelevated and
   reports False when the CA is absent.

   Record (SC-3 live rows) - PASSED live both-host (2026-10-31, drop `WebContent.dll`
   SHA-256 `683FA932...A47A9C58` hash-verified on Smart Client + Event Server):

   - Trust setup executed live: `scripts/provision-event-server-authority.ps1` (elevated, on
     the ES) created CA thumb `E4D254D58CB1BDA6E894C0F484F3C5AA718D2377` and granted
     `NT AUTHORITY\NETWORK SERVICE` read on the key file; the exported .cer was
     `Import-Certificate`d into the Smart Client machine's `LocalMachine\Root`.
   - Override row PASS (`ProbeProxiedHttpsLoginFlowWithAcceptUntrustedOverride`): ES log
     `Loaded deployment proxy CA... (thumbprint E4D254D5...)` + `Minted loopback proxy leaf...`;
     client log `deployment CA ... is present in this machine's trusted roots`, `tunnel TLS
     established`; the browser completed form -> POST /login -> 302+Set-Cookie -> cookie-gated
     dashboard -> background XHR: `signedIn='SIGNED IN' xhr='XHR-OK'`, cookie echo
     round-tripped; the target server journaled `GET /`, `POST /login`, `GET /dashboard`,
     `GET /api/status` carrying the session cookie.
   - Fail-closed row PASS (`ProbeProxiedHttpsTargetFailsClosedWithoutOverride`): the same
     allowlisted origin WITHOUT the flag - browser document `title='Web Content proxy: 502'`
     naming the per-target accept-untrusted switch; the target server journaled
     `TLS handshake failed` (the fetcher refused the self-signed chain mid-handshake).
     No login form, no bypass.

   Live defects these rows caught and fixed: the restricted service account's CA store open
   (read-only load fallback, step 1) and tunnel responses rendered as plaintext onto the raw
   socket instead of into the TLS stream (browser decryption failure; pinned by
   `ProxyForwardConnectionTests.WritesInnerTunnelResponsesIntoTheTunnelNotOntoTheRawSocket`).

   Artifacts: `%TEMP%\webcontent-sc-proxy-probe\<run>\sc-login-probe-tls-{open,closed}-*.png/.txt.

   Full-suite green (2026-10-31, drop `683FA932…A47A9C58` hash-verified on both hosts, one
   detached xunit run, filter `~ProbeProxied`): fetch `20261031-235301`, login
   `20261031-235408`, tls-open `20261031-235514`, tls-closed `20261031-235635`. The override
   row rewrote its own allowlist line through the live Management Client edit → guarded Save
   → verified-readback path, and the suite left the live allowlist at the fail-closed default
   (no flags).

### SC-4 WebSocket CONNECT tunnel round trip (#57)

Requires the same both-machine drop as SC-1 (the six tunnel message ids are new on BOTH sides:
an old Smart Client never sends TunnelOpenRequest, an old Event Server never answers one - the
upgrade then fails closed as a 502 page, never smuggled into the fetch path). One allowlist
entry: the ws origin spelled as its http twin (default `http://172.16.128.210:18125`).

1. **Tunnel row**: open `http://172.16.128.210:18125/` in a proxied workspace view. The served
   page issues `new WebSocket('ws://172.16.128.210:18125/echo')` and sends a random token;
   live Chromium proxies that as `CONNECT 172.16.128.210:18125` plus a PLAINTEXT websocket
   handshake inside the tunnel (live repro - not the absolute `GET ws://…` form RFC 6455
   describes; both are handled). The listener accepts the CONNECT only because the origin is
   allowlisted as its http twin, verifies the inner head is a valid ws upgrade before any Event
   Server socket opens, and opens the byte tunnel. Expected: the page shows `WS-OK:<token>` -
   its own token echoed - while the ws server journals `WS-HANDSHAKE /echo` plus
   `WS-ECHO <n> bytes`. Client-side proof and server-side
   proof together: the upgrade crossed browser -> loopback listener (decision: tunnel, not
   fetch) -> TunnelOpenRequest over MIP messaging -> Event Server raw socket + rebuilt
   origin-form handshake -> target 101 relayed VERBATIM -> raw-frame pumps in both directions,
   with no HTTP request/response chunking anywhere on the byte path.
2. **Idle/half-open reclaim + caps**: unit-pinned (`ProxyTunnelCorrelationTests`,
   `ProxyTunnelServerTests`); live, the ES `EsMaxTunnels` cap answers the 9th simultaneous
   open with a logged refusal while the first eight keep streaming.
3. Automatable: `ScProxyWebSocketProbeTests.ProbeProxiedWebSocketEchoRoundTrip` (probe
   category, self-provisioning allowlist entry, raw-socket `WebSocketEchoServer` on
   `UX_WS_TARGET`, default `http://172.16.128.210:18125`; writes `sc-ws-probe-*.png/.txt`
   artifacts under `%TEMP%\webcontent-sc-proxy-probe\<run>\`).

   Record (SC-4 live row) - **passed 2026-11-01** on drop `49E4F8C7…A6EEE3` hash-verified on
   both hosts (BEAST Smart Client + vms.joshooaj.com Event Server), one detached xunit run
   (filter `~ProbeProxied`), all five live rows green: ws `20261101-021941`, fetch
   `20261101-022105`, login `20261101-022211`, tls-open `20261101-022318`, tls-closed
   `20261101-022437`. The ws row closed both ways: page verdict `WS-OK:WS-TOKEN-aro1gp694g`
   (token echoed through the whole chain) and ws-server journal `WS-HANDSHAKE /echo` +
   `WS-ECHO 19 bytes` + `WS-CLOSE client`. Smart Client plugin log for the same session:
   `Proxy CONNECT to '172.16.128.210:18125' allowlisted as an http twin; expecting a plaintext
   ws:// handshake in the tunnel` then `Proxy websocket CONNECT tunnel to
   '172.16.128.210:18125' established; pumping bytes in both directions.` — the live repro
   that found the CONNECT-carry posture: an earlier run on the pre-fix drop (`B4EAE436…488466`)
   showed the browser's upgrade arriving as CONNECT and being refused, which is what drove
   the `ConnectWebSocketTunnel` disposition.

### SC-5 Explicit proxy error pages and the Event Server outage demo (#58)

Requires the same both-machine drop as SC-1. The refusal posture: a proxied workspace NEVER
loads anything direct from the Smart Client machine; every failure kind renders its own page in
the view (title `Web Content proxy: <status>`) whose body names which side failed.

1. **Start-URL refusal row**: set the workspace home address (Management Client pane
   `txtHomeAddress`) to an http origin the workspace does NOT allowlist (e.g.
   `http://neverssl.com/`). Open the workspace: the start document itself is the 403 page
   ("not in the workspace proxy allowlist") - no address-bar interaction, no fallback page,
   no spinner. Since #66 the page is the friendly cause-category page (cause chip "Not allowed
   by the administrator" + plain headline up front; the exact origin, the canonical allowlist
   entry that would admit it, and the verbatim refusal text in a collapsed "Technical details"
   section with a Copy diagnostics button). #88 adds the **https sibling**: an off-allowlist
   https home (e.g. `https://neverssl.com/`) renders the SAME page, committed at the requested
   URL - but answered at the VIEW layer, because Chromium transports https to the loopback
   listener only as CONNECT and drops the body of a refused CONNECT, so the proxy's own page
   can never reach the screen for https. Restore the home address afterwards.
2. **Outage row (the ticket demo)**: with a healthy proxied page rendered, stop
   `MilestoneEventServerService` on the Event Server machine (WinRM, jhadmin creds per
   `AGENTS.md`; poll to `Stopped` - the stop is slow). Reload the proxied page: within
   seconds (transmit fails on the disposed messaging channel) or at most the 30 s client
   deadline (silence = no fetcher listening) the view shows the 503 page naming the missing
   component ("The VMS server proxy component is not available... never loaded direct from
   this computer"). Smart Client MIPLog marker: `Proxy fetch for <uri> failed on the Smart
   Client side before any VMS fetcher answered (code=ServerUnavailable)`.
3. **Recovery**: start the service again (poll `Running`; its plugin load needs a few
   seconds). The running client's messaging channel stays disposed (an ES restart disposes
   every client's `MessageCommunication` - `AGENTS.md`), so recovery is a fresh Smart Client
   session: relaunch, log in, navigate - the page loads again, no error page.
4. Distinctions pinned at the unit table: `NotAllowed` (server allowlist drift) → 403, never
   the "could not reach" wording; `TooLarge` → page naming the byte cap; target-leg `Timeout`
   failure frame → 504 with "VMS server could not get an answer from the target"; local
   allowlist refusal → the same 403 page as (1).
5. Automatable: `ScProxyErrorProbeTests` (probe category; rows
   `ProbeProxiedStartUrlOutsideAllowlistShows403Page`,
   `ProbeProxiedStartUrlHttpsOutsideAllowlistShows403Page` (#88), and
   `ProbeEventServerOutageShowsProxyErrorPage`; artifacts
   `sc-error-probe-{starturl403,starturl403-https,healthy,es-down,recovered}.png` + results .txt under
   `%TEMP%\webcontent-sc-proxy-probe\<run>\`). The outage row mutates the live VMS and only
   runs with `WEBCONTENT_ES_STOP=1` (row goes red without the flag, never silently skips); it
   restarts the service itself for the recovery half, and the finally block restarts it again if
   anything aborted while it was down. The start-URL row restores the previous home address
   through the guarded Management Client path.

    Deployment prerequisite (2026-11-01 loop): the rows need the Smart Client machine's MIP root
    at the same drop the Event Server runs — the 503 `ServerUnavailable` page is #58 *client*
    code, and BEAST is still on the #57 drop (`49E4F8C7…A6EEE3`). The ES drop
    (`84623C10…697AA41`, DP-1 record 1) was staged byte-identically via `Copy-Item
    -FromSession` under `.cache\es-drop-84623C10\`, so the SC copy only needs the one elevated
    consent already handed off in DP-1 record 2. Elevation attempts 2026-11-01 ~04:15/~04:25
    (AGENTS.md `Start-Process -Verb runas`, no `-Wait`): the RDP session is connected, so unlike
    the DP-1 attempts (auto-cancelled) the consent prompt stayed PENDING until the idle timeout
    cancelled it — nobody was at the desktop. The unattended loop re-attempts the elevated copy
    each wake; once the deployed `WebContent.dll` hashes to `84623C10…697AA41`, run the rows:
    `dotnet test tests/ux/WebContent.UxTests --filter "FullyQualifiedName~ScProxyErrorProbeTests"`
    plus `$env:WEBCONTENT_ES_STOP='1'` for the outage row.

2. **Live-host attempt (2026-11-01, not verified - needs one unlocked desktop).** `vms.joshooaj.com`
   is a second candidate view machine: it already runs the current drop (`84623C10.697AA41` verified
   by file hash in `C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin`) and has Smart
   Client + Management Client installed, so the rows can run there without the DP-1 consent. That
   splits the Management Client phase from the view phase - only the first box needs to drive MC
   comfortably, and the workspace state lives on the VMS, so the view machine may trust it. Added
   for that: `UX_SKIP_MC_PROVISIONING=1` (the row logs the control, never skips it silently, and
   whoever sets it owns restoring the home address afterwards).

   What the attempt found, and the thing that stops it: **an idle-locked interactive session makes
   every injected-input route fail, and the probe cannot tell that from a broken login.** With the
   session at its lock screen, UIA reads and `Value`-pattern writes still work, but UIA `SetFocus`
   throws "Operation is not valid due to the current state of the object", FlaUI `Mouse.MoveTo` /
   `LeftDoubleClick` are dropped (the cursor does not move), and `SetCursorPos` returns false while
   `GetCursorPos` keeps reporting the old position. So the row reaches the login dialog, fills it,
   and then dies in the Management Client tree (`Could not collapse the Management Client tree to
   the Home root`) because the clicks land nowhere. Both candidate desktops lock on idle (BEAST
   session 1 after ~2 h, the vms session after ~40 min) and the lock lives on the secure desktop:
   there is no unattended route past it (`consent.exe` auto-cancels; winlogon owns the desktop).
   No live workspace state was mutated by any of these attempts - every run failed before the first
   Management Client Save - so no home-address restore is outstanding.

   Human step for the SC-5 rows: on a machine whose MIP root hashes to `84623C10.697AA41` and whose
   interactive desktop stays unlocked for the run (screensaver/lock off), with the repo `.env`
   present, run

   ```powershell
   $env:WEBCONTENT_ES_STOP = '1'   # only for the outage row; it stops/starts the live Event Server
   Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~ScProxyErrorProbeTests' }
   ```

   Harness hardening landed while diagnosing it (`LoginFlow`, `ManagementClientUi`): the password is
   entered through the UIA `Value` pattern before the focus+keyboard route (a dialog with no
   foreground to give is the normal case for a probe launched beside a busy desktop), `AutomationId`
   reads are guarded because the focused element can be a window whose provider does not implement
   the property, the Management Client window is raised (attach to the foreground thread + topmost)
   before any mouse-driven tree route - a click at tree coordinates otherwise lands on whatever
   window overlaps it - and the auto-login race catch also accepts `NoClickablePoint` /
   `ElementNotEnabled` as "the dialog vanished because the client signed itself in".

3. **Live pass, outage row (2026-11-01 ~08:20-08:26) - PASSED, run `20261101-082437`.** The
   deployment prerequisite is closed: at ~08:19 a fresh elevation was granted on BEAST (the
   hardened `-File` payload transcript records `elevated: True`) and the staged drop copied in -
   BEAST's MIP root now verifies per file to `84623C10…697AA41`, identical to the Event Server.
   The outage row ran both-machine: baseline proxied page rendered (`title='HTTP Forever - A
   reliably insecure connection'`), `MilestoneEventServerService` stopped over WinRM, reload
   surfaced `503` with title `Web Content proxy: 503` and the body naming the missing component
   ("...No proxy-fetch answer arrived from the VMS Event Server within 30s (exchange
   96d6169fd93b4f95ad2a911130591288)..."), MIPLog.txt carried the `ServerUnavailable` marker, and
   the row's finally restored the service (`Running`; the next row's healthy baseline proves the
   plugin reloaded). The allowlist `Ensure` short-circuited ("already allowlisted; no Management
   Client edit needed") - the #54-#57 entries are still live on the workspace.
   The start-URL row, run FIRST in the same `dotnet test`, failed before its first live change:
   `CollapseTreeToRoot` timed out with every level-1 node reporting four no-op strikes, while the
   identical route succeeded minutes later in the outage row (`tree collapsed to root (realized
   items=18...)`). Same locked-desktop injection window as record 2, but intermittent: the session
   flips between usable and no-op windows (consent-era secure-desktop churn / RDP re-lock), and
   `SetCursorPos` succeeding is a necessary but not sufficient usable-window signal. No workspace
   state was changed by that failed attempt; next wake re-runs only that row.

4. **Live pass, start-URL row (2026-11-01 ~08:46-08:48) - PASSED, run `20261101-084616`.**
   Re-run alone in a fresh usable-desktop window. Management Client phase clean: the tree collapse
   succeeded at strike 1 (`tree collapsed to root (realized items=18...)`) - the intermittency of
   record 3 confirmed, nothing in the product at fault. Home address `https://joshooaj.com/` ->
   `http://neverssl.com/` (verified readback), and the workspace's FIRST document - no address-bar
   interaction - was the refusal page: title `Web Content proxy: 403`, body "The request origin
   'http://neverssl.com' is not in the workspace proxy allowlist." Home restored to
   `https://joshooaj.com/` (verified readback) by the row's finally. With record 3 both SC-5 rows
   are green: the ticket's live demo and the start-URL refusal.

5. **Recovery half automated + live PASS (2026-11-01 ~13:06-13:09), run `20261101-130629`.** Step 3
   above was only described in records 3-4, not asserted, so the outage row now finishes the demo
   itself: after the service is back (row starts it itself, 10 s for the fetcher's plugin load) it
   relaunches the Smart Client, signs in, selects the workspace and navigates - the proxied document
   must come back, not an error page. Live result: `baseline title='HTTP Forever - A reliably
   insecure connection'` -> ES stopped over WinRM -> reload `503`, committed title
   `Web Content proxy: 503` naming the component (exchange `56ddae5fcbb243a4b8b8018eccaceaca`),
   `ServerUnavailable` marker found in MIPLog.txt -> ES `Running` -> **fresh session title back to
   `HTTP Forever - A reliably insecure connection`**, body carrying the real page text, screenshot
   `sc-error-probe-recovered.png` (the green HTTP Forever header, unlocked padlock). New assertions:
   the recovered title must not start with `Web Content proxy:` and must equal the baseline title;
   the recovered body must not contain `Web Content proxy:`. The finally block now restarts the
   service only if it is still down (the row normally restarts it mid-row for this half), so the
   "manual recovery required" line can no longer fire after a clean run.
   Both rows were also re-run green by hand immediately before, after a couple of attempts aborted
   with `No addressBar in the proxied workspace view` (the outage row refuses rather than silently
   navigating elsewhere; its finally restarted the Event Server each time, so the service was never
   left down): start-URL row `20261101-120323` (home `http://httpforever.com/` ->
   `http://neverssl.com/` -> title `Web Content proxy: 403` -> restored, verified readback) and
   outage row `20261101-120540` (same 503 posture, `ServerUnavailable` marker, service `Running`
   afterwards). BEAST's deployed `WebContent.dll` for these runs: `760F1A69…5B507C`,
   `FileVersion 0.1.186.31842` - a later build of the same sources as the Event Server's
   `84623C10…697AA41`, which is why the hashes differ (the assembly version is stamped per build).

6. **#66 friendly refusal page - start-URL row PASSED 2026-11-02 ~23:20 (run `20261102-232047`).**
   The refusal page re-designed by #66 (explicit cause categories, collapsed technical detail) on
   both machines (both `WebContent.dll` SHA-256 `5269DCEA…1326AD` before the run, verified by file
   hash not timestamp). Management Client phase clean (allowlist `Ensure` short-circuited; home
   `http://httpforever.com/` -> `http://neverssl.com/` verified readback), and the row's NEW
   assertions - not just the old title/body ones - all held on the live page: title byte-compatible
   `Web Content proxy: 403`; cause chip "Not allowed by the administrator" and headline "This web
   address is not allowed for this view" in the body; the `details` element exists and reads
   `open == false` (technical section collapsed by default); the `#wdc-copy` Copy-diagnostics
   button is present; the collapsed section carries "not in the workspace proxy allowlist" (the
   verbatim refusal, proving the #58 sentences still ship) AND "Allowlist entry that would admit
   this origin: http://neverssl.com" (the actionable fix). Screenshot `sc-error-probe-starturl403.png`
   shows the rendered card. Home address restored to `http://httpforever.com/` (verified readback).
   The outage row's assertions (title `Web Content proxy: 503`, "VMS server proxy component is not
   available", "never loaded direct from this computer") were kept verbatim through the wording
   layer - the explanation texts carry those exact sentences - so the outage posture needs no
   re-run beyond the unit table for this change.

7. **#88 https refusals at the view layer - https start-URL row PASSED 2026-11-03 ~02:07-02:09
   (run `20261103-020728`), http sibling re-passed (`20261103-020505`).** Root cause confirmed as
   the ticket suspected: the forwarder DOES write the full 403 + friendly page as the refused
   CONNECT's answer body (unit pin: `ProxyForwardConnectionTests.WritesTheFullForbiddenPageAsTheConnectAnswerBody`),
   and Chromium discards that body and renders its native `ERR_TUNNEL_CONNECTION_FAILED` page -
   the pre-fix live half of the pair showed exactly that page at `https://neverssl.com/`. Fix:
   proxied views register a `WebResourceRequested` filter (`https://*/*`, only when a listener
   exists); the shared-grammar gate `ProxyViewRefusal` answers the off-allowlist https refusal
   with the identical #66 403 page BEFORE any CONNECT opens, committed at the requested URL.
   The http row never double-answers (absolute-form http stays on the proxy's own path) and
   ws/wss upgrades keep the CONNECT posture (the event does not surface websocket requests) -
   documented asymmetry in README. Decision record: option 2 (view-level interception) chosen over
   terminate-then-mint-for-blocked-hosts (would widen the deliberately allowlist-gated leaf-mint
   gate) and over accepting the limitation (the http row already proves the user-facing posture
   the https row now matches). Both machines at drop `E17995E1…5479BDFC` (`FileVersion
   0.1.261.61565`, verified by file hash). Live facts asserted, not screenshot-only: home
   `http://httpforever.com/` -> `https://neverssl.com/` (verified readback); the workspace's first
   document URL starts `https://neverssl.com` (same committed-history posture as the http row);
   title byte-identical `Web Content proxy: 403`; cause chip + headline in the body; `details`
   reads `open == false`; `#wdc-copy` present; the collapsed section carries "not in the workspace
   proxy allowlist" AND "Allowlist entry that would admit this origin: https://neverssl.com" (the
   https twin, the scheme actually requested); home restored to `http://httpforever.com/`
   (verified readback). Smart Client MIPLog marker: `WebContent proxy: view-level https refusal
   for browser <id>: GET 'https://neverssl.com/' ... No CONNECT was opened and the browser is
   never taken direct.` (also fires for the favicon subresource). #69/ES side: the view's report
   rides the ordinary tally - the Event Server log gained `Proxy block recorded: ...
   origin='https://neverssl.com' reason=NotAllowed client='BEAST'`. The http row re-passed on the
   same drop with the new event handler registered, so the absolute-form path is unregressed.
   Screenshot `sc-error-probe-starturl403-https.png`.

### DP-1 Event Server deployment script and rollout records (#59)

The proxied feature is both-machine; the Event Server side has a deployment the installer
cannot shortcut (plugins load only at service start). `scripts/deploy-event-server-plugin.ps1`
is the remote, repeatable form of the documented procedure: local pre-check that the drop
carries a `plugin.def` declaring the `Service` environment (an old def copies fine and then
silently never loads - refused up front), WinRM with explicit credentials, stop
`MilestoneEventServerService` and WAIT for `Stopped` (the stop is slow and the dll is locked
until then), copy into the installer's `INSTALLFOLDER`, verify EVERY copied file by SHA-256
against the local source (never by timestamp - the copy preserves it), start and wait for
`Running`, then print the rollout follow-ups: fetcher-ready marker check, mandatory Smart
Client restart after the ES restart, and the one-time CA provisioning pointer.

1. **Record (script validated live end-to-end) - PASSED 2026-11-01** against
   `vms.joshooaj.com`: drop built from HEAD (post-#58), `WebContent.dll` SHA-256
   `84623C10…697AA41`, all 11 drop files hash-identical in
   `C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin`, service back to `Running`,
   and the ES `logs\MIP.log` gained `Proxy fetcher ready (concurrency=3,
   request id=WebContent.Proxy.FetchRequest)` at startup. ES machine product: **XProtect 2024
   R2** (event server binaries file-version 24.2a) - the README's "verified against" line.
   - Live defect the run caught and fixed: the script's stop-wait loop hung while the service
     was actually stopped - WinRM deserializes `ServiceControllerStatus` as its underlying
     **Int32**, so the remote `$state -eq 'Stopped'` comparison never matched; the poll now
     calls `.ToString()` remotely. The script's log-location note was also corrected to the
     observed layout (`%ProgramData%\Milestone\XProtect Event Server\logs\MIP.log` plus
     `logs\MIPLogs\` rolled archives).
2. **Fresh-both-machine smoke row (SC-1 procedure against a fresh drop) - PASSED 2026-11-01
   09:12.** Both MIP roots hash-verified at the same HEAD drop (`84623C10…697AA41`): the Event
   Server side via record 1, BEAST side staged byte-identically (`.cache\es-drop-84623C10\`) and
   elevated-copied by the #58 loop (~08:30, first UAC consent a human approved - the overnight
   unattended attempts all auto-cancelled; see SC-5 record 2 for the locked-desktop diagnosis).
   With the elevated hosts control `127.0.0.1 httpforever.com # webcontent-sc-1-block` in place,
   `WEBCONTENT_SC1_BLOCKED=1 dotnet test tests/ux/WebContent.UxTests --filter
   FullyQualifiedName~ScProxyFetchProbeTests`: the pre-check confirmed a direct client-side GET
   already fails, a fresh Smart Client session then RENDERED `http://httpforever.com/`
   (`title='HTTP Forever - A reliably insecure connection'`, 2103 body chars) while the
   post-render direct client GET still failed - the page arrived only over
   loopback listener -> MIP messaging -> Event Server fetcher. Artifacts:
   `%TEMP%\webcontent-sc-proxy-probe\20261101-091122\sc-proxy-probe-page.png` +
   `sc-proxy-probe-results.txt`. Hosts entry removed after the row. (The 09:09 first attempt
   died in the Management Client phase on a transient UIA `0x80040201`; the retry passed.)
   - **Elevation received (2026-11-01 ~08:19)**: a fresh elevated copy was granted on BEAST and
     the staged drop landed - every file in `CascadiaWebContentPlugin` verifies SHA-256-identical
     to `84623C10…697AA41` (same per-file verification this script does remotely). Steps 2-4
     (blocked smoke row) still need an unlocked desktop on that machine; the hosts entry itself
     needs one more elevated write.
   3. **Fresh-both-machine smoke row - PASSED 2026-11-01 ~09:03-09:04 (run `20261101-090329`).**
      Against the fresh drop on both machines (BEAST Smart Client + `vms.joshooaj.com` Event
      Server): the second elevation of the cycle added the hosts control
      (`127.0.0.1 httpforever.com # webcontent-sc-1-block`), the row's pre-check confirmed the
      direct client-side GET fails BEFORE navigating, the proxied workspace browser then rendered
      `title='HTTP Forever - A reliably insecure connection'` (2103 body chars) - the page provably
      originates on the Event Server machine while the client itself cannot reach the target.
      Afterwards the elevated payload removed the hosts entry (record 2 step 4) and the workspace
      home address is untouched (`https://joshooaj.com/`, last verified by the SC-5 restore).
      The last open acceptance row of #59 is thereby satisfied.

### SC-6 wss:// websockets through the terminated TLS tunnel (#60)

Requires the same both-machine drop as SC-1 (both machines must carry the #60 build - an older
Event Server keeps refusing https-scheme tunnel targets, which makes these rows fail loudly,
never silently) plus the SC-3 one-time deployment CA provisioning, so the browser's tunnel TLS
to the loopback listener validates cleanly and any visible failure is the TARGET certificate
decision, not the loopback leaf.

The posture lifted from #57: a wss:// upgrade riding an allowlisted HTTPS origin is now
tunnelled - never fetched, no longer refused 501. The loopback listener accepts the CONNECT and
terminates the tunnel TLS with an ES-minted leaf; the in-tunnel upgrade GET rides the existing
byte-tunnel protocol; the Event Server dials a FRESH TLS leg to the target, admits only a 101,
relays it verbatim, then pumps decrypted frames both ways. #61 wired the per-target
accept-untrusted override into that leg: the entry for exactly this origin decides whether the
tunnel's own SslStream accepts an untrusted chain - full chain validation everywhere else, never
a global bypass, never the browser-side loopback termination. The chain-trust success row
imports trust instead: `WssTargetTrust` copies the probe certificate's public DER into the Event
Server machine's `LocalMachine\Root` over WinRM (jhadmin creds from `.env`, per `AGENTS.md`;
idempotent and self-cleaning by probe CN), skipped with `UX_WSS_TRUST_IMPORT=0` when the target
is trusted by other means.

Automatable: `ScProxyWssProbeTests` (probe category; artifacts
`sc-wss-probe-page.png` / `sc-wss-probe-tls-closed-page.png` / `sc-wss-probe-accept-untrusted-page.png`
+ results .txt under `%TEMP%\webcontent-sc-proxy-probe\<run>\`):

1. **Success row** `ProbeProxiedWssEchoRoundTrip`: the page is served from the plain http page
   origin and dials `wss://<https origin>/echo`; the workspace allowlist carries BOTH origins
   (the https entry WITHOUT any flag - the entry scheme is the posture). Expected: the page
   shows `WS-OK:<token>` (client-side proof) while the wss server journals `TLS-HANDSHAKE`,
   `WS-HANDSHAKE` and `WS-ECHO` (server-side proof the ES dialed TLS and replayed the upgrade).
2. **Fail-closed row** `ProbeProxiedWssTlsChainFailureRefusesBeforeTheUpgradeArrives`: a second
   https origin, same entry posture, certificate deliberately NOT imported. Expected: the page
   reports `WS-FAIL` (the browser never gets a tunneled TLS 101) and the target journals the TLS
   leg being dialed (`TLS-CONNECT`) but never the upgrade handshake - the Event Server refused
   the chain before replaying it. The page itself renders fine (plain http origin).
3. **Override row** (#61) `ProbeProxiedWssAcceptUntrustedOverrideAdmitsSelfSignedTarget`: the
   SAME self-signed origin as the fail-closed row, certificate still deliberately NOT imported -
   but the workspace allowlist entry now carries the per-target accept-untrusted flag (probe
   entry shape `<origin> accept-untrusted`; the MC grid renders it as the row checkbox, and the
   provisioning readback pins the flag). Expected: the Event Server's tunnel TLS leg takes the
   flag instead of chain trust - the page shows `WS-OK:<token>` and the target journals
   TLS-HANDSHAKE + WS-HANDSHAKE + WS-ECHO. Each row re-Ensures its own flag state for the
   origin, so the flag never leaks between rows; the Event Server refreshes its allowlist union
   at most once per 60 s, and the minutes between rows' Management Client phases comfortably
   exceed that window.

Defaults are the lab's server-network addresses; override with `UX_WSS_PAGE_TARGET` (default
`http://172.16.128.210:18125`), `UX_WSS_TARGET` (default `https://172.16.128.210:18126`),
`UX_WSS_UNTRUSTED_TARGET` (default `https://172.16.128.210:18127`), `UX_ES_HOST` (defaults to
the `VMS_ADDRESS` host):

```powershell
Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~ScProxyWssProbeTests' }
```

**Live rows PASSED 2026-11-01** (Event Server deployed via `scripts/deploy-event-server-plugin.ps1
-Confirm:$False` - pwsh 7.6.9 NREs on the non-interactive `ShouldProcess` prompt; the copy-lock
retry of `053e22e` was earned on this rollout. BEAST at the matching #60 drop, `WebContent.dll
EFBA5B80…52D8` hash-verified, via a human-consented elevated copy - consent requests from an
unattended agent chain never surface on the desktop, so the copy itself was the one human step).
**Fail-closed row**, run
`20261101-174151`: page verdict `WS-FAIL:error` while the untrusted wss target journal read
`TLS-CONNECT peer` -> `TLS-HANDSHAKE ok` -> `connection fault: IOException: The decryption
operation failed` - the Event Server negotiated the TLS leg, refused the chain, and aborted
before replaying the upgrade (`WS-HANDSHAKE` never appeared; the journal shape also confirms the
review's note that Schannel surfaces the refusal as a wrapped IOException, which is exactly what
the `ProxyTunnelServer` inner-chain catch classifies as `TlsValidation`). **Echo row**, run
`20261101-174657`: `WssTargetTrust` imported the probe certificate (`B7304600…5FE6`) into the
Event Server's `LocalMachine\Root` over WinRM, then the proxied page showed
`WS-OK:WSS-TOKEN-do7amc35w2i` while the wss server journal read `TLS-CONNECT` ->
`TLS-HANDSHAKE ok` -> `WS-HANDSHAKE /echo` -> `WS-ECHO 21 bytes` -> `WS-CLOSE client` -
browser -> CONNECT -> loopback-terminated TLS -> in-tunnel upgrade -> MIP byte tunnel ->
Event Server TLS leg -> target 101 -> decrypted frames both ways, end to end. Artifacts
`sc-wss-probe-tls-closed-page.png` / `sc-wss-probe-page.png` + results .txt in those run dirs.
Found and fixed during the run: `WssTargetTrust` treated the bare `VMS_ADDRESS` hostname as a
uri and died before the import (`7813b73`).

**#61 override row PASSED 2026-11-02** (run `20261102-113540`, both machines hash-identical at
the new drop, ES `Proxy fetcher ready` 11:34:34): the live Management Client saved
`https://172.16.128.210:18127 accept-untrusted` through the Proxy-tab grid (audit line +
reloaded-pane double verification), the probe certificate was deliberately NOT imported
anywhere, and the proxied page showed `WS-OK:WSS-TOKEN-yu4ub8qkkhh` while the wss server journal
read `TLS-CONNECT peer` -> `TLS-HANDSHAKE ok` -> `WS-HANDSHAKE /echo` -> `WS-ECHO 21 bytes` ->
`WS-CLOSE client` - the accept-untrusted flag, scoped to its own origin, admitted the
self-signed chain on the Event Server's tunnel leg. The two #60 twin rows re-passed on the same
drop, pinning that the wiring neither widens nor narrows the default posture: **fail-closed
row** run `20261102-114705` (same origin, flag removed, `WS-FAIL:error`; the target journal
read only the TLS dial + negotiate then `connection fault: IOException: The decryption
operation failed`, and the Event Server logged `Proxy block recorded
origin='https://172.16.128.210:18127'` at 11:48:45 while minting the loopback leaf for the
same origin worked); **trusted-echo row** run `20261102-115053` (no flag, certificate imported
into the Event Server's `LocalMachine\Root` over WinRM, `WS-OK:WSS-TOKEN-n71gu47y88` with the
full journal) - full chain validation still ACCEPTS valid chains through the wired resolver, so
the fail-closed row's refusal is the chain, not the wiring. Environmental flake along the way:
the first fail-closed attempt (`20261102-114002`) hit the known Smart Client launch window
while the Event Server service restarted (fetcher-stopped 11:43:29 / fetcher-ready 11:43:47);
it passed on re-run. Artifacts `sc-wss-probe-accept-untrusted-page.png` / `-results.txt` in
those run dirs.

The override row passed once more as an independent confirmation on the `90625f3`-hardened
harness: run `20261102-115638` (drop `7015417E…DAEDA9` hash-verified on both machines via the
psake `Deploy` task, no elevation) - the MC grid saved `https://172.16.128.210:18127
accept-untrusted`, certificate in NO trust store, page
`WS-OK:WSS-TOKEN-fmd8ql29fy8`, journal TLS-CONNECT -> TLS-HANDSHAKE ok -> WS-HANDSHAKE /echo ->
WS-ECHO 21 bytes -> WS-CLOSE client. Housekeeping from that session: a failed probe orphans a
`testhost` process that locks the UxTests dll - kill stale `testhost` processes before
re-running TestUI.

### SC-7 Setup-mode blocked-resources indicator (#83)

Automatable, unit: `ProxyBlockTallyTests` — the per-view tally in front of the #69
`ProxyBlockReporter`: one entry per origin+reason with hit counts (canonical origin display,
"(N hits)" label), most-recently-seen-first snapshot promoted on re-sighting, bounded at 200
entries with least-recently-seen eviction, `Reset` (per navigation) clears + raises `Changed`
once and no-ops quietly when empty, null/empty refusals count nothing, a throwing ES forwarder
(dead channel) never reaches the browser flow and never costs a local count, forwarder swap to
null (listener stop) detaches cleanly, and the production composition (tally → `ProxyBlockReporter`)
sends exactly one `ProxyBlockReportPayload` per refusal while also counting locally.

Live: `ScProxyBlockedIndicatorProbeTests.ProbeSetupModeBlockedResourcesIndicator` (probe
category; artifacts under `%TEMP%\webcontent-sc-proxy-probe\<run>\`) — `BlockedResourcePageServer`
serves `/blocked-page/` (document + two `<img>`s + a `<script>` from the deliberately
never-allowlisted origin `http://<host>:18131`) and a `/clean/` twin, both from the
already-allowlisted page origin `http://<host>:18125` (no Management Client phase;
`UX_REQUIRE_MC_PROVISIONING=1` restores the guarded Ensure). The run drives Smart Client on UIA
patterns + CDP only — no SendInput: navigation is `page.GotoAsync` (also keeps the probe url off
the workspace), the last-shown workspace must already be the proxied one (pattern-select fallback
via `SmartClientUi.TrySelectWorkspaceWithoutInput`), captures use `Artifacts.WindowPrint`
(PrintWindow; `CopyFromScreen` throws "handle is invalid" or hangs outright on a disconnected
session). Rows: page renders (title proves the allowlist entry survived); the indicator is NOT
rendered in runtime mode despite the client-side refusals; entering setup mode shows
"N resources blocked" (`blockedIndicatorText`); invoking the button lists `blockedResourceOrigin`
(canonical, matching the listener's refusal), `blockedResourceReason` ("not allowed by the
workspace allowlist"), `blockedResourceCount`; the clean twin navigation resets the tally (row
and indicator gone); runtime re-blocking stays invisible and re-entering setup mode re-shows the
tally.

**Live rows pending (2026-11-02)**: the Smart Client refuses to START on this machine's
disconnected desktop session — `ClientLog.txt` "Video player validation failed ... Error creating
Direct3D9Ex object!" and a `Cannot start` dialog ("There is a unknown issue."), reproduced three
consecutive attempts (05:29/05:31/05:34, `ReconTests.Probe_SmartClientStartupOnDisconnectedDesktop`
captures the dialog text); the plugin drop itself is fine (loaded cleanly in the connected-session
runs earlier that day, ES hash-verified). Reconnect the console (`tscon 1 /dest:console` needs
elevation an unattended Medium-IL session cannot get) and re-run:

```powershell
Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~ProbeSetupModeBlockedResourcesIndicator' }
```

Companion recon fact `ReconTests.Probe_ManagementClientTreeSelectionWithoutInput` (diagnostic,
never asserts) settled the MC-phase question while MC starts fine on the same session: the plugin
rows (`Cascadia Technology`, `Web Content Workspaces`) are already realized, but the tree items
expose NO ExpandCollapse pattern and LegacyIAccessible `DoDefaultAction` only SELECTS (never
toggles expansion) — workspace children never realize without a real double-click, so any
MC-provisioning phase is input-injection-dependent by nature (FlaUI SendInput fails with
ERROR_ACCESS_DENIED on a disconnected session; UIA patterns/SetValue/Invoke all work).

### SC-8 OS (WinINET) proxy inheritance for non-proxied views; proxied views ignore it (#67)

The question this closes is a documentation question with evidence: an admin asking "make the
web views use our corporate proxy" is asking for OS-level behavior. The plugin injects NO
proxy arguments into a non-proxied view's browser, so that browser resolves its proxy from the
OS - on Windows, the per-user WinINET "LAN settings" proxy - while a proxied view's browser is
launched with the loopback pin (`ProxyBrowserArguments.For`: every scheme to the loopback
listener + `--proxy-bypass-list=<-loopback>`), which makes the system proxy moot. Both halves
are now live-observed, not inferred.

Automatable harness pieces (ux project only; no plugin code changed):

- `WinInetProxyHarness` - a loopback plain-HTTP forward proxy that answers every
  absolute-form GET with a marker page (`WebContent OS-proxy harness` /
  `WININET-OS-PROXY-ROUTE-CONFIRMED`) and journals every request head (method,
  absolute-form target, User-Agent). It never tunnels (CONNECT is journaled + 403) and never
  dials upstream, so a journaled request IS an OS-proxy request.
- `WinInetProxyControl` - snapshot / apply / restore of the exact HKCU WinINET values
  (`ProxyEnable`, `ProxyServer`, `ProxyOverride`, `AutoConfigURL`) plus the
  `InternetSetOptionW(SETTINGS_CHANGED/REFRESH)` broadcast, so the probe changes the machine
  exactly as the LAN-settings dialog would and puts it back verbatim.
- `BrowserProcessArgs` - the live `Win32_Process` command lines of the plugin's
  `msedgewebview2.exe` processes (scoped by `--user-data-dir` marker), which is where
  "which proxy does this browser obey" is actually decided.

Live: `ScOsProxyInheritanceProbeTests` (probe category; artifacts under
`%TEMP%\webcontent-os-proxy-probe\<run>\`). Both rows use the same workspace home,
`http://wininet-proxy.invalid/` - an RFC 2606 `.invalid` host that can NEVER resolve, so any
bytes at that url can only have come through a proxy. The Management Client phase sets the
Demo Workspace home + routing toggle in one session, verifies the plugin's
`Stored proxy targets ... routing on|off` audit line plus a reloaded-pane readback, and the
probe's finally restores whatever pre-call state it read (baseline fallback
`http://httpforever.com/` + routing on).

- **Row 1 (inheritance)** `ProbeNonProxiedViewRoutesThroughOsConfiguredProxy` - routing OFF,
  WinINET proxy pointed at the harness: the workspace's first document renders the harness
  marker page (title + marker + absolute url echo asserted over CDP), the harness journal
  carries the browser's absolute-form `GET http://wininet-proxy.invalid/` with its Chromium
  UA, and every plugin browser process command line shows NO `--proxy-server` and no
  `--no-proxy-server` (the only injected argument in the session is the harness's own
  `--remote-debugging-port=0` via `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`).
- **Row 2 (contrast)** `ProbeProxiedViewNeverUsesOsConfiguredProxy` - routing ON, SAME OS
  proxy, SAME url: the workspace's first document is the plugin's own
  `Web Content proxy: 403` page ("not in the workspace proxy allowlist" + the canonical
  entry line), the harness journal for that origin is EMPTY (the OS proxy was never asked),
  and the live browser command lines carry `--proxy-server=http=http://127.0.0.1:<listener>;https=...;ws=...;wss=...`
  plus `--proxy-bypass-list=<-loopback>`.

**Live records (BEAST, vms.joshooaj.com):**

- Row 1 (inheritance): `20261103-035256` — **PASSED** 2026-11-03. Home
  `http://wininet-proxy.invalid/` (routing OFF, WinINET proxy = harness 127.0.0.1:55920): the
  tab's document title was `WebContent OS-proxy harness`, the body carried the marker + the
  absolute url echo, the journal recorded `GET http://wininet-proxy.invalid/` with the
  `Edg/156.0.0.0` UA, and the browser command lines showed the `msedgewebview2.exe` tree with
  no proxy switch. Restore verified in the artifact (WinINET back to the exact snapshot).
- Row 2 (contrast): `20261103-040006` — **PASSED** 2026-11-03. SAME home, routing ON: the
  document title was `Web Content proxy: 403` with the "not allowed by the administrator" body,
  the harness journal for `wininet-proxy.invalid` was EMPTY (0 of 73 journaled requests — the
  others were the machine's own apps discovering the temporarily-set OS proxy), and the live
  command lines carried `--proxy-server=http=http://127.0.0.1:63391;https=...;ws=...;wss=...`
  with `--proxy-bypass-list=<-loopback>` on the browser tree root. WinINET + workspace
  (home `http://httpforever.com/`, routing ON) both restored verbatim, audit line + reloaded
  pane readback confirmed.
- Harness ordering fact discovered while building this: the Management Client phase must run
  BEFORE the WinINET proxy is applied — MC's own login channel follows the system proxy, and
  pointing WinINET at the harness (which 403s CONNECT and never dials upstream) breaks the MC
  login. The probe now orders MC first.

## Installer checks

### IN-1 Installer force-closes running Milestone clients (#70)

The MSI must never fail (1603) because `Client.exe` / `VideoOS.Administration.exe` hold the
plugin dlls. WiX v7 removed the restart-manager `CloseApplications` element, so `setup.wxs`
authores `util:CloseApplication` rows that **force-kill** the three plugin-hosting processes
(`Client.exe`, `VideoOS.Administration.exe`, and the observed alternate host
`VideoOS.OAuth2TokenGenerator.exe`) — neither Milestone app is RestartManager-cooperative, so
unsaved client state is lost. In the full UI (`MUILevel=5`) each app gets an Abort/Retry/Ignore
warning first (default Ignore); silent runs (`/qn`) take the no-prompt twin row. The kill is
scheduled right after InstallInitialize (1501), well before RemoveFiles (3500)/InstallFiles
(4000), and `MajorUpgrade Schedule="afterInstallExecute"` keeps RemoveExistingProducts late
(6501) — safe because old and new packages share identical component GUIDs, so MSI reference
counting preserves shared files.

Automated (build-time): `build.ps1` fails the installer build unless the compiled MSI's
`InstallExecuteSequence` keeps that ordering and the `Wix4CloseApplication` table carries the
three targets (see the matrix row above).

Manual live rows:

1. Uninstall with clients running: `msiexec /x {product-code} /qn` → exit 0, clients gone, drop
   folder and uninstall registry entry gone.
2. First install with clients running: `msiexec /i <msi> /qn` → exit 0, clients killed, drop
   hash-identical to `output\WebContent\`, product registered.
3. **The #70 scenario** — upgrade with clients running (old version installed, `WebContent.dll`
   proven LOCKED by an exclusive-open probe first): `msiexec /i <new msi> /qn` → exit 0 (not
   1603), clients killed, version bumped, drop hash-identical. The log shows the deferred
   `WixCloseApplicationsDeferred` custom action with `MSI_LUA ... sufficient privileges` and
   `MainEngineThread is returning 0`.

The full-UI Abort/Retry/Ignore dialog is structurally verified only (the prompt rows and
`Description` strings are in the compiled tables); nobody clicked through it live.

**All three live rows PASSED 2026-11-03** on the VMS machine (`vms.joshooaj.com`), clients
launched into the real interactive session 3 via scheduled tasks, `MilestoneEventServerService`
stopped around the MSI operations (it also holds the drop dlls; see #68 for its own upgrade
story) and restarted afterwards. Uninstall of `0.1.259.40320` (`uninstall.log`), fresh install
of `0.1.259.40320` (`install-fresh.log`), and the upgrade `0.1.259.40320 → 0.1.999.1`
(`upgrade.log`) each exited 0 with the clients killed; the final production reinstall verified
`WebContent.dll` hash-identical (`52B303DA…868C1E`) to the local build drop. Test artifacts and
scheduled tasks removed afterwards.

## Known high-risk regressions → pinned by

| Regression (remediation it would undo) | Pinned by |
|---|---|
| Duplicate message/receiver handling after reopen (#10) | `CallbackOwnershipTests`, MC-1 |
| Runtime exception on malformed/legacy persisted values (#11) | `ConfigResilienceTests`, `WorkspacePropertiesTests` |
| Tabs permanently suppressed by a startup timing failure (#12) | MC-4 (recovery markers), `WorkspaceDefinitionTests.SelectVisible_*` |
| License code resurrected by merge/revert (#13) | `PluginLicenseHygieneTests` (red-proven guard) |
| Inconsistent address normalization across admin/runtime flows (#14) | `AddressNormalizationTests`, MC-3 |
| Unbounded browser tracking / stale async callbacks in long sessions (#15) | `BrowserRegistryTests` bounded-cycle test, `CallbackOwnershipTests`, MC-6 |
| Workspace tab losing the operator's page on workspace switch, or ignoring an admin definition edit (#9) | `WorkspaceSessionStateTests` (fingerprint guard, restore precedence, update detection), MC-7 |
| Broken workspace config silently hiding its tab again (#9) | `WorkspaceDefinitionTests` (`ConfigurationError` + `SelectVisible` visibility pins), MC-8 |
| Proxy allowlist lost on toggle-off, or proxy silently defaulting on for old definitions (#52) | `ProxyConfigurationTests` (legacy blob = proxy off; roundtrip), MC-9 |
| Inconsistent proxy-target canonicalization between admin and runtime flows (#52) | `ProxyPolicyTests` + `WorkspacePropertiesTests` (shared `ProxyPolicy` canonical form in both save and Build paths) |
| Fetch message ids renamed to something ending in "Command", silently breaking SecurityLevel.Normal clients (#53) | `ProxyFetchContractGuardTests` (id-suffix pins), ES-1 |
| Chunk framer or correlation reassembler losing frames/order so the client sees truncated bodies (#53) | `ProxyFetchFramerTests`, `ProxyFetchCorrelationTests`, `ProxyFetchTransportTests` |
| Proxied workspace silently reaching targets direct, or driving the ES fetcher at arbitrary origins (#54) | `ProxyForwardPolicyTests` (403/405/502 table), `ProxyBrowserArgumentsTests` (no scheme falls back to direct), `ProxyWorkspaceProfileTests` (enabled always proxies), `ProxyServerAllowlistTests` (ES-side union gate, closed before first resolve), SC-1 |
| Reassembled body framed with the target's stale Content-Length or hop-by-hop headers, corrupting the rendered page (#54) | `ProxyForwardCodecTests` (Content-Length restated, hop-by-hop dropped) |
| Proxy-fetch payloads moved back from JSON strings to plugin-namespace types, silently null-ing `Message.Data` on hosts whose type resolution cannot see WebContent.dll (the Smart Client black hole) (#54) | `ProxyFetchWireCodecTests` (round-trip + foreign-content pins), `ProxyFetchReplyGuardTests.BothSidesCarryPayloadsAsJsonStringsNeverAsPluginTypes` (both transmit sides), ES-1 bullet 3, SC-1 |
| Repeated `Set-Cookie` values comma-joined or login 3xx followed server-side, so the session cookie never reaches the browser and vendor logins loop back to the form (#55) | `ProxyFetchTransportTests` (repeated Set-Cookie captured per value; 302+Set-Cookie relayed, never followed), `ProxyForwardConnectionTests` (separate Set-Cookie lines rendered; POST body+Cookie to the seam), SC-2 |
| Request method tables drifting between the client forwarder and the Event Server, so a browser POST forwards locally then dies as a server-side refusal (#55) | One shared `ProxyForwardPolicy.IsForwardedMethod` table used on both sides; `ProxyForwardPolicyTests` (verb table), `ProxyFetchTransportTests` (verb replay) |
| Cookie/Authorization headers silently stripped at the proxy, breaking exactly the login flows #55 exists for | `ProxyForwardCodecTests.ToOutboundRequestHeaders` pins (Cookie/Authorization/custom kept verbatim; only hop-by-hop+Host+Content-Length dropped), `ProxyFetchTransportTests` replay pin |
| Global (rather than per-target) TLS bypass on the Event Server, so one admin flag makes every proxied https device unvalidated (#56) | `ProxyFetchTlsDecisionTests` (override consulted per allowlist entry; default fail-closed `TlsValidation`), `ProxyServerAllowlistTests` (flag round-trips with its entry only) |
| Leaf private keys crossing the wire or a session CA written into the user root store (crypt32's modal consent dialog hangs unattended sessions; ADR-0002's superseded shape) (#56, ADR-0003) | `ProxyLeafContractTests` (request carries only the SPKI; response only public DERs), `ProxyFetchReplyGuardTests` (leaf replies unicasted), `ProxyLoopbackTls`/`ProxyLoopbackTrust` (no store-write path exists on the client), SC-3 |
| Minting gate decoupled from the fetch allowlist, so a leaf could be signed for an origin the fetcher would refuse (forged-tunnel potential) (#56) | `ProxyLeafContractTests` (same union allowlist gates minting; `NotAllowed` fail-closed before first resolve), `ProxyFetchReplyGuardTests` (LeafRequestId filter + unicast) |
| Websocket bytes smuggled through the HTTP fetch/chunk framing (double base64, lost framing, or a wss upgrade silently fetched instead of refused) (#57, #60) | `ProxyForwardPolicyTests` (upgrade never a forward; the 501 refusal posture is deleted in #60 — wss upgrades tunnel, never fetch), `ProxyTunnelContractTests` (bytes ride their own directional ids, base64 at the JSON edge only), `ProxyForwardConnectionTests` (verbatim 101; pumps), SC-4 |
| Tunnel sockets accumulating on the Event Server (half-open opens, silent peers, client teardown without notice) (#57) | `ProxyTunnelCorrelationTests` (open deadline, idle sweep returns ids for close notification, orphan-ack closes its handle, CloseAll on teardown), `ProxyTunnelServerTests` (idle reclaim closes + notifies, cap, CloseAll), `ProxyTunnelServer` reaper (sweep never escapes to the ES process) |
| Upgrade request-target schemes drifting out of the codec (absolute `ws://` refused, or the CONNECT-carried ws form live Chromium actually sends treated as an unlisted https tunnel), so no live browser ever tunnels (#57) | `ProxyForwardPolicyTests` (absolute `ws://` tunnels, CONNECT http-twin posture accepted, https-entry precedence; `wss://` lifted from 501 to tunnelled by #60), `ProxyForwardCodec` ws/wss rebase pin, SC-4 |
| A blind raw-byte CONNECT tunnel opened by an http-allowlisted entry (anything riding the tunnel, not just websockets) (#57) | `ProxyForwardConnectionTests` (in-tunnel head validated as a real ws handshake before any ES socket opens; non-ws first bytes close the tunnel; failed open closes quietly inside the tunnel) |
| A stopped/restarting Event Server surfacing as a silent 30-second hang or a generic 502 indistinguishable from a target failure, so the admin cannot tell "proxy component absent" from "target down" (#58) | `ProxyFetchCorrelationTests` (deadline silence = `ServerUnavailable`; `Fail` carries the channel reason), `ProxyForwardConnectionTests` (503 component page, 403 vs 502 vs 504 posture table, size-cap page), SC-5 |
| The tunnel TLS leg trusting any chain (every wss device invisibly unvalidated), or the wss posture drifting onto an http twin entry so a strict https origin never admits the upgrade (#60) | `ProxyTunnelServerTests` (production resolver is null = SslStream full chain validation; untrusted chain → explicit `TlsValidation` naming the origin and the Event Server side; the accept-all resolver is a test-only seam), `ProxyForwardPolicyTests` (https entry required for the upgrade — http twin alone 403; no `WebSocketTunnelRefused` posture exists), SC-6 |
| The Status tab hammering the Event Server after its tab closes (polling follows the pane, not the selection), or bitrate columns inventing spikes out of first sightings / backwards counters (#76) | `ProxyStatusViewTests` (rate needs two sightings, non-later snapshot / backwards counters / unknown ids → zero, `Reset` drops the baseline), `WorkspaceTabShellGuardTests` (Status tab has no config input — it can never dirty the pane), MC-12 (marker count flat across tab-away, resumes on reopen) |
| Client-side proxy refusals vanishing with the debug log, so an integrator fixing "camera won't load" gets no per-view record of which origin the allowlist refused (#83) | `ProxyBlockTallyTests` (per-origin+reason counts, navigation reset, bounded eviction, dead-forwarder isolation, one ES payload + one local row per refusal), SC-7 |
| A wildcard allowlist entry reaching the apex, a literal IP, `localhost`, or the VMS's own hosts - SSRF widening of every gate AND the mint policy at once (#73) | `ProxyPolicyTests` (seeded apex/subdomain/floor invariants over random hosts and IPv4s), `ProxyAllowlistGateAgreementTests` (ES fetch, client forward, and leaf mint reach identical verdicts on one matrix), MC-13 |
| A tri-state string on a WPF-boolean DataGrid attribute taking the whole Management Client config page down at BAML parse time (#76, found in the MC-13 walk) | `WorkspaceTabShellGuardTests` (every `CanUser*` on every page DataGrid pinned to a real bool), MC-13 |
| A CA export writing an EMPTY file when no authority exists, guessing the CA's provenance (a script-provisioned certificate invisible to the export), reaching any cert store WRITE flag, or outgrowing the MIP frame limits mid-reply (#75) | `ProxyCaExportTests` (`NoAuthority` "does not exist yet" posture; fixed-CN read-only `LocalMachine\Personal` resolve with source guards on the open flags; view builder never offers a save without bytes; 8 KiB DER / 16 KiB wire caps pinned under the proven chunk headroom with a real 2048-bit CA under the cap), MC-15 (live bytes byte-identical to the legacy-script-provisioned store entry) |
| MSI upgrade dying with 1603 because a running Smart Client / Management Client locks the plugin dlls, or the force-close drifting after RemoveFiles/InstallFiles so the files are still locked when the MSI reaches them (#70) | `build.ps1` installer sequencing guard (compiled-MSI action order + kill-target table pins), IN-1 (live uninstall/fresh-install/upgrade rows with the dll proven LOCKED, all exit 0) |
| A non-proxied view silently losing OS (WinINET) proxy inheritance (a stray browser-arguments injection), or a proxied view falling back to the system proxy so the workspace allowlist stops being the whole story (#67) | `ProxyBrowserArgumentsTests` (the pin shape: all four schemes + `<-loopback>`), `ProxyWorkspaceProfileTests` (enabled always proxies; disabled emits no profile), SC-8 (both postures observed live on the real browser command lines and the OS-proxy harness journal) |

## Scope notes (deliberate)

- The retry state machine in `WebContentPluginDefinition` (backoff cap, empty-load retries)
  is host-coupled (`EnvironmentManager`) and is verified by MC-4, not unit-pinned. If it is
  ever refactored, extract the policy into a pure class first and move MC-4's backoff
  assertions into the suite.
- WebView2/Smart Client UI behavior (actual page render, WPF address-bar visuals) cannot be
  unit-tested at the pure seams; MC-1-MC-19 (plus ES-1 and SC-1) are the release gate for those.
