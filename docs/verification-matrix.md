# Verification matrix — regression safety net (ticket #16)

Release-gating verification contract for the WebContent Smart Client plugin. Every area is
either pinned by the automated suite (preferred) or by an explicit live-host check with a
named log marker. An area is **verified** only when all of its rows pass.

## How to run

| Gate | Command | Pass criterion |
|---|---|---|
| Regression suite (all automated rows) | `dotnet test src/WebContent.Tests` | exit 0, zero failed |
| One-command gate script | `.\verify.ps1` (add `-IncludePluginBuild` on a VS2022 machine) | exit 0, prints `Release gate: PASS` |
| CI | `Tests` job in `.github/workflows/ci.yml` (runs on PRs and pushes) | job green before merge to `main`; the installer only publishes from `main` |
| Plugin build | `msbuild src/WebContent/WebContent.csproj /t:Build /p:Configuration=Debug /p:Platform=x64` | exit 0, no sbom-tool warnings (PostBuildEvent passes `-D true`, so stale `_manifest` dirs self-overwrite) |
| Manual host checks | Steps below, run against a deployed plugin per `AGENTS.md` | each row's expected outcome and log marker observed |

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
| Proxy fetch messaging (Event Server fetcher) | #53 | `ProxyFetchContractGuardTests` (message ids never end in "Command" so SecurityLevel.Normal clients can send; payload types stay plugin-namespace `[Serializable]` with public fields — wire shape itself is pinned by `ProxyFetchWireCodecTests` since #54; frame-kind/error-code constants stable), `ProxyFetchFramerTests` (meta frame Sequence=-1 then base64 chunk frames 0..N-1 with IsLastChunk; failure is a lone frame), `ProxyFetchCorrelationTests` (meta+chunks→complete body; out-of-order buffered; failure frame surfaces error code; foreign correlation ids ignored; timeout), `ProxyFetchTransportTests` (real HttpListener: status+headers captured, size cap aborts, connect/read budgets, refused connection maps to Unreachable; #55: method/form body/Cookie+custom headers replayed at the target, 302+Set-Cookie relayed instead of followed, repeated Set-Cookie values captured separately), `EventServerLoadGuardTests` (plugin.def load env includes `Service`; the definition exposes `BackgroundPlugins` with a Service-targeted plugin and declares both fetch message ids in `PluginDefinedMessageIds`) | See ES-1 and MC-10 below: Event Server loads the fetcher, and the Management Client harness fetches a page through it with failures logged |
| Loopback forwarding proxy (Smart Client view consumes the fetch seam) | #54, write verbs #55 | `ProxyForwardCodecTests` (absolute-form proxied request parsing; origin-form/non-http refused; CONNECT recognized by authority target; outbound request-header filter keeps Cookie/Authorization/custom headers and drops hop-by-hop+Host+Content-Length; forwarded-response rendering: status line, content headers relayed, hop-by-hop dropped, Content-Length restated from the reassembled body; refusal pages), `ProxyForwardPolicyTests` (decision table via `ProxyPolicy`: allowlisted GET/HEAD/POST/PUT/DELETE/OPTIONS/PATCH forward, other origins 403, TRACE/junk verbs 405, CONNECT 502, malformed 400 — address-bar navigation follows the same rule), `ProxyForwardConnectionTests` (full stream flow: forward+relay, POST method/form body/Cookie to the fetch seam, 302+Set-Cookie relayed back, repeated Set-Cookie rendered as separate lines, 403 without touching the fetcher, oversized declared body 413, chunked body 411, Unreachable→502, Timeout→504), `ProxyBrowserArgumentsTests` (every http/https/ws scheme pinned to the listener, `<-loopback` bypass off — never direct), `ProxyWorkspaceProfileTests` (enabled+targets → listener allowlist; duplicates collapse; enabled-with-no-targets still proxies and allows nothing; disabled → no profile), `ProxyServerAllowlistTests` (ES refuses origins no proxied workspace allowlists, proxy-off workspaces don't count, closed before first resolve, stale-on-error keeps serving the last snapshot) | See SC-1 below: with the drop deployed on both the Event Server machine and the Smart Client machine, and the client's route to the target killed, the proxied page still loads — proof the traffic source is the server, not the client. See SC-2: a login POST round-trips with its session cookie in a live Smart Client session |
| HTTPS targets: target TLS verdict, per-target override, loopback leaf mint (ADR-0002/0003) | #56 | `ProxyFetchTlsDecisionTests` (https targets get full chain validation on the Event Server; an untrusted chain refuses with `TlsValidation` unless the allowlist entry carries the per-target accept-untrusted override — never a global bypass), `ProxyPolicyTests` (target-line grammar: `https://host:port accept-untrusted` in either token order; unknown tokens or a second origin reject the whole entry), `ProxyServerAllowlistTests` (the `acceptUntrusted` flag round-trips with its entry through the workspace JSON), `ProxyLeafContractTests` (LeafRequest carries ONLY the client-generated SPKI; the allowlist union is the minting gate — `NotAllowed` fail-closed like the fetcher, `BadRequest` for malformed origin/key, `NoAuthority` when the deployment CA cannot load/create; minted leaf chains to the deployment CA with host SAN + serverAuth EKU; single-frame correlation with deadline), `ProxyLoopbackTlsTests` (leaves cached per host:port, one mint exchange per origin; refused mint fails the tunnel with the server's code; seeded `AcceptTunnelAsync` + real Schannel server handshake on a channel-minted leaf), `ProxyLoopbackClientKeyTests` (exported SPKI is public-only; `AttachLeaf` yields a private key Schannel accepts; failed attach keeps the key usable), `ProxyFetchReplyGuardTests` (leaf answers unicasted to the requester endpoint, never broadcast), `EventServerLoadGuardTests` (leaf message ids declared) | See SC-3 below: both-machine drop plus the one-time elevated CA import into `LocalMachine\Root`; a self-signed https target fails closed WITHOUT the override (proxy error page names the exact per-target switch) and the full login flow completes WITH it |
| WebSocket CONNECT tunnels through the proxy (raw bytes both directions, outside the fetch chunking path) | #57 | `ProxyForwardPolicyTests` (absolute ws:// upgrade to an allowlisted http origin tunnels; CONNECT to an http-twin-allowlisted origin accepted as the ws-tunnel posture live Chromium actually uses — https entries keep precedence for the #56 TLS posture; wss:// and in-tunnel upgrades refused 501, never fetched; missing Sec-WebSocket-Key / wrong version 400; h2c upgrades stay ordinary forwards), `ProxyWebSocketHandshakeTests` (upgrade recognized only for GET+Connection upgrade token+Upgrade websocket; rebuilt origin-form handshake: uri-derived Host, browser's Sec-WebSocket-Key byte-identical, explicit Connection/Upgrade pair, header-injection gate; 101-only admission; head terminator), `ProxyTunnelContractTests` (six directional ids; one JSON-string payload shape for the whole protocol; Data/Close kinds; limits), `ProxyForwardConnectionTests` (upgrade head pipelined bytes ride into the tunnel first; the target's verbatim 101 written to the browser before the first pump byte; both pumps; either side ending ends both; in-tunnel wss refused 501 through the tunnel stream), `ProxyTunnelServerTests` (in-process ws target: 101 admits and both pumps echo; non-101 refused HandshakeRefused; dead target Unreachable; ES cap rejects; duplicate id rejected; client bytes reach the target in order through the serialized writer; idle reclaim closes and notifies; CloseAll), `ProxyTunnelCorrelationTests` (client registry: ordered pump queue, open deadline completes as failure, half-open + idle reclaim return ids for close notification, orphan open-ack closes its handle, teardown closes all), `EventServerLoadGuardTests` (tunnel ids declared in PluginDefinedMessageIds) | See SC-4 below: the ws-echo page in a proxied Smart Client browser shows its token echoed (client-side proof) while the ws server journals WS-HANDSHAKE + WS-ECHO (server-side proof the upgrade arrived as a tunnel, not a fetch) |

## Manual host checks

Run after deploying new build output per `AGENTS.md` (kill `client`/`VideoOS.Administration`
hosts first; copy to the MIPPlugins root; verify by file hash, not timestamp).

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

1. In Management Client, enable "Route web traffic through the VMS server proxy" on a workspace and add one target line (e.g. `https://10.0.0.5:8443`); Save.
2. In Smart Client, open the workspace tab's properties page. Expected: the Proxy section reports the enabled state and lists the allowlisted target (canonical form).
3. Toggle the proxy off, Save, and check the target list is still present in the stored definition (off-toggle preserves allowlisted targets; MC-9 pin for the wipe bug found in review).
4. With the proxy enabled, enter an invalid target line (`ftp://x`, `not a url`) and Save: refused with a message naming the line; MC log marker: `Rejected save for workspace '<name>' (FQID: ...): invalid proxy target`. Enabled with no targets at all is refused likewise (`proxy enabled but no target allowlisted`).

### ES-1 Event Server fetcher load and answer (#53)

1. Deploy the new drop per `AGENTS.md`, then restart the XProtect Event Server service (it only
   loads MIP plugins at service start; plugin.def gained the `Service` load environment).
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
| Websocket bytes smuggled through the HTTP fetch/chunk framing (double base64, lost framing, or a wss upgrade silently fetched instead of refused) (#57) | `ProxyForwardPolicyTests` (upgrade never a forward; wss/in-tunnel 501), `ProxyTunnelContractTests` (bytes ride their own directional ids, base64 at the JSON edge only), `ProxyForwardConnectionTests` (verbatim 101; pumps), SC-4 |
| Tunnel sockets accumulating on the Event Server (half-open opens, silent peers, client teardown without notice) (#57) | `ProxyTunnelCorrelationTests` (open deadline, idle sweep returns ids for close notification, orphan-ack closes its handle, CloseAll on teardown), `ProxyTunnelServerTests` (idle reclaim closes + notifies, cap, CloseAll), `ProxyTunnelServer` reaper (sweep never escapes to the ES process) |
| Upgrade request-target schemes drifting out of the codec (absolute `ws://` refused, or the CONNECT-carried ws form live Chromium actually sends treated as an unlisted https tunnel), so no live browser ever tunnels (#57) | `ProxyForwardPolicyTests` (absolute `ws://` tunnels, CONNECT http-twin posture accepted, https-entry precedence, `wss://` 501), `ProxyForwardCodec` ws/wss rebase pin, SC-4 |
| A blind raw-byte CONNECT tunnel opened by an http-allowlisted entry (anything riding the tunnel, not just websockets) (#57) | `ProxyForwardConnectionTests` (in-tunnel head validated as a real ws handshake before any ES socket opens; non-ws first bytes close the tunnel; failed open closes quietly inside the tunnel) |

## Scope notes (deliberate)

- The retry state machine in `WebContentPluginDefinition` (backoff cap, empty-load retries)
  is host-coupled (`EnvironmentManager`) and is verified by MC-4, not unit-pinned. If it is
  ever refactored, extract the policy into a pure class first and move MC-4's backoff
  assertions into the suite.
- WebView2/Smart Client UI behavior (actual page render, WPF address-bar visuals) cannot be
  unit-tested at the pure seams; MC-1–MC-10 (plus ES-1 and SC-1) are the release gate for those.
