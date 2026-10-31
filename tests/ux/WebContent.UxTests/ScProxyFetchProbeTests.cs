using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Diagnostic probe (#54): launches and logs in a Smart Client, opens the proxied
/// "Demo Workspace", and navigates its browser view to the workspace's allowlisted
/// http target. The view's WebView2 is pinned to the plugin's loopback forwarding
/// proxy, so a rendered page proves the full browser -> loopback listener -> MIP
/// messaging -> Event Server fetcher round-trip. Artifacts land in
/// webcontent-sc-proxy-probe\&lt;run&gt; (sc-proxy-probe-*.png / .txt).
/// </summary>
public class ScProxyFetchProbeTests
{
    private const string FetchUrl = "http://httpforever.com/";

    private readonly ITestOutputHelper _output;

    public ScProxyFetchProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedWorkspaceBrowserFetch()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

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

        var log = new System.Text.StringBuilder();
        try
        {
            // SC-1 negative control (docs/verification-matrix.md): when the client machine's own
            // route to the target is deliberately broken (hosts entry pointing it at 127.0.0.1),
            // a direct hit must fail while the proxied page still renders - proving the traffic
            // originates on the Event Server machine.
            var blockedControl = Environment.GetEnvironmentVariable("WEBCONTENT_SC1_BLOCKED") == "1";

            main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(main);
            Assert.NotNull(item);

            var addressBar = item.FindFirstDescendant(cf => cf.ByAutomationId("addressBar"));
            if (addressBar == null)
            {
                throw new InvalidOperationException("No addressBar in the proxied workspace view; cannot drive the fetch probe.");
            }

            // Give the plugin's ProxyFetchClient filter registration its documented 3-4 s
            // head start before the first fetch is attempted through the loopback listener.
            Thread.Sleep(6000);

            SmartClientUi.NavigateAddress(addressBar, FetchUrl);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, "httpforever.com");

            // The loopback fetch has the MIP round-trip plus the Event Server's own GET to
            // satisfy; give it well past the client-side 30 s deadline to be conclusive.
            var body = await page.WaitForSelectorAsync("body", new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 45000 });
            var text = body == null ? null : await body.TextContentAsync();
            var title = await page.TitleAsync();

            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = Artifacts.PathOf("sc-proxy-probe-page.png"),
            });

            log.AppendLine($"navigated {FetchUrl} -> title='{title}' bodyChars={(text?.Length ?? -1)}");
            if (!string.IsNullOrEmpty(text))
            {
                var snippet = text.Replace('\r', ' ').Replace('\n', ' ');
                log.AppendLine($"body snippet: {snippet.Substring(0, Math.Min(200, snippet.Length))}");
            }

            if (blockedControl)
            {
                // The control is only meaningful if this very process cannot reach the target
                // directly from the client machine while the browser view above did.
                using var direct = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                try
                {
                    var response = await direct.GetAsync(FetchUrl);
                    log.AppendLine($"direct client-side GET -> HTTP {(int)response.StatusCode} (control INVALID: direct path not broken)");
                }
                catch (Exception directFailure)
                {
                    log.AppendLine($"direct client-side GET -> FAILED as required: {directFailure.GetType().Name}: {directFailure.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"probe failed: {ex.GetType().Name}: {ex.Message}");
            var fresh = sc.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
            if (fresh != null)
            {
                Artifacts.Window(fresh, "sc-proxy-probe-failure");
            }
        }
        finally
        {
            Artifacts.WriteText("sc-proxy-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }
    }
}
