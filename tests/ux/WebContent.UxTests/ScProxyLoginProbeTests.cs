using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Live acceptance probes for #55 (SC-2 in docs/verification-matrix.md): a vendor-style login
/// flow through the proxied workspace browser. A POST login form, a 302 + Set-Cookie session
/// round-trip, a cookie-gated dashboard, and a background XHR all cross the
/// browser -> loopback listener -> MIP messaging -> Event Server fetcher chain with their
/// method, headers, and body intact. The probe is self-setting: it ensures the login server's
/// origin sits in the workspace proxy allowlist (Management Client phase), serves the login
/// site from this machine via <see cref="LoginTestServer"/> (the Event Server fetches it from
/// the server network), then drives the login in Smart Client over CDP.
/// Artifacts land in webcontent-sc-proxy-probe\&lt;run&gt; (sc-login-probe-*.png / .txt).
///
/// #56 adds the two HTTPS rows: the same site behind a self-signed certificate, allowlisted
/// WITHOUT the per-target override (the Event Server must refuse the target chain and the
/// browser must show the proxy error page — fail-closed), and allowlisted WITH
/// "accept-untrusted" (the whole login flow must complete over the loopback-terminated tunnel).
/// Both rows presuppose the ADR-0003 deployment: the Event Server holds the deployment CA in
/// LocalMachine\Personal and this machine trusts it in LocalMachine\Root, so the browser's
/// tunnel TLS to the loopback listener validates cleanly and any visible failure is the
/// TARGET's certificate decision, not the loopback leaf's.
/// </summary>
public class ScProxyLoginProbeTests
{
    /// <summary>
    /// Origin the login site is served from and the workspace must allowlist. Override with
    /// UX_LOGIN_TARGET when this machine's server-network address differs; the port must match
    /// the allowlisted entry because the fetcher is driven at exactly this origin.
    /// </summary>
    private const string DefaultLoginTarget = "http://172.16.128.210:18123";

    /// <summary>
    /// #56: the self-signed HTTPS origin for the two TLS rows (its own port, so its allowlist
    /// entry never collides with the plain-HTTP login origin).
    /// </summary>
    private const string DefaultHttpsLoginTarget = "https://172.16.128.210:18124";

    private readonly ITestOutputHelper _output;

    public ScProxyLoginProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedVendorLoginFlow()
    {
        var target = Environment.GetEnvironmentVariable("UX_LOGIN_TARGET") ?? DefaultLoginTarget;

        var outcome = await RunLoginProbeAsync(
            allowlistEntry: target,
            target: target,
            useTls: false,
            artifactPrefix: "sc-login-probe");

        // The probe goes RED like SC-1: a login that loops back to the form, a proxy error page,
        // or a background XHR that comes back denied all fail the run, not merely record it.
        Assert.True(outcome.Failure == null, $"probe run failed: {outcome.Failure}{Environment.NewLine}{outcome.Log}");
        Assert.NotNull(outcome.Title);
        Assert.False(titleIsProxyError(outcome.Title), $"browser showed the proxy error page instead of the vendor site: '{outcome.Title}'");
        Assert.Equal("SIGNED IN", outcome.SignedIn);
        Assert.NotNull(outcome.XhrStatus);
        Assert.StartsWith("XHR-OK", outcome.XhrStatus);
        Assert.Contains("POST /login", outcome.Log);
    }

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedHttpsTargetFailsClosedWithoutOverride()
    {
        var target = Environment.GetEnvironmentVariable("UX_HTTPS_LOGIN_TARGET") ?? DefaultHttpsLoginTarget;

        // Allowlisted WITHOUT the accept-untrusted flag: the Event Server fetcher must refuse
        // the self-signed target chain (TlsValidation), and the browser must see the proxy
        // error page - never the login form, never a silent pass-through.
        var outcome = await RunLoginProbeAsync(
            allowlistEntry: target,
            target: target,
            useTls: true,
            artifactPrefix: "sc-login-probe-tls-closed");

        Assert.True(outcome.Failure == null, $"probe run failed: {outcome.Failure}{Environment.NewLine}{outcome.Log}");
        Assert.True(
            titleIsProxyError(outcome.Title),
            $"fail-closed expected the proxy error page for the untrusted target, got title '{outcome.Title}'{Environment.NewLine}{outcome.Log}");
        Assert.Contains("accept untrusted", outcome.PageBody ?? string.Empty);
        Assert.True(
            outcome.ServerJournal.Any(entry => entry.Contains("TLS", StringComparison.OrdinalIgnoreCase)),
            $"the login server never recorded the refused TLS attempt{Environment.NewLine}{outcome.Log}");
    }

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedHttpsLoginFlowWithAcceptUntrustedOverride()
    {
        var target = Environment.GetEnvironmentVariable("UX_HTTPS_LOGIN_TARGET") ?? DefaultHttpsLoginTarget;

        // The same target WITH the per-entry override: the fetcher accepts the untrusted chain
        // for exactly this origin, and the full login/session/XHR flow completes through the
        // loopback-terminated tunnel.
        var entry = $"{target} {AcceptUntrustedToken}";
        var outcome = await RunLoginProbeAsync(
            allowlistEntry: entry,
            target: target,
            useTls: true,
            artifactPrefix: "sc-login-probe-tls-open");

        Assert.True(outcome.Failure == null, $"probe run failed: {outcome.Failure}{Environment.NewLine}{outcome.Log}");
        Assert.NotNull(outcome.Title);
        Assert.False(titleIsProxyError(outcome.Title), $"override did not open the tunnel for the untrusted target: '{outcome.Title}'");
        Assert.Equal("SIGNED IN", outcome.SignedIn);
        Assert.NotNull(outcome.XhrStatus);
        Assert.StartsWith("XHR-OK", outcome.XhrStatus);
        Assert.Contains("POST /login", outcome.Log);
    }

    /// <summary>ProxyPolicy.AcceptUntrustedFlag, inlined (the harness cannot see internals).</summary>
    private const string AcceptUntrustedToken = "accept-untrusted";

    private static bool titleIsProxyError(string title) =>
        title != null && title.StartsWith("Web Content proxy:", StringComparison.OrdinalIgnoreCase);

    private sealed class ProbeOutcome
    {
        public string Failure;
        public string Title;
        public string PageBody;
        public string SignedIn;
        public string XhrStatus;
        public string CookieEcho;
        public IReadOnlyList<string> ServerJournal = new string[0];
        public string Log = string.Empty;
    }

    /// <summary>
    /// Shared probe run: MC allowlist phase (the entry replaces any older line for the same
    /// origin, so the fail-closed and override rows cannot leave contradictory entries), then
    /// Smart Client + CDP drive. Rows that expect the proxy error page pass no login
    /// interaction beyond navigation: the form never appears there, so the run captures the
    /// error document and returns.
    /// </summary>
    private async Task<ProbeOutcome> RunLoginProbeAsync(
        string allowlistEntry,
        string target,
        bool useTls,
        string artifactPrefix)
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var targetUri = new Uri(target);
        var log = new System.Text.StringBuilder();
        var outcome = new ProbeOutcome();
        string failure = null;
        LoginTestServer server = null;

        try
        {
            // Phase 1: the workspace proxy allowlist must carry the login origin before the
            // Smart Client logs in — a target the fetcher refuses is a NotAllowed frame, not a
            // login page.
            EnsureAllowlistedTarget(config, allowlistEntry, log);

            // Phase 2: serve the vendor site on THIS machine; the Event Server proves server-side
            // reachability by fetching it from the VMS network while the browser never goes direct.
            server = new LoginTestServer(targetUri.Port, useTls);
            log.AppendLine($"{(useTls ? "https" : "http")} login server bound to port {targetUri.Port}, origin {target}, allowlist entry '{allowlistEntry}'");

            // Phase 3: drive the real login flow in the proxied Smart Client browser.
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            using var sc = ClientSession.Launch(config.SmartClientExe);
            var login = LoginFlow.WaitForLoginWindow(sc);
            LoginFlow.Login(sc, login, config);
            LoginFlow.AcceptCertificateTrust(sc);
            var main = LoginFlow.WaitForSmartClientMain(sc);
            SmartClientUi.Maximize(main);
            Thread.Sleep(1500);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

            main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(main);
            Assert.NotNull(item);

            var addressBar = item.FindFirstDescendant(cf => cf.ByAutomationId("addressBar"));
            if (addressBar == null)
            {
                throw new InvalidOperationException("No addressBar in the proxied workspace view; cannot drive the login probe.");
            }

            // Filter-registration head start (documented 3-4 s), same as the SC-1 probe.
            Thread.Sleep(6000);

            SmartClientUi.NavigateAddress(addressBar, targetUri.AbsoluteUri);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, targetUri.Host);

            // Playwright THROWS on a selector timeout; for the fail-closed row the missing form
            // is the expected outcome (the proxy error page renders instead), so translate the
            // timeout into a null form and let the branch below classify the document.
            Microsoft.Playwright.IElementHandle form;
            try
            {
                form = await page.WaitForSelectorAsync("#signin", new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 45000 });
            }
            catch (Microsoft.Playwright.PlaywrightTimeoutException)
            {
                form = null;
            }

            outcome.Title = await page.TitleAsync();
            log.AppendLine($"first document at {target}: title='{outcome.Title}' form={form != null}");
            if (form == null)
            {
                // No login form: either a genuine failure or the expected fail-closed error page.
                outcome.PageBody = await page.ContentAsync();
                await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
                {
                    Path = Artifacts.PathOf($"{artifactPrefix}-document.png"),
                });

                if (!titleIsProxyError(outcome.Title))
                {
                    throw new InvalidOperationException($"login form not rendered at {target} — page shows something else");
                }

                // Expected fail-closed document: capture and return without login interaction.
                outcome.ServerJournal = Snapshot(server);
                outcome.Log = log.ToString();
                return outcome;
            }

            await page.FillAsync("#username", LoginTestServer.Username);
            await page.FillAsync("#password", LoginTestServer.Password);
            await page.ClickAsync("#signin");

            // The POST -> 302+Set-Cookie -> GET /dashboard chain runs entirely through the proxy;
            // only a session that round-tripped reaches the signed-in marker.
            var marker = await page.WaitForSelectorAsync("#signed-in", new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 60000 });
            if (marker == null)
            {
                throw new InvalidOperationException("#signed-in never appeared — cookie/session round-trip failed");
            }

            outcome.SignedIn = await marker.TextContentAsync();
            outcome.Title = await page.TitleAsync();
            outcome.CookieEcho = await (await page.QuerySelectorAsync("#cookie-echo"))?.TextContentAsync();

            // The dashboard's background fetch resolves asynchronously — reading #xhr-status the
            // instant the dashboard renders races it and catches the XHR-PENDING placeholder.
            // Wait for the XHR to land (either verdict), THEN read it.
            await page.WaitForFunctionAsync(
                "() => { var el = document.getElementById('xhr-status'); return el && el.textContent && el.textContent !== 'XHR-PENDING'; }",
                null,
                new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 30000 });
            outcome.XhrStatus = await (await page.QuerySelectorAsync("#xhr-status"))?.TextContentAsync();

            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = Artifacts.PathOf($"{artifactPrefix}-dashboard.png"),
            });

            log.AppendLine($"after login: title='{outcome.Title}' signedIn='{outcome.SignedIn}' xhr='{outcome.XhrStatus}' cookieEcho='{outcome.CookieEcho}'");
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            if (ex is AggregateException agg)
            {
                detail = string.Join(";", agg.Flatten().InnerExceptions.Select(i => i.GetType().Name + ": " + i.Message));
            }
            failure = ex.GetType().Name + ": " + detail;
            log.AppendLine("probe failed: " + failure);
            log.AppendLine(ex.ToString());
        }
        finally
        {
            outcome.Failure = failure;
            outcome.ServerJournal = Snapshot(server);
            foreach (var entry in outcome.ServerJournal)
            {
                log.AppendLine($"server: {entry}");
            }

            server?.Dispose();
            outcome.Log = log.ToString();
            Artifacts.WriteText($"{artifactPrefix}-results.txt", outcome.Log);
            _output.WriteLine(outcome.Log);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        return outcome;
    }

    private static IReadOnlyList<string> Snapshot(LoginTestServer server)
    {
        if (server == null)
        {
            return new string[0];
        }

        lock (server)
        {
            return server.Journal;
        }
    }

    /// <summary>
    /// Management Client phase: make sure the workspace proxy target list carries exactly one
    /// line for the probe origin — the entry as given, replacing any older line for the same
    /// origin so a previous run's flag state never leaks into this row's verdict. Saving only
    /// when the list actually changed. Runs before the Smart Client so the fetched
    /// configuration carries the new allowlist.
    /// </summary>
    private void EnsureAllowlistedTarget(UxConfig config, string entry, System.Text.StringBuilder log)
    {
        ProcessKiller.KillClients("VideoOS.Administration");
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        ManagementClientUi.CloseStrayLoginDialogs(mc);
        ManagementClientUi.WaitForTreeReady(mcMain);

        // Canonical starting state (fast double-click collapse, no keyboard), then walk the
        // known chain top-down (enumerate rows + click + wheel scroll): each step realizes the
        // rows the next one needs, so no blind walking or long sleeps are involved.
        ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
        ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
        ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
        ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);

        var targetsBox = ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 30);
        var current = ReadValue(targetsBox);
        log.AppendLine($"proxy targets before: '{current}'");

        var lines = (current ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Any(line => string.Equals(line, entry, StringComparison.OrdinalIgnoreCase)))
        {
            log.AppendLine($"entry '{entry}' already allowlisted; no Management Client edit needed");
            RetireManagementClient(mc, mcMain);
            return;
        }

        // Same origin with a different flag state is a contradictory entry: exactly one line
        // decides the origin's TLS posture.
        var originToken = entry.Split(' ')[0];
        lines.RemoveAll(line => line.Split(' ')[0].Equals(originToken, StringComparison.OrdinalIgnoreCase));

        ManagementClientUi.EnsureCheckBox(mcMain, "chkProxyEnabled", true);
        lines.Add(entry);
        ManagementClientUi.SetValue(mcMain, "txtProxyTargets", string.Join("\n", lines));

        if (!ManagementClientUi.Save(mc, mcMain, 20))
        {
            throw new InvalidOperationException($"Save never became enabled after adding '{entry}' to the proxy targets.");
        }

        // Read the pane back: a save the shell silently discarded (or a confirmation whose
        // No/Cancel path was taken) must fail here, not strand the Smart Client phase with a
        // stale allowlist.
        Thread.Sleep(1500);
        var after = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 15));
        if (after == null || !after.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.Trim().Equals(entry, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"After Save, the proxy targets read back '{after}' — '{entry}' did not stick.");
        }

        log.AppendLine($"allowlisted '{entry}' and saved (verified readback)");
        RetireManagementClient(mc, mcMain);
    }

    /// <summary>
    /// Closes Management Client after its phase: an MC left open can hold a pending
    /// "Save changes?" modal that steals focus and blocks UIA for the Smart Client phase
    /// (the #56 hang). Close politely, sweep any confirmation it raises, then guarantee exit.
    /// </summary>
    private static void RetireManagementClient(ClientSession mc, FlaUI.Core.AutomationElements.Window mcMain)
    {
        try { mcMain.Close(); } catch { }
        ManagementClientUi.AcceptPendingModal(mc, 5);
        ProcessKiller.KillClients("VideoOS.Administration");
    }

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name; }
        catch { return element.Name; }
    }
}
