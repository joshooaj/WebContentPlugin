# Web Content Plugin for XProtect Smart Client

Add rich web content to the Smart Client with a browser component that is
always up to date and widely compatible with the modern web, popular IoT
platforms, and more.

The Web Content plugin uses the Microsoft Edge WebView2 runtime which means it
will always be kept up to date without the need to upgrade the plugin. And
because it is a Microsoft Edge browser, it will work with whatever content you
need it for.

![Screenshot of web content plugin in Smart Client](./docs/assets/images/screenshot1.png)

## Features

- **Evergreen** browser based on Microsoft Edge WebView2 Runtime. Browser stays up to date even if the plugin does not.
- **Runs in Background** when enabled. Your browser session isn't lost if you switch views and come back.
- **Isolate browser sessions** for improved security. Each browser instance can use its own "profile" reducing the risk
  of exposing data between instances.
- **Remember the last address** instead of always navigating to the configured home address on startup.
- **Automatically clear history** when the client is closed if you don't want to leave sensitive browser history on disk.
- **Hide the toolbar** or even just the address field to minimize misuse of the browser or simply regain some of that precious screen real estate.
- **Easily mute unexpected audio** from a browser in a previous view that is no longer displayed. If a background browser
    should start playing sound, you'll see a notification with the name of the web page and a button to mute that browser instance.

## Browser data per XProtect user

The Web Content plugin stores its WebView2 browser profile in
`%APPDATA%\SmartClient.WebContentData`, **keyed to the XProtect login identity** you used to sign
into Smart Client, not merely to the Windows account. Different XProtect users therefore start with
**separate, fresh** browser profiles. They do not inherit each other's browsing history, cookies or
saved credentials on the same machine.

**Important: the Windows account is the security boundary.** WebView2/Chromium encrypts protected
browsing data (cookies and saved passwords) with Windows DPAPI, which is tied to the Windows logon.
Two different XProtect accounts on the *same* Windows account are separated but not
*cryptographically* isolated from each other. To keep two XProtect identities fully private, log
each into Smart Client under a **distinct Windows account**.

### Deliberately sharing a browser profile

If multiple operators intentionally need to share a single WebContent profile (for example a shared
control-room operator), they must log into Smart Client as the **same XProtect login identity** on
the **same Windows machine account**. Using different XProtect identities will give each a separate
profile even on a shared Windows account.

See `docs/adr/0001-browser-profile-per-xprotect-user.md` for the full decision and the security
analysis.

## Getting started

### Install the plugin

Download the installer from the [latest release](https://github.com/joshooaj/WebContentPlugin/releases/latest). Run the
file named `CascadiaWebContentPlugin.<version>.msi`.

The installer **closes running Milestone applications** (Smart Client, Management Client) so
upgrades never fail on locked plugin files. These apps do not cooperate with Windows' restart
manager, so they are closed without a chance to save — close Smart Client yourself if you have
unsaved view or layout changes. In the full UI the installer shows a warning first and lets you
abort, retry, or proceed.

The installer is not currently signed, so your browser will warn you that the file is potentially unsafe. The release
also carries a `CascadiaWebContentPlugin.<version>.msi.sha256.txt` file that you can use to validate the installer hash.

Click **Keep**, and if your browser asks again, click **Keep anyway**. The installation experience will improve when I
get a code signing certificate setup.

The installer has one page beyond the license: when the machine runs an **Event Server** (`MilestoneEventServerService`)
and the plugin is already installed there, the wizard shows an **Event Server options** page with a
**Restart the Event Server now?** checkbox (checked by default). Upgrading the plugin on that machine stops the service
first (waiting for the real stop), copies the files, and the checkbox decides whether the installer starts it again at
the end — clearing it leaves the service stopped so you can schedule the restart yourself. The restart matters: the new
plugin files only load when the Event Server (re)starts, and **restarting it disconnects every connected Smart Client's
plugin channel — restart those Smart Clients afterwards**. On machines without an Event Server, or when the plugin is
being installed on that Event Server for the first time, that page never appears and the service is never stopped.
For unattended installs the choice is a property: `msiexec /i <msi> /qn RESTART_EVENT_SERVER=0` declines the restart.
One deliberate caveat: **uninstalling** the plugin while the Event Server is running does not stop the service either —
stop `MilestoneEventServerService` first or the service keeps the plugin files locked (see the Event Server deployment
notes below).

Beyond that the installer is simple: acknowledge the license, and it will install the Microsoft Edge
WebView2 Runtime if necessary, and place the plugin files in `C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin\`.

### HTTPS targets: deployment certificate authority (one-time setup)

A workspace proxy allowlist may include `https://` targets (tick **Accept untrusted** on the row
to tolerate a device's self-signed certificate — the flag is per target, never global).
To make those pages render without browser certificate errors, the plugin uses one long-lived certificate
authority per deployment (see `docs/adr/0003-event-server-mints-loopback-leaf-certs.md`):

1. **Create the CA once on the Event Server machine — the installer does it.** The Event
   Server service runs as a restricted built-in account (`NT AUTHORITY\NETWORK SERVICE` on
   current Milestone installs) that cannot create the CA itself, so the MSI does it elevated
   at install time: on the **Event Server options** page (the page that appears only when the
   machine runs the Event Server service), tick **Enable web content proxy support**. The
   installer then creates the CA certificate named
   `XProtect WebContent Plugin Loopback Proxy` in the Local Machine Personal store, grants
   the Event Server service account read access to its private key so leaf minting works at
   runtime, and leaves any existing CA untouched (upgrade re-runs reuse it, thumbprint and
   key permissions included). The private key never leaves that machine. Scripted installs
   are opted in by default; pass `PROXY_SUPPORT=0` on the `msiexec` command line to decline
   (an Event Server machine installed with the box unticked never has its certificate stores
   touched — start the installer again or run the legacy script below if proxy support is
   wanted later).
   *Legacy/repair fallback:* if the plugin reached the Event Server machine without the MSI
   (the dev `Copy-Item` shortcut), run this once from an elevated Windows PowerShell 5.1
   session on that machine instead:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/provision-event-server-authority.ps1
   ```
2. **Each Smart Client machine trusts the CA once, elevated.** Get the public certificate onto each
   Smart Client machine and import it into the machine's trusted-root store. The Management Client
   ships it at any time: under **Cascadia
   Technology > Web Content Workspaces**, the **Export proxy certificate** button saves the CA's
   public certificate (`*.cer`, DER) — read from the Event Server over MIP messaging, with no
   private key in sight, and it works no matter how the CA was created (the installer's proxy opt-in
   checkbox, lazy first-use, or the legacy script).

   - **Recommended for more than one machine: push it with Active Directory Group Policy.** Import
     the exported `.cer` into a GPO at *Computer Configuration > Policies > Windows Settings >
     Security Settings > Public Key Policies > Trusted Root Certification Authorities*. Every
     domain-joined Smart Client machine picks the CA up on its next policy refresh — no per-machine
     logon, and new machines get it automatically.
   - One machine at a time, copy the exported `.cer` to it and run:

   ```powershell
   Import-Certificate -FilePath C:\Users\Public\WebContent-Proxy-Authority.cer -CertStoreLocation Cert:\LocalMachine\Root
   ```

   Until that import is done, proxied HTTPS pages load but show a browser certificate error — the plugin
   never bypasses certificate validation on its own. The Smart Client view also says so directly: any
   proxied view whose deployment CA is missing from the machine's trusted-root stores shows a banner
   to every user: *"The proxy certificate is not trusted on this PC - contact your administrator."*
   (dismissible for the session; it returns next session until the CA is trusted). An administrator in
   **setup mode** additionally gets a **Fix certificate trust** button on that banner: it launches the
   bundled `WebContentTrustHelper.exe` elevated. Windows shows its elevation consent *and* the
   certificate-store warning, both deliberately and never suppressed, and the tool imports exactly
   the reported CA (thumbprint-verified, nothing else). Once the CA is trusted the banner clears and
   proxied HTTPS pages reload without restarting Smart Client. If the
   button answers that the proxy authority certificate does not exist yet, no CA has been created on the
   Event Server machine — complete step 1 first.

    You do not have to wait for users to report those banners: each client machine that hits the
    condition reports it to the Event Server once (per machine, not per view), and the Management
    Client's **Diagnostics** tab for the workspace then shows an aggregate warning — "N client
    machines report the proxy CA is not trusted. Export it from here and distribute it." — listing
    each affected machine, with the same **Export proxy certificate** button inline. The list lives
    only in the Event Server's memory (it resets when the service restarts), so it is a live signal,
    not an audit trail.

To remove the plugin completely, also delete the CA: from `LocalMachine\My` on the Event Server machine
(`Remove-Item Cert:\LocalMachine\My\<thumbprint>`) and from `LocalMachine\Root` on each Smart Client machine.

### Add Web Content to a view

1. Open a new Smart Client instance (plugins are only discovered during startup) and login.
2. Click the **Setup** button at the top right.
3. Select an existing view, or create a new view.
4. Locate **Cascadia Technology > Web Content** in the **System Overview** side panel, and drag it into the view.

![Screenshot of plugin in system overview list](./docs/assets/images/systemoverview.png)

### Configuration

1. With the new Web View selected, scroll to the bottom of the side panel where you'll find the properties.
2. Update the Home address, and any other options as needed.
3. Click the **Setup** button again to exit setup mode.

![Screenshot of web content view item properties](./docs/assets/images/properties.png)

These per-view properties apply to a **manually placed** Web Content view. A view that lives on
an admin-defined workspace (see below) keeps its **home/target URL and proxy posture locked**:
the field is still shown, but rendered read-only with a "Managed by Administrator" note, and
any stored override of those security-relevant settings is ignored at every load. Cosmetic and
navigation options (toolbar, address bar, zoom, audio, history, isolation, developer tools)
stay editable per instance.

## Workspace administration (Management Client)

The plugin also integrates with the XProtect Management Client. Under **Cascadia Technology**, a
**Web Content Workspaces** node lets administrators centrally define workspaces: their home address,
display options, behavior (background running, audio notifications, history clearing), privacy and
advanced options such as session isolation and developer tools.

![Web Content Workspaces configured in the Management Client](./docs/assets/images/mc-workspace.png)

### Per-role security

Each workspace is a securable item. On the **Cascadia Technology** tab of a role's security settings,
select a workspace and grant or revoke the granular claims for that role, such as viewing and managing
the web content workspace.

![Cascadia Technology security tab for a role in the Management Client](./docs/assets/images/mc-role-security.png)

## Proxied web access (route workspace traffic through the VMS server)

Some devices (cameras, intercoms, vendor portals) serve their web pages on a network segment the
operator's computer cannot reach. The **workspace proxy** fixes this: for a proxied workspace the
browser's web traffic is routed away from the operator machine and out of the VMS server's network
instead. The Smart Client fronts the browser with a machine-local forwarding proxy, relays every
request over classic MIP messaging, and the VMS-side plugin component performs the actual fetch.
The operator's computer never contacts the device, and a proxied workspace never silently falls
back to a direct connection.

### What the admin toggles, and where

On the workspace's details page in the **Management Client** under
**Cascadia Technology > Web Content Workspaces**:

1. Open the workspace's **Proxy** tab and tick **Route web traffic through the VMS server proxy**.
2. Fill the **Proxy targets** grid — one row per allowed origin: pick the **scheme**, enter the
   **host** (a wildcard may be `*.foo.example.com`, which covers that domain's subdomains but never
   the apex, or a lone `*`), and add a **port** only when the target is not on the scheme default.
   Tick **Accept untrusted** on the row whose device serves a self-signed certificate (the
   untrusted-certificate override is per target, never global; see the certificate authority
   section above before using `https://` targets). **Add target** and the per-row **Remove** button
   change the row count; a lone `*` row raises a warning banner while it is in the grid.
3. Save. A row the matcher rejects is flagged in its **Problem** column and the whole save is
   refused by name — nothing is silently dropped. A proxied workspace with no valid targets
   stays proxied and refuses everything — the allowlist is never bypassed by direct access.

The toggle takes effect when the workspace tab is (re)opened in Smart Client; the Smart Client
properties page shows the effective proxy state for the workspace.

### The computer's own proxy (WinINET): inherited when off, ignored when on

A view **without** the workspace proxy injects no proxy arguments into its browser, so the
browser simply follows the computer's own web-proxy configuration — on Windows that is the
per-user WinINET proxy ("Internet Options → LAN settings"; the `ProxyEnable`/`ProxyServer`
values, settable machine-wide through Group Policy or ProxyConfig-style tools). To put such
views' traffic through a corporate proxy, configure that OS setting for the users running the
Smart Client. The plugin deliberately has **no proxy-URL setting of its own** — the operating
system already owns that (including proxy authentication and PAC), and non-proxied views
inherit it for free.

A **proxied** workspace deliberately ignores that OS proxy. Its browser profile launches with
every scheme pinned to the machine-local loopback proxy (`--proxy-server=http=http://127.0.0.1:<port>;https=...;ws=...;wss=...`)
and the implicit localhost bypass disabled (`--proxy-bypass-list=<-loopback>`), so a system
proxy can never widen what a proxied workspace loads — the allowlist posture is decided by
the VMS server, and nothing reaches the network except through it. Verified live (SC-8 in
`docs/verification-matrix.md`): with the user's WinINET proxy pointed at a local logging
harness, a non-proxied view renders the harness's marker page for a host that can never
resolve directly, while the proxied view of the same url shows the plugin's own 403 refusal
page and the OS-proxy harness never sees a single request for that origin.

### The server side is the XProtect EVENT SERVER

The component that performs the fetches runs **inside the XProtect Event Server process — not the
Management Server**. The machine running the Event Server needs the plugin drop installed on it as
well as on every Smart Client machine. The plugin definition loads in the `Service` environment, and
the Event Server only loads MIP plugins at **service start**, so a deployment includes a service
restart.

**The supported way to deploy to that machine is the release MSI, run on the machine itself** — the
same `CascadiaWebContentPlugin.<version>.msi` as everywhere else. On an Event Server machine it:

- stops `MilestoneEventServerService` (waiting for the real stop, no fixed sleep) **only** when the
  plugin is already installed there, copies the files into
  `C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin`, and restarts the service per the
  wizard's **Restart the Event Server now?** choice — first installs never stop the running service,
  and machines without an Event Server see none of this (see the install section above);
- on a first install with the service running, installs the files without touching the service —
  the plugin then loads at the next Event Server start;
- runs unattended as `msiexec /i CascadiaWebContentPlugin.<version>.msi /qn` (add
  `RESTART_EVENT_SERVER=0` to leave the restart scheduled by hand).

Verify any deployment by **SHA-256 hash against the build drop**, never by timestamp (the copy
preserves `LastWriteTime`), and confirm the fetcher loaded by checking the Event Server's MIP log
for `Proxy fetcher ready (concurrency=3, request id=WebContent.Proxy.FetchRequest)` — or use the
Management Client workspace page's **Test proxy fetch** row for an end-to-end check.

One caveat for a machine whose drop was previously placed by file copy rather than by the installer:
files a `Copy-Item` left behind are untracked by MSI, and a stray at the **same file version** as the
MSI's payload shadows it during an upgrade — the registration bumps while the stray stays. The
supported migration is a pure-`msiexec` uninstall/install cycle: stop `MilestoneEventServerService`,
`msiexec /x {product-code} /qn` (this also removes the strays, which the upgrade has now registered),
confirm the drop folder is gone, then run the new MSI. Verified live in `docs/verification-matrix.md`
(IN-3).

`scripts/deploy-event-server-plugin.ps1` (and the psake `DeployToVms` task) are **dev/test shortcuts**:
the remote, repeatable file-copy form of the old procedure for lab machines. They are not the
supported deployment path, and they explicitly do **not** set up proxy support — the certificate
authority for `https://` targets is the separate one-time setup above either way.

Two follow-ups matter after any Event Server plugin deployment:

- **Restart every Smart Client that was connected before the restart.** The Event Server restart
  disposes each client's messaging channel; without a Smart Client restart every proxied page
  fails with the proxy's "server proxy component is not available" error page (and the log shows
  `Call to TransmitMessage after call to Dispose?`).

### Rule triggers (fire VMS rules from proxied requests)

A **rule trigger** turns one served proxy request into a VMS event the normal rule engine can
react to — an operator notification, a saved view, an export, whatever the rule builder offers.
Each workspace keeps its own list of trigger rows; when a proxied fetch **completes with a
response** and matches a row, the plugin raises one analytics event on the Event Server bus.
Matching runs on the Event Server after the response exists, so transport failures (no status)
and CONNECT tunnels never trigger, and a match never changes the browser's response.

The event type is fixed — **`WebContent proxy request`** — for every trigger in every workspace.
Per-request detail rides inside the event (workspace name, method, full url, HTTP status, the
matching row), so the admin writes **one** Analytics Event definition
(*Management Client → Event definitions*, matching the message text `WebContent proxy request`,
source = the workspace item) and then builds as many rules on it as desired.

A trigger row has three parts:

| Part | Written as | Matches |
|---|---|---|
| **url** | `scheme://host[:port][/path-glob]` | the FULL fetched url. Scheme `http`, `https`, or `*`; host exact, `*.tail` (one or more labels below the tail — never the apex, same wildcard as the allowlist), or a lone `*` after the scheme; a **port only when the url names that port** (an absent port means the scheme default, so `:8443` traffic needs `:8443` in the row); the path glob is literal text where `*` spans any run **including `/`** and `?` is exactly one character — an absent path means any path. Query strings and fragments never take part. A lone `*` as the whole url matches anything. |
| **method** | `GET`, `POST`, … (case-insensitive) | exact verb; blank means any method. |
| **status** | `4xx` or `404` | the response status: a class (`1xx`…`5xx`) or an exact code; blank means any status. |

A row the matcher refuses is reported once per configuration refresh in the Event Server's MIP
log (`Workspace rows refreshed: N row(s)...` plus a named per-row reason) and never takes down the
workspace's other rows. Trigger rows live in the workspace configuration alongside the proxy
allowlist. Administrators maintain them from the **Proxy tab** of the workspace configuration
dialog, in the **Rule triggers** grid between the permission headers and the test fetch: each row
has a URL pattern, a Method dropdown (`*` wildcard first, then the relayable verbs), a Status code
(blank = any, `1xx`–`5xx` class, or an exact code), a read-only **Event raised** column showing
which event type the row fires (`WebContent proxy request`), and a live problem column. Rows are
checked against the same grammar the Event Server plugin enforces when it audits proxy traffic, so
a row the dialog accepts can never be refused at runtime; an invalid row is marked in place and
refuses the whole save, naming it. Rows are evaluated in the order shown — one request raises at
most the first matching row's event — and saving with no rows stores no trigger policy at all. To confirm the
event channel on a freshly deployed Event Server before any rules exist, place a
`trigger-selftest.flag` file next to the deployed `WebContent.dll` and restart the service — the
Event Server MIP log shows the probe event raised and seen on the bus with the same id (see
`docs/verification-matrix.md`, ES-3).

### Versions, logs, and failure pages

- The relay rides classic MIP message communication (Event Server message service, default port
  22333). Event Server plugins have been able to register message filters from `Init()` since
  **XProtect 2019 R3** (earlier releases silently failed that registration), so proxied workspaces
  require XProtect 2019 R3 or later. Verified against **XProtect 2024 R2** in the lab where this
  document's deployment records were made.
- Logs for the proxy path: Smart Client plugin activity lands in
  `%ProgramData%\Milestone\XProtect Smart Client\MIPLog.txt` (rolling `MIPLogs\MIPLog.N.txt`);
  the Event Server writes its MIP log to `%ProgramData%\Milestone\XProtect Event Server\logs\MIP.log`
  with rolled archives under `logs\MIPLogs\` (the Event Server tray icon's "Show MIP logs" opens
  them); Management Client diagnostics (including the Test proxy fetch
  harness) go to `Logs\MIP.log` under its ProgramData folder.
- When a proxied page fails, the **browser itself shows an explicit proxy error page** naming the
  failing side — never a silent direct load and never an endless spinner. Each page carries a
  friendly headline, exactly one **cause category** chip, and a collapsed "Technical details"
  section (exact origin, the canonical allowlist entry that would admit it, the byte cap, the
  verbatim refusal text, a "Copy diagnostics" button). Off-allowlist **https** refusals ride the
  page too (#88): Chromium transports every https target to the loopback proxy only as a CONNECT
  and drops the body of a refused CONNECT, so the proxy's own page can never reach the screen for
  those — the browser view answers the same 403 page itself, at the requested URL, before any
  CONNECT is opened, using the exact same allowlist the proxy gates on. The one remaining
  asymmetry: **ws:// / wss:// websocket upgrades** are refused at the CONNECT layer and still
  render Chromium's native error page (websocket requests are not surfaced to the view's
  interception); fail-closed is identical everywhere — nothing is ever loaded direct.
  - **Not allowed by the administrator** (403) — the target origin is not in the workspace
    allowlist (the details name the origin and the exact entry to add). The same 403 also appears
    when the VMS server and the Smart Client disagree because their allowlists drifted.
  - **VMS proxy component unavailable** (503) — the proxy component on the Event Server is absent,
    unreachable, or not answering (deploy check above; Smart Client restart needed after an Event
    Server restart).
  - **Target unreachable / Response too large (proxy size cap) / Certificate not trusted /
    Proxy tunnel failure** (502) — the VMS server reached the messaging layer but the target
    connection, the response size cap, the target's TLS certificate, or the proxy tunnel refused.
  - **Timeout** (504) — the VMS server fetched the target but the target timed out.

## License

The Web Content Plugin is free to use with all features. No license,
activation, or time limit is required. It is distributed under the
[MIT License](./LICENSE).

## Software Build of Materials

You will find a software build of materials, also known as an SBOM (S-bomb), in the `_manifest` subdirectory of the
plugin installation path at `C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin\_manifest\spdx_2.2\`. In this
folder you will find the SBOM file `manifest.spdx.json` in SPDX:2.2 format, along with `manifest.spdx.json.sha256` which
is a SHA256 hash of the SBOM file.

These files are generated using Microsoft's [sbom-tool](https://github.com/microsoft/sbom-tool) and the same tool can be
used to validate your own software installation. To do so, you would install `sbom-tool` per Microsoft's documentation
and then run the following command.

```plaintext
dotnet sbom-tool validate -b C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin\ -o .sbom-validation.txt -mi SPDX:2.2
```

Or, if the `sbom-tool` command is installed globally on `PATH` you can drop the leading `dotnet` cli command name.

```plaintext
sbom-tool validate -b C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin\ -o .sbom-validation.txt -mi SPDX:2.2
```
