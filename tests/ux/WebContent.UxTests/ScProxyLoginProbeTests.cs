using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Live acceptance probe for #55 (SC-2 in docs/verification-matrix.md): a vendor-style login
/// flow through the proxied workspace browser. A POST login form, a 302 + Set-Cookie session
/// round-trip, a cookie-gated dashboard, and a background XHR all cross the
/// browser -> loopback listener -> MIP messaging -> Event Server fetcher chain with their
/// method, headers, and body intact. The probe is self-setting: it ensures the login server's
/// origin sits in the workspace proxy allowlist (Management Client phase), serves the login
/// site from this machine via <see cref="LoginTestServer"/> (the Event Server fetches it from
/// the server network), then drives the login in Smart Client over CDP.
/// Artifacts land in webcontent-sc-proxy-probe\&lt;run&gt; (sc-login-probe-*.png / .txt).
/// </summary>
public class ScProxyLoginProbeTests
{
    /// <summary>
    /// Origin the login site is served from and the workspace must allowlist. Override with
    /// UX_LOGIN_TARGET when this machine's server-network address differs; the port must match
    /// the allowlisted entry because the fetcher is driven at exactly this origin.
    /// </summary>
    private const string DefaultLoginTarget = "http://172.16.128.210:18123";

    private readonly ITestOutputHelper _output;

    public ScProxyLoginProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedVendorLoginFlow()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var target = Environment.GetEnvironmentVariable("UX_LOGIN_TARGET") ?? DefaultLoginTarget;
        var targetUri = new Uri(target);
        var log = new System.Text.StringBuilder();
        string failure = null;
        string title = null;
        string signedIn = null;
        string xhrStatus = null;
        string cookieEcho = null;
        LoginTestServer server = null;

        try
        {
            // Phase 1: the workspace proxy allowlist must carry the login origin before the
            // Smart Client logs in — a target the fetcher refuses is a NotAllowed frame, not a
            // login page.
            EnsureAllowlistedTarget(config, target, log);

            // Phase 2: serve the vendor site on THIS machine; the Event Server proves server-side
            // reachability by fetching it from the VMS network while the browser never goes direct.
            server = new LoginTestServer(targetUri.Port);
            log.AppendLine($"login server bound to port {targetUri.Port}, origin {target}");

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

            var form = await page.WaitForSelectorAsync("#signin", new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 45000 });
            if (form == null)
            {
                throw new InvalidOperationException($"login form not rendered at {targetUri} — page shows something else");
            }

            title = await page.TitleAsync();
            log.AppendLine($"login page title='{title}'");

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

            signedIn = await marker.TextContentAsync();
            title = await page.TitleAsync();
            cookieEcho = await (await page.QuerySelectorAsync("#cookie-echo"))?.TextContentAsync();

            // The dashboard's background fetch resolves asynchronously — reading #xhr-status the
            // instant the dashboard renders races it and catches the XHR-PENDING placeholder.
            // Wait for the XHR to land (either verdict), THEN read it.
            await page.WaitForFunctionAsync(
                "() => { var el = document.getElementById('xhr-status'); return el && el.textContent && el.textContent !== 'XHR-PENDING'; }",
                null,
                new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 30000 });
            xhrStatus = await (await page.QuerySelectorAsync("#xhr-status"))?.TextContentAsync();

            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = Artifacts.PathOf("sc-login-probe-dashboard.png"),
            });

            log.AppendLine($"after login: title='{title}' signedIn='{signedIn}' xhr='{xhrStatus}' cookieEcho='{cookieEcho}'");
            lock (server)
            {
                foreach (var entry in server.Journal)
                {
                    log.AppendLine($"server: {entry}");
                }
            }
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}");
        }
        finally
        {
            server?.Dispose();
            Artifacts.WriteText("sc-login-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        // The probe goes RED like SC-1: a login that loops back to the form, a proxy error page,
        // or a background XHR that comes back denied all fail the run, not merely record it.
        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
        Assert.NotNull(title);
        Assert.False(title.StartsWith("Web Content proxy:"), $"browser showed the proxy error page instead of the vendor site: '{title}'");
        Assert.Equal("SIGNED IN", signedIn);
        Assert.NotNull(xhrStatus);
        Assert.StartsWith("XHR-OK", xhrStatus);
        Assert.Contains("POST /login", string.Join("\n", log));
    }

    /// <summary>
    /// Management Client phase: make sure the login origin is in the McWorkspace proxy target
    /// list (proxy enabled), saving only when the list actually changed. Runs before the Smart
    /// Client so the fetched configuration carries the new allowlist.
    /// </summary>
    private void EnsureAllowlistedTarget(UxConfig config, string target, System.Text.StringBuilder log)
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

        if (lines.Any(line => string.Equals(line, target, StringComparison.OrdinalIgnoreCase)))
        {
            log.AppendLine($"target '{target}' already allowlisted; no Management Client edit needed");
            return;
        }

        ManagementClientUi.EnsureCheckBox(mcMain, "chkProxyEnabled", true);
        lines.Add(target);
        ManagementClientUi.SetValue(mcMain, "txtProxyTargets", string.Join("\n", lines));

        if (!ManagementClientUi.Save(mcMain, 20))
        {
            throw new InvalidOperationException($"Save never became enabled after adding '{target}' to the proxy targets.");
        }

        log.AppendLine($"allowlisted '{target}' and saved");
    }

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name; }
        catch { return element.Name; }
    }
}
