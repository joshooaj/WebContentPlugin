using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Live acceptance probe for #57 (SC-5 in docs/verification-matrix.md): a vendor page opened in
/// the proxied Smart Client browser establishes a WEBSOCKET, and the connection survives the
/// full chain — browser -> loopback listener (upgrade recognized, not fetched) -> MIP messaging
/// tunnel -> Event Server raw socket -> target 101 -> raw frames both directions. The page's
/// echoed token is client-side proof; the echo server's WS-HANDSHAKE/WS-ECHO journal is
/// server-side proof the upgrade really arrived as a websocket (not as a refused fetch). The
/// run is self-provisioning like the login probes: it ensures the ws origin (spelled as its
/// http twin) sits in the workspace proxy allowlist first.
/// Artifacts land in webcontent-sc-proxy-probe\&lt;run&gt; (sc-ws-probe-*.png / .txt).
/// </summary>
public class ScProxyWebSocketProbeTests
{
    /// <summary>
    /// Origin the ws-echo page is served from and the workspace must allowlist (the ws://
    /// origin spelled as its http twin — the proxy gates the upgrade on the http origin).
    /// Override with UX_WS_TARGET when this machine's server-network address differs.
    /// </summary>
    private const string DefaultWsTarget = "http://172.16.128.210:18125";

    private readonly ITestOutputHelper _output;

    public ScProxyWebSocketProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedWebSocketEchoRoundTrip()
    {
        var target = Environment.GetEnvironmentVariable("UX_WS_TARGET") ?? DefaultWsTarget;
        var outcome = await RunWebSocketProbeAsync(target);

        Assert.True(outcome.Failure == null, $"probe run failed: {outcome.Failure}{Environment.NewLine}{outcome.Log}");
        Assert.NotNull(outcome.Title);
        Assert.False(titleIsProxyError(outcome.Title), $"browser showed the proxy error page instead of the ws page: '{outcome.Title}'");
        Assert.NotNull(outcome.WsStatus);
        Assert.True(
            outcome.WsStatus.StartsWith("WS-OK:", StringComparison.Ordinal),
            $"the websocket round trip did not echo the token: '{outcome.WsStatus}'{Environment.NewLine}{outcome.Log}");
        Assert.True(
            outcome.ServerJournal.Any(entry => entry.Contains("WS-HANDSHAKE", StringComparison.Ordinal)),
            $"the ws server never recorded the upgrade handshake{Environment.NewLine}{outcome.Log}");
        Assert.True(
            outcome.ServerJournal.Any(entry => entry.Contains("WS-ECHO", StringComparison.Ordinal)),
            $"the ws server never echoed a frame back through the tunnel{Environment.NewLine}{outcome.Log}");
    }

    private static bool titleIsProxyError(string title) =>
        title != null && title.StartsWith("Web Content proxy:", StringComparison.OrdinalIgnoreCase);

    private sealed class ProbeOutcome
    {
        public string Failure;
        public string Title;
        public string WsStatus;
        public IReadOnlyList<string> ServerJournal = new string[0];
        public string Log = string.Empty;
    }

    private async Task<ProbeOutcome> RunWebSocketProbeAsync(string target)
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var targetUri = new Uri(target);
        var log = new System.Text.StringBuilder();
        var outcome = new ProbeOutcome();
        string failure = null;
        WebSocketEchoServer server = null;

        try
        {
            // Phase 1: the allowlist entry for the ws origin (its http twin) must exist before
            // the browser asks for the upgrade; an unlisted origin is a 403 page, not a tunnel.
            ProxyTargetProvisioning.Ensure(config, target, log);

            // Phase 2: serve the ws-echo site on THIS machine; the Event Server proves server-
            // network reachability by dialing it for the tunnel while the browser never goes direct.
            server = new WebSocketEchoServer(targetUri.Port);
            log.AppendLine($"ws-echo server bound to port {targetUri.Port}, origin {target}, allowlist entry '{target}'");

            // Phase 3: drive the real page in the proxied Smart Client browser.
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
                throw new InvalidOperationException("No addressBar in the proxied workspace view; cannot drive the ws probe.");
            }

            // Filter-registration head start (documented 3-4 s), same as the other probes.
            Thread.Sleep(6000);

            SmartClientUi.NavigateAddress(addressBar, targetUri.AbsoluteUri);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, targetUri.Host);

            // The page reports WS-PENDING until the echoed token lands (or a failure verdict).
            await page.WaitForFunctionAsync(
                "() => { var el = document.getElementById('ws-status'); return el && el.textContent && el.textContent !== 'WS-PENDING'; }",
                null,
                new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 60000 });
            outcome.WsStatus = await (await page.QuerySelectorAsync("#ws-status"))?.TextContentAsync();
            outcome.Title = await page.TitleAsync();
            log.AppendLine($"ws verdict: status='{outcome.WsStatus}' title='{outcome.Title}'");

            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = Artifacts.PathOf("sc-ws-probe-page.png"),
            });

            // Hold the closed-loop evidence window open briefly so the server journal captures
            // the close frames too.
            await Task.Delay(1500);
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
            outcome.ServerJournal = server == null
                ? new string[0]
                : (IReadOnlyList<string>)server.Journal;
            foreach (var entry in outcome.ServerJournal)
            {
                log.AppendLine($"server: {entry}");
            }

            server?.Dispose();
            outcome.Log = log.ToString();
            Artifacts.WriteText("sc-ws-probe-results.txt", outcome.Log);
            _output.WriteLine(outcome.Log);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        return outcome;
    }
}
