using System.Text;
using FlaUI.Core.AutomationElements;
using Microsoft.Playwright;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Diagnostic probe (#83, verification-matrix SC-7): the setup-mode blocked-resources
/// indicator. A page loads FINE through the proxy while its subresources (two images +
/// a script) point at an origin the workspace does NOT allowlist - the loopback listener
/// refuses them client-side (403 NotAllowed) and tallies them per browser-view instance.
/// The contract:
///   1. nothing renders in runtime mode for ordinary users (indicator absent/offscreen),
///   2. entering setup mode shows "N resources blocked" near the address bar,
///   3. clicking it lists origin (selectable), reason, and hit count,
///   4. a top-level navigation resets the counters - the clean twin page hides the indicator,
///   5. re-blocking in runtime mode stays invisible until setup mode shows it again.
///
/// The page origin is http://172.16.128.210:18125 - an origin the workspace allowlist has
/// carried since the #56/#57 probes provisioned it, so this run drives NO Management Client
/// phase: the blocked page's title is itself the proof the entry is still in place (a missing
/// entry renders the "Web Content proxy:" error page instead, which the assertions call out).
/// Set UX_REQUIRE_MC_PROVISIONING=1 to run the guarded MC Ensure phase first. The blocked
/// subresource origin (port 18131) is deliberately never allowlisted and never served: the
/// refusal happens at the loopback listener before the Event Server is ever asked.
///
/// The run drives the Smart Client on UIA patterns + CDP only (no SendInput): a disconnected
/// desktop session rejects mouse/keyboard injection with ERROR_ACCESS_DENIED, so navigation
/// goes through CDP, the workspace comes up as the persisted last one (with a pattern-select
/// fallback), and window evidence uses the hang-guarded PrintWindow capture. Artifacts land
/// in webcontent-sc-proxy-probe\&lt;run&gt;.
/// </summary>
public class ScProxyBlockedIndicatorProbeTests
{
    /// <summary>Page origin already carried by the workspace allowlist (provisioned by the #56/#57 probes).</summary>
    private static string BindHost => Environment.GetEnvironmentVariable("UX_BLOCKED_HOST") ?? "172.16.128.210";
    private static int PagePort => ReadPort("UX_BLOCKED_PAGE_PORT", 18125);
    private static int BlockedOriginPort => ReadPort("UX_BLOCKED_ASSET_PORT", 18131);

    private readonly ITestOutputHelper _output;

    public ScProxyBlockedIndicatorProbeTests(ITestOutputHelper output) => _output = output;

    private static int ReadPort(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var port) ? port : fallback;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeSetupModeBlockedResourcesIndicator()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        string failure = null;
        string pageTitle = null;
        bool runtimeIndicatorHidden = false;
        bool indicatorVisibleInSetup = false;
        string indicatorText = null;
        bool originRowFound = false;
        string originRowValue = null;
        bool reasonRowFound = false;
        string countRowValue = null;
        bool hiddenAfterCleanNavigation = false;
        bool runtimeIndicatorHiddenAfterReblock = false;
        bool setupShowsReblockedTally = false;

        BlockedResourcePageServer server = null;
        ClientSession sc = null;
        try
        {
            var allowlistEntry = $"http://{BindHost}:{PagePort}";
            if (Environment.GetEnvironmentVariable("UX_REQUIRE_MC_PROVISIONING") == "1")
            {
                ProxyTargetProvisioning.Ensure(config, allowlistEntry, log);
            }
            else
            {
                log.AppendLine($"reusing the workspace allowlist entry '{allowlistEntry}' provisioned by the #56/#57 probes; no Management Client phase (the page title assertion proves it is still in place)");
            }

            server = new BlockedResourcePageServer(BindHost, PagePort, BlockedOriginPort);
            log.AppendLine($"page origin (allowlisted): {server.PageOrigin}; blocked subresource origin: {server.BlockedOrigin}");

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

            // Smart Client reopens the workspace shown at the previous run; the proxied
            // workspace is the last one every proxy probe used. Only fall back to tab
            // selection (pattern-based, no mouse) when the plugin view did not come up.
            AutomationElement item = null;
            try { item = SmartClientUi.WaitForMipViewItem(main, 20); } catch (TimeoutException) { }
            if (item == null)
            {
                log.AppendLine("plugin view not on the opening workspace; pattern-selecting the tab (no input injection)");
                if (SmartClientUi.TrySelectWorkspaceWithoutInput(main, sc, config.WorkspaceName))
                {
                    main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
                }
                item = SmartClientUi.WaitForMipViewItem(main);
            }
            Assert.NotNull(item);
            log.AppendLine("proxied workspace view is up (runtime mode)");

            // Filter-registration head start (documented 3-4 s), same as the other probes.
            Thread.Sleep(6000);

            // Runtime phase: the page loads through the proxy, its subresources are refused
            // client-side; navigation goes through CDP (no keyboard, and the address bar
            // would persist the probe url onto the workspace).
            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await WaitForBrowserPageAsync(browser, 90);
            await page.GotoAsync($"{server.PageOrigin}/blocked-page/", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60000 });
            await page.WaitForSelectorAsync("#probe-marker", new PageWaitForSelectorOptions { Timeout = 30000 });
            pageTitle = await page.TitleAsync();
            log.AppendLine($"blocked-subresource page (Event Server proxied) -> title='{pageTitle}'");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifacts.PathOf("sc-blocked-indicator-page.png") });

            // Give Chromium a moment to issue the three subresource requests; their refusals
            // reach the tally even though nothing renders in runtime mode.
            Thread.Sleep(5000);
            item = SmartClientUi.WaitForMipViewItem(main);
            runtimeIndicatorHidden = !IndicatorRendered(item, log, "runtime");
            log.AppendLine($"runtime posture: indicator rendered = {!runtimeIndicatorHidden}");

            // Setup mode: the tally surfaces as "N resources blocked".
            log.AppendLine("entering setup mode...");
            SmartClientUi.ClickSetup(main);
            Thread.Sleep(2000);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            SmartClientUi.CloseSidePanels(main);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !indicatorVisibleInSetup)
            {
                item = SmartClientUi.WaitForMipViewItem(main);
                indicatorVisibleInSetup = IndicatorRendered(item, log, "setup");
                if (!indicatorVisibleInSetup) Thread.Sleep(1000);
            }
            log.AppendLine($"setup posture: indicator visible = {indicatorVisibleInSetup}");

            if (indicatorVisibleInSetup)
            {
                var text = item.FindFirstDescendant(cf => cf.ByAutomationId("blockedIndicatorText"));
                indicatorText = text?.Name;
                log.AppendLine($"indicator text: '{indicatorText}'");

                // Click to open the per-view list.
                SmartClientUi.TryInvokeById(main, "blockedIndicator", 1500);
                var rowsDeadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < rowsDeadline && !originRowFound)
                {
                    item = SmartClientUi.WaitForMipViewItem(main);
                    var origin = item.FindFirstDescendant(cf => cf.ByAutomationId("blockedResourceOrigin"));
                    if (origin != null)
                    {
                        originRowValue = ReadElementValue(origin);
                        originRowFound = string.Equals(originRowValue, server.BlockedOrigin, StringComparison.OrdinalIgnoreCase);
                        var reason = item.FindFirstDescendant(cf => cf.ByAutomationId("blockedResourceReason"));
                        reasonRowFound = reason != null && (reason.Name ?? "").Contains("not allowed", StringComparison.OrdinalIgnoreCase);
                        var hits = item.FindFirstDescendant(cf => cf.ByAutomationId("blockedResourceCount"));
                        countRowValue = hits?.Name;
                    }
                    if (!originRowFound) Thread.Sleep(1000);
                }
                log.AppendLine($"list row: origin='{originRowValue}' reason-row={reasonRowFound} count='{countRowValue}'");
                SafeWindowPrint(main, "sc-blocked-indicator-setup", log);
            }
            else
            {
                SafeWindowPrint(main, "sc-blocked-indicator-setup-missing", log);
            }

            // Navigation resets the counters like the browser's own console: the clean twin
            // page (same origin, no blocked subresources) must hide the indicator again.
            await page.GotoAsync($"{server.PageOrigin}/clean/", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60000 });
            var cleanDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < cleanDeadline && await page.TitleAsync() != "UX Probe CLEANPAGE") await Task.Delay(500);
            log.AppendLine($"clean twin page -> title='{await page.TitleAsync()}'");
            var hideDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < hideDeadline && !hiddenAfterCleanNavigation)
            {
                item = SmartClientUi.WaitForMipViewItem(main);
                hiddenAfterCleanNavigation = !IndicatorRendered(item, log, "after-clean");
                if (!hiddenAfterCleanNavigation) Thread.Sleep(1000);
            }
            log.AppendLine($"after clean navigation: indicator hidden = {hiddenAfterCleanNavigation}");

            // Back to runtime mode and re-block: tallies keep counting invisibly, and setup
            // mode shows them again - the reset-then-recount cycle end to end.
            log.AppendLine("leaving setup mode and re-blocking in runtime...");
            SmartClientUi.ClickSetup(main);
            Thread.Sleep(2000);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            await page.GotoAsync($"{server.PageOrigin}/blocked-page/", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60000 });
            Thread.Sleep(6000);
            item = SmartClientUi.WaitForMipViewItem(main);
            runtimeIndicatorHiddenAfterReblock = !IndicatorRendered(item, log, "runtime-reblock");
            log.AppendLine($"runtime re-block posture: indicator rendered = {!runtimeIndicatorHiddenAfterReblock}");

            log.AppendLine("re-entering setup mode to prove the runtime-phase refusals tallied...");
            SmartClientUi.ClickSetup(main);
            Thread.Sleep(2000);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            SmartClientUi.CloseSidePanels(main);
            var recountDeadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < recountDeadline && !setupShowsReblockedTally)
            {
                item = SmartClientUi.WaitForMipViewItem(main);
                setupShowsReblockedTally = IndicatorRendered(item, log, "setup-recount");
                if (!setupShowsReblockedTally) Thread.Sleep(1000);
            }
            log.AppendLine($"setup re-count posture: indicator visible = {setupShowsReblockedTally}");
            SafeWindowPrint(main, "sc-blocked-indicator-recount", log);
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}{Environment.NewLine}{ex}");
        }
        finally
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillClients("VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            sc?.Dispose();
            server?.Dispose();
            Artifacts.WriteText("sc-blocked-indicator-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
        Assert.NotNull(pageTitle);
        Assert.False(pageTitle.StartsWith("Web Content proxy:"), $"the top-level document itself was refused (is 'http://{BindHost}:{PagePort}' still in the workspace allowlist?): '{pageTitle}'");
        Assert.True(runtimeIndicatorHidden, "the blocked-resources indicator rendered in runtime mode - ordinary users must see nothing");
        Assert.True(indicatorVisibleInSetup, "setup mode did not show the blocked-resources indicator despite client-side subresource refusals");
        Assert.NotNull(indicatorText);
        Assert.Matches(@"\d+ resources? blocked", indicatorText);
        Assert.True(originRowFound, $"the list did not carry the canonical blocked origin '{server.BlockedOrigin}' (read: '{originRowValue}')");
        Assert.True(reasonRowFound, "the list row did not name the not-allowed refusal category");
        Assert.True(hiddenAfterCleanNavigation, "the tally did not reset on a top-level navigation to a clean page");
        Assert.True(runtimeIndicatorHiddenAfterReblock, "the indicator rendered in runtime mode after fresh refusals were tallied");
        Assert.True(setupShowsReblockedTally, "setup mode did not re-show the indicator after runtime-mode re-blocking");
    }

    /// <summary>First CDP page whose url is an http(s) page of the plugin browser.</summary>
    private static async Task<IPage> WaitForBrowserPageAsync(IBrowser browser, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var page = browser.Contexts
                .SelectMany(c => c.Pages)
                .FirstOrDefault(p => (p.Url ?? string.Empty).StartsWith("http", StringComparison.OrdinalIgnoreCase));
            if (page != null) return page;
            await Task.Delay(1000);
        }
        throw new TimeoutException("The proxied browser never exposed an http page over CDP. Seen: " +
            string.Join(", ", browser.Contexts.SelectMany(c => c.Pages).Select(p => p.Url)));
    }

    private static void SafeWindowPrint(Window main, string name, StringBuilder log)
    {
        try
        {
            var file = Artifacts.WindowPrint(main, name);
            log.AppendLine(file == null ? $"window capture '{name}' abandoned (no screen surface)" : $"window capture '{name}' -> {file}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"window capture '{name}' threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the plugin view renders the indicator control (visible on screen, not just
    /// present in the automation tree - Collapsed WPF chrome stays offscreen).
    /// </summary>
    private static bool IndicatorRendered(AutomationElement viewItem, StringBuilder log, string context)
    {
        var indicator = viewItem.FindFirstDescendant(cf => cf.ByAutomationId("blockedIndicator"));
        if (indicator == null)
        {
            return false;
        }

        try
        {
            if (indicator.IsOffscreen)
            {
                return false;
            }

            var rect = indicator.BoundingRectangle;
            return rect.Width > 0 && rect.Height > 0;
        }
        catch (Exception ex)
        {
            log.AppendLine($"  indicator visibility read failed ({context}): {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string ReadElementValue(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported) return element.Patterns.Value.Pattern.Value.Value;
        }
        catch { /* fall back to Name below */ }
        return element.Name;
    }
}
