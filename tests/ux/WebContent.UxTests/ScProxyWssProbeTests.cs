using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Live acceptance probes for #60 (SC-6 in docs/verification-matrix.md): a wss:// websocket
/// inside a PROXIED HTTPS page rides the same byte tunnel as plain ws — browser CONNECT ->
/// loopback listener terminates the tunnel TLS (ES-minted leaf) -> in-tunnel upgrade GET is
/// recognized as a websocket tunnel (NOT refused 501) -> MIP messaging tunnel -> Event Server
/// dials a fresh TLS leg to the target, replays the rebuilt handshake, admits only the 101,
/// then pumps decrypted frames both ways. The two rows pin both TLS postures on the Event
/// Server's leg: with the probe certificate imported into the Event Server's LocalMachine\Root
/// store (WssTargetTrust) the round trip echoes the token; with an untrusted chain the Event
/// Server refuses before the upgrade ever reaches the target (the target journal shows the TLS
/// leg dialed but never the WS handshake) and the page reports a failed websocket, never an
/// echo. The allowlist posture is scheme-exact: the entry is the https origin itself.
/// Artifacts land in webcontent-sc-proxy-probe\&lt;run&gt; (sc-wss-probe-*.png / .txt).
/// </summary>
public class ScProxyWssProbeTests
{
    /// <summary>Origin the wss-echo page is served from and allowlisted (plain http twin, same
    /// shape as the ws probe). Override with UX_WSS_PAGE_TARGET when this machine's
    /// server-network address differs.</summary>
    private const string DefaultPageTarget = "http://172.16.128.210:18125";

    /// <summary>#60 success row: the wss target origin (its own port, so its https allowlist
    /// entry never collides with the page origin). Override with UX_WSS_TARGET.</summary>
    private const string DefaultWssTarget = "https://172.16.128.210:18126";

    /// <summary>#60 fail-closed row: a second TLS origin whose certificate is deliberately NOT
    /// imported, so the Event Server's chain validation must refuse it. UX_WSS_UNTRUSTED_TARGET.</summary>
    private const string DefaultUntrustedWssTarget = "https://172.16.128.210:18127";

    private readonly ITestOutputHelper _output;

    public ScProxyWssProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedWssEchoRoundTrip()
    {
        var page = Environment.GetEnvironmentVariable("UX_WSS_PAGE_TARGET") ?? DefaultPageTarget;
        var target = Environment.GetEnvironmentVariable("UX_WSS_TARGET") ?? DefaultWssTarget;

        var outcome = await RunWssProbeAsync(
            pageTarget: page,
            wssTarget: target,
            importProbeCertificate: true,
            artifactPrefix: "sc-wss-probe");

        Assert.True(outcome.Failure == null, $"probe run failed: {outcome.Failure}{Environment.NewLine}{outcome.Log}");
        Assert.NotNull(outcome.Title);
        Assert.False(titleIsProxyError(outcome.Title), $"browser showed the proxy error page instead of the wss page: '{outcome.Title}'");
        Assert.NotNull(outcome.WsStatus);
        Assert.True(
            outcome.WsStatus.StartsWith("WS-OK:", StringComparison.Ordinal),
            $"the wss round trip did not echo the token: '{outcome.WsStatus}'{Environment.NewLine}{outcome.Log}");
        Assert.True(
            outcome.ServerJournal.Any(entry => entry.Contains("TLS-HANDSHAKE", StringComparison.Ordinal)),
            $"the wss server never completed a TLS handshake with the Event Server{Environment.NewLine}{outcome.Log}");
        Assert.True(
            outcome.ServerJournal.Any(entry => entry.Contains("WS-HANDSHAKE", StringComparison.Ordinal)),
            $"the wss server never recorded the upgrade handshake{Environment.NewLine}{outcome.Log}");
        Assert.True(
            outcome.ServerJournal.Any(entry => entry.Contains("WS-ECHO", StringComparison.Ordinal)),
            $"the wss server never echoed a frame back through the tunnel{Environment.NewLine}{outcome.Log}");
    }

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedWssTlsChainFailureRefusesBeforeTheUpgradeArrives()
    {
        var page = Environment.GetEnvironmentVariable("UX_WSS_PAGE_TARGET") ?? DefaultPageTarget;
        var target = Environment.GetEnvironmentVariable("UX_WSS_UNTRUSTED_TARGET") ?? DefaultUntrustedWssTarget;

        // Same allowlist posture as the success row (the https origin, no flag), but the probe
        // certificate is NOT trusted on the Event Server: the tunnel TLS leg must fail chain
        // validation there, and only the failure side effects may appear.
        var outcome = await RunWssProbeAsync(
            pageTarget: page,
            wssTarget: target,
            importProbeCertificate: false,
            artifactPrefix: "sc-wss-probe-tls-closed");

        Assert.True(outcome.Failure == null, $"probe run failed: {outcome.Failure}{Environment.NewLine}{outcome.Log}");
        Assert.NotNull(outcome.Title);
        Assert.False(titleIsProxyError(outcome.Title), $"the page origin is plain http: only the wss leg should fail, got '{outcome.Title}'");
        Assert.NotNull(outcome.WsStatus);
        Assert.True(
            outcome.WsStatus.StartsWith("WS-FAIL", StringComparison.Ordinal),
            $"the untrusted wss target must fail the websocket, never echo: '{outcome.WsStatus}'{Environment.NewLine}{outcome.Log}");
        Assert.True(
            outcome.ServerJournal.Any(entry => entry.Contains("TLS-CONNECT", StringComparison.Ordinal)),
            $"the wss server never saw the Event Server's TLS dial{Environment.NewLine}{outcome.Log}");
        Assert.True(
            !outcome.ServerJournal.Any(entry => entry.Contains("WS-HANDSHAKE", StringComparison.Ordinal)),
            $"the upgrade reached the target despite the Event Server refusing its TLS chain:{Environment.NewLine}{outcome.Log}");
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

    private async Task<ProbeOutcome> RunWssProbeAsync(
        string pageTarget,
        string wssTarget,
        bool importProbeCertificate,
        string artifactPrefix)
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var pageUri = new Uri(pageTarget);
        var targetUri = new Uri(wssTarget);
        var log = new System.Text.StringBuilder();
        var outcome = new ProbeOutcome();
        string failure = null;
        WebSocketEchoServer wssServer = null;
        WebSocketEchoServer pageServer = null;

        try
        {
            // Phase 1: BOTH origins must sit in the workspace allowlist before the browser asks:
            // the plain http origin for the page, the https origin itself for the wss posture
            // (an http twin alone is a 403 for the upgrade; the entry scheme IS the posture).
            ProxyTargetProvisioning.Ensure(config, pageTarget, log);
            ProxyTargetProvisioning.Ensure(config, wssTarget, log);

            // Phase 2: serve both sites on THIS machine; the Event Server proves server-network
            // reachability by dialing the https origin for the tunnel TLS leg while the browser
            // never goes direct.
            wssServer = new WebSocketEchoServer(targetUri.Port, useTls: true, tlsHost: targetUri.Host);
            log.AppendLine($"wss-echo server bound to port {targetUri.Port}, origin {wssTarget}, allowlist entry '{wssTarget}'");
            if (importProbeCertificate)
            {
                if (WssTargetTrust.ImportEnabled)
                {
                    WssTargetTrust.ImportOnEventServer(config, wssServer.TlsCertificate, log);
                }
                else
                {
                    log.AppendLine("UX_WSS_TRUST_IMPORT=0: assuming the target certificate is trusted on the Event Server by other means");
                }
            }
            else
            {
                log.AppendLine("fail-closed row: deliberately NOT importing the probe certificate (chain validation must refuse it)");
            }

            pageServer = new WebSocketEchoServer(pageUri.Port, useTls: false, probePageHtml: BuildWssProbePage(wssTarget));
            log.AppendLine($"wss-echo page served from {pageTarget}, allowlist entry '{pageTarget}', dialing wss target {wssTarget}");

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
                throw new InvalidOperationException("No addressBar in the proxied workspace view; cannot drive the wss probe.");
            }

            // Filter-registration head start (documented 3-4 s), same as the other probes.
            Thread.Sleep(6000);

            SmartClientUi.NavigateAddress(addressBar, pageUri.AbsoluteUri);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, pageUri.Host);

            // The page reports WS-PENDING until the echoed token lands (or a failure verdict:
            // a refused TLS leg on the Event Server surfaces to the browser as a failed
            // websocket handshake, never as a fetchable error document).
            await page.WaitForFunctionAsync(
                "() => { var el = document.getElementById('ws-status'); return el && el.textContent && el.textContent !== 'WS-PENDING'; }",
                null,
                new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 90000 });
            outcome.WsStatus = await (await page.QuerySelectorAsync("#ws-status"))?.TextContentAsync();
            outcome.Title = await page.TitleAsync();
            log.AppendLine($"wss verdict: status='{outcome.WsStatus}' title='{outcome.Title}'");

            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = Artifacts.PathOf($"{artifactPrefix}-page.png"),
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
            outcome.ServerJournal = wssServer == null
                ? new string[0]
                : (IReadOnlyList<string>)wssServer.Journal;
            foreach (var entry in outcome.ServerJournal)
            {
                log.AppendLine($"wss server: {entry}");
            }

            wssServer?.Dispose();
            pageServer?.Dispose();
            outcome.Log = log.ToString();
            Artifacts.WriteText($"{artifactPrefix}-results.txt", outcome.Log);
            _output.WriteLine(outcome.Log);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        return outcome;
    }

    /// <summary>The page loads from the plain http origin and dials the https origin by
    /// absolute url, so the fail-closed row can show a failed websocket while the page
    /// itself renders fine.</summary>
    private static string BuildWssProbePage(string wssUrl) =>
        "<!DOCTYPE html><html><head><title>wss-echo-probe</title></head><body>" +
        "<h1>wss tunnel probe</h1>" +
        "<div id=\"ws-status\">WS-PENDING</div>" +
        "<script>" +
        "var token = 'WSS-TOKEN-' + Math.random().toString(36).slice(2);" +
        "try {" +
        $"  var socket = new WebSocket('{wssUrl}/echo');" +
        "  socket.onopen = function () { socket.send(token); };" +
        "  socket.onmessage = function (event) {" +
        "    document.getElementById('ws-status').textContent = event.data === token ? 'WS-OK:' + event.data : 'WS-MISMATCH:' + event.data;" +
        "    socket.close();" +
        "  };" +
        "  socket.onerror = function () { document.getElementById('ws-status').textContent = 'WS-FAIL:error'; };" +
        "  socket.onclose = function (event) {" +
        "    var el = document.getElementById('ws-status');" +
        "    if (el.textContent === 'WS-PENDING') { el.textContent = 'WS-FAIL:closed-' + event.code; }" +
        "  };" +
        "} catch (e) { document.getElementById('ws-status').textContent = 'WS-FAIL:' + e.message; }" +
        "</script></body></html>";
}
