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

    /// <summary>
    /// The origin form of <see cref="FetchUrl"/> as the workspace allowlist stores it: no
    /// trailing slash. The fetch probe depends on this entry; the login probes learned the
    /// hard way that the entry must be (re)provisioned, not assumed, after the workspace
    /// item was recreated.
    /// </summary>
    private const string AllowlistEntry = "http://httpforever.com";

    private readonly ITestOutputHelper _output;

    public ScProxyFetchProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedWorkspaceBrowserFetch()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;
        string title = null;
        var bodyChars = -1;
        var directReachedTarget = false;
        var blockedControl = Environment.GetEnvironmentVariable("WEBCONTENT_SC1_BLOCKED") == "1";
        ClientSession sc = null;
        try
        {
            // Phase 1 (Management Client): provision the allowlist entry this probe depends
            // on before the Smart Client logs in. Assuming the workspace keeps the entry
            // forever is what turned this row red (403) after the Demo Workspace item was
            // recreated without it.
            ProxyTargetProvisioning.Ensure(config, AllowlistEntry, log);

            // Phase 2: fresh Smart Client so it fetches the updated configuration.
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            sc = ClientSession.Launch(config.SmartClientExe);
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
                throw new InvalidOperationException("No addressBar in the proxied workspace view; cannot drive the fetch probe.");
            }

            // Give the plugin's ProxyFetchClient filter registration its documented 3-4 s
            // head start before the first fetch is attempted through the loopback listener.
            Thread.Sleep(6000);

            // SC-1 negative control (docs/verification-matrix.md): when the client machine's own
            // route to the target is deliberately broken (hosts entry pointing it at 127.0.0.1),
            // a direct hit must fail while the proxied page still renders - proving the traffic
            // originates on the Event Server machine. Establish the block BEFORE rendering, so a
            // run whose block is missing goes red instead of reporting the gap post hoc.
            if (blockedControl)
            {
                directReachedTarget = await DirectGetReachesTargetAsync();
                if (directReachedTarget)
                {
                    throw new InvalidOperationException(
                        "control INVALID: the client machine still reaches the target directly; add the hosts entry (127.0.0.1 httpforever.com # webcontent-sc-1-block) before this run");
                }
                log.AppendLine("control pre-check: direct client-side GET already fails (route is blocked)");
            }

            SmartClientUi.NavigateAddress(addressBar, FetchUrl);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, "httpforever.com");

            // The loopback fetch has the MIP round-trip plus the Event Server's own GET to
            // satisfy; give it well past the client-side 30 s deadline to be conclusive.
            var body = await page.WaitForSelectorAsync("body", new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 45000 });
            var text = body == null ? null : await body.TextContentAsync();
            title = await page.TitleAsync();
            bodyChars = text?.Length ?? -1;

            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = Artifacts.PathOf("sc-proxy-probe-page.png"),
            });

            log.AppendLine($"navigated {FetchUrl} -> title='{title}' bodyChars={bodyChars}");
            if (!string.IsNullOrEmpty(text))
            {
                var snippet = text.Replace('\r', ' ').Replace('\n', ' ');
                log.AppendLine($"body snippet: {snippet.Substring(0, Math.Min(200, snippet.Length))}");
            }

            if (blockedControl)
            {
                // Re-check after the render: the control must still hold at verdict time.
                directReachedTarget = await DirectGetReachesTargetAsync();
                log.AppendLine(directReachedTarget
                    ? "direct client-side GET -> reached the target (control INVALID: direct path not broken)"
                    : "direct client-side GET -> FAILED as required while the proxied page rendered");
            }
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}");
            var fresh = sc?.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
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
            sc?.Dispose();
        }

        // The probe goes RED: evidence in the artifacts file is for humans, but a proxy that
        // never completes, an error page that renders instead of the target, or a control run
        // whose direct route was never broken must fail the run, not merely record it.
        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
        Assert.NotNull(title);
        Assert.False(title.StartsWith("Web Content proxy:"), $"browser showed the proxy error page instead of the target: '{title}'");
        Assert.True(bodyChars >= 500, $"target body too small to be the real page ({bodyChars} chars)");
        if (blockedControl)
        {
            Assert.False(directReachedTarget, "SC-1 control invalid: the client reached the target directly while the proxied page rendered");
        }
    }

    private static async Task<bool> DirectGetReachesTargetAsync()
    {
        using var direct = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            await direct.GetAsync(FetchUrl);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
