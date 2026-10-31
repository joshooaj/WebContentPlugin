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
| Proxy fetch messaging (Event Server fetcher) | #53 | `ProxyFetchContractGuardTests` (message ids never end in "Command" so SecurityLevel.Normal clients can send; payload types are plugin-namespace `[Serializable]` with public fields — the whitelist case that keeps `MessageCommunicationSettings.config` unnecessary; frame-kind/error-code constants stable), `ProxyFetchFramerTests` (meta frame Sequence=-1 then base64 chunk frames 0..N-1 with IsLastChunk; failure is a lone frame), `ProxyFetchCorrelationTests` (meta+chunks→complete body; out-of-order buffered; failure frame surfaces error code; foreign correlation ids ignored; timeout), `ProxyFetchTransportTests` (real HttpListener: status+headers captured, size cap aborts, connect/read budgets, refused connection maps to Unreachable), `EventServerLoadGuardTests` (plugin.def load env includes `Service`; the definition exposes `BackgroundPlugins` with a Service-targeted plugin and declares both fetch message ids in `PluginDefinedMessageIds`) | See ES-1 and MC-10 below: Event Server loads the fetcher, and the Management Client harness fetches a page through it with failures logged |
| Loopback forwarding proxy (Smart Client view consumes the fetch seam) | #54 | `ProxyForwardCodecTests` (absolute-form proxied request parsing; origin-form/non-http refused; CONNECT recognized by authority target; forwarded-response rendering: status line, content headers relayed, hop-by-hop dropped, Content-Length restated from the reassembled body; refusal pages), `ProxyForwardPolicyTests` (decision table via `ProxyPolicy`: allowlisted GET forwards, other origins 403, non-GET 405, CONNECT 502, malformed 400 — address-bar navigation follows the same rule), `ProxyForwardConnectionTests` (full stream flow: forward+relay, 403 without touching the fetcher, Unreachable→502, Timeout→504), `ProxyBrowserArgumentsTests` (every http/https/ws scheme pinned to the listener, `<-loopback` bypass off — never direct), `ProxyWorkspaceProfileTests` (enabled+targets → listener allowlist; duplicates collapse; enabled-with-no-targets still proxies and allows nothing; disabled → no profile), `ProxyServerAllowlistTests` (ES refuses origins no proxied workspace allowlists, proxy-off workspaces don't count, closed before first resolve, stale-on-error keeps serving the last snapshot) | See SC-1 below: with the drop deployed on both the Event Server machine and the Smart Client machine, and the client's route to the target killed, the proxied page still loads — proof the traffic source is the server, not the client |

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
3. No `MessageCommunicationSettings.config` whitelist edit is needed: every payload type
   (`ProxyFetchRequestPayload`, `ProxyFetchResponsePayload`, `ProxyFetchHeader`) lives in the
   plugin's own namespace, which the messaging layer trusts by default. The escape hatch only
   becomes necessary if a payload ever references a foreign assembly type — then that type would
   have to be added to the config file next to the host executable.
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
   listener. Then block the client machine's route to the target (`New-NetFirewallRule` block,
   or pull the client off that segment). Reload: the page MUST still render, while a direct
   `Invoke-WebRequest http://10.0.0.5:8080/` from the client machine now fails. That asymmetry
   is the proof the traffic originates on the Event Server machine.
4. Address-bar navigation to a NON-allowlisted origin in the proxied workspace shows the proxy's
   403 page ("not in the workspace proxy allowlist"), and no request leaves for it (the fetcher
   is never asked). Navigation to an https:// origin shows the 502 CONNECT-refusal page — https
   is never taken direct. A POST/HTML form submit shows the 405 page.
5. Event Server `Logs\MIP.log` during 3: successes are silent; failure cases (4) log the
   refusal/`Proxy fetch failed` markers from ES-1/MC-10.
6. Remove the view item from the layout, or exit the Smart Client: the listener stops with the
   browser (`Proxy listener` log entries only on faults); `netstat -an | findstr <port>` shows
   nothing listening afterwards. (A mere close of the view tab keeps a background-retained
   browser — and its listener — alive for re-attach; that is existing browser lifecycle behavior.)

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
| Fetch message ids renamed to something ending in "Command", or payloads moved to a non-whitelisted type, silently breaking SecurityLevel.Normal clients or deserialization (#53) | `ProxyFetchContractGuardTests` (id-suffix + serializability/whitelist pins), ES-1 |
| Chunk framer or correlation reassembler losing frames/order so the client sees truncated bodies (#53) | `ProxyFetchFramerTests`, `ProxyFetchCorrelationTests`, `ProxyFetchTransportTests` |
| Proxied workspace silently reaching targets direct, or driving the ES fetcher at arbitrary origins (#54) | `ProxyForwardPolicyTests` (403/405/502 table), `ProxyBrowserArgumentsTests` (no scheme falls back to direct), `ProxyWorkspaceProfileTests` (enabled always proxies), `ProxyServerAllowlistTests` (ES-side union gate, closed before first resolve), SC-1 |
| Reassembled body framed with the target's stale Content-Length or hop-by-hop headers, corrupting the rendered page (#54) | `ProxyForwardCodecTests` (Content-Length restated, hop-by-hop dropped) |

## Scope notes (deliberate)

- The retry state machine in `WebContentPluginDefinition` (backoff cap, empty-load retries)
  is host-coupled (`EnvironmentManager`) and is verified by MC-4, not unit-pinned. If it is
  ever refactored, extract the policy into a pure class first and move MC-4's backoff
  assertions into the suite.
- WebView2/Smart Client UI behavior (actual page render, WPF address-bar visuals) cannot be
  unit-tested at the pure seams; MC-1–MC-10 (plus ES-1 and SC-1) are the release gate for those.
