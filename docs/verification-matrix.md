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

## Scope notes (deliberate)

- The retry state machine in `WebContentPluginDefinition` (backoff cap, empty-load retries)
  is host-coupled (`EnvironmentManager`) and is verified by MC-4, not unit-pinned. If it is
  ever refactored, extract the policy into a pure class first and move MC-4's backoff
  assertions into the suite.
- WebView2/Smart Client UI behavior (actual page render, WPF address-bar visuals) cannot be
  unit-tested at the pure seams; MC-1–MC-9 are the release gate for those.
