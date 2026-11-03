using System.Text;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

using Microsoft.Playwright;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #84 probe: the Management Client's Diagnostics tab end to end against the live Event
/// Server. A Smart Client browser view loads (its origin is allowlisted) while its subresources
/// point at an origin the workspace does NOT allowlist - the Smart Client's loopback listener
/// refuses them and forwards every refusal into the Event Server's in-memory blocked ring
/// (#69/#83 plumbing). The contract pinned here:
///   1. the Diagnostics tab renders the blocked origin with its "not allowed" reason, and the
///      tab drives the SAME status-query pair as the Status tab ONLY while it is open (the MC
///      MIP.log marker cadence is the polling trace, #76's discipline);
///   2. the row's one-click "Add to allowlist" lands the exact origin as a row in the Proxy
///      tab's structured grid (visible there UNSAVED), annotates the Diagnostics row
///      "on allowlist", hides its own button, and arms the page's normal Save;
///   3. the promotion saves through the ordinary allowlist save discipline - the audit line
///      names the promoted origin and a RELOADED pane re-renders it (the "equivalent to the
///      allowlist editor's save, survives a workspace re-read" acceptance criterion);
///   4. after the reload the Diagnostics row re-annotates itself "on allowlist" from the SAVED
///      grid - and a CertUntrusted-style refusal never offers promotion by construction (the
///      builder hides the button; pinned in the unit layer, not reproducible against this ES).
/// The probe mutates the workspace allowlist briefly and restores it verbatim (the #73 probe's
/// WithoutProbeLines discipline), so it leaves no residue. Run alone.
/// </summary>
public class McDiagnosticsTabProbeTests
{
    /// <summary>Same page pair the #83 probe drives: the page origin has been carried by the
    /// workspace allowlist since the #56/#57 probes; the subresource origin never is.</summary>
    private static string BindHost => Environment.GetEnvironmentVariable("UX_BLOCKED_HOST") ?? "172.16.128.210";
    private static int PagePort => ReadPort("UX_BLOCKED_PAGE_PORT", 18125);
    private static int BlockedOriginPort => ReadPort("UX_BLOCKED_ASSET_PORT", 18131);

    /// <summary>Marker prefixes logged by WorkspaceConfigPage.RefreshStatus - the same pair the
    /// Status tab probe counts (#76), because both tabs ride one poll loop (#84).</summary>
    private const string SnapshotMarker = "Proxy status snapshot for workspace";
    private const string QueryFailMarker = "Proxy status query for workspace";

    private const string PromotionMarker = "Proxy allowlist promotion for workspace";

    private readonly ITestOutputHelper _output;

    public McDiagnosticsTabProbeTests(ITestOutputHelper output) => _output = output;

    private static int ReadPort(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var port) ? port : fallback;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeDiagnosticsTabPromotesLiveBlockedOriginIntoTheSavedAllowlist()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-diagnostics-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        Window mcMain = null;
        ClientSession mc = null;
        ClientSession sc = null;
        BlockedResourcePageServer server = null;
        try
        {
            server = new BlockedResourcePageServer(BindHost, PagePort, BlockedOriginPort);
            var pageOrigin = server.PageOrigin;
            var blockedOrigin = server.BlockedOrigin;
            log.AppendLine($"page origin (allowlisted): {pageOrigin}; blocked subresource origin: {blockedOrigin}");

            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            ProcessKiller.KillClients("Client", "VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            // ---- Smart Client phase: put a live blocked origin into the ES ring ----
            sc = ClientSession.Launch(config.SmartClientExe);
            var scLogin = LoginFlow.WaitForLoginWindow(sc);
            LoginFlow.Login(sc, scLogin, config);
            LoginFlow.AcceptCertificateTrust(sc);
            var scMain = LoginFlow.WaitForSmartClientMain(sc);
            SmartClientUi.Maximize(scMain);
            Thread.Sleep(1500);
            scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

            AutomationElement item = null;
            try { item = SmartClientUi.WaitForMipViewItem(scMain, 20); } catch (TimeoutException) { }
            if (item == null)
            {
                log.AppendLine("plugin view not on the opening workspace; pattern-selecting the tab (no input injection)");
                if (SmartClientUi.TrySelectWorkspaceWithoutInput(scMain, sc, config.WorkspaceName))
                {
                    scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
                }
                item = SmartClientUi.WaitForMipViewItem(scMain);
            }
            Assert.NotNull(item);
            log.AppendLine("proxied workspace view is up (runtime mode)");

            // Filter-registration head start, same as the other proxy probes.
            Thread.Sleep(6000);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await WaitForBrowserPageAsync(browser, 90);
            await page.GotoAsync($"{pageOrigin}/blocked-page/", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60000 });
            await page.WaitForSelectorAsync("#probe-marker", new PageWaitForSelectorOptions { Timeout = 30000 });
            var pageTitle = await page.TitleAsync();
            log.AppendLine($"blocked-subresource page (Event Server proxied) -> title='{pageTitle}'");
            Assert.False(pageTitle.StartsWith("Web Content proxy:"),
                $"the top-level document itself was refused (is '{pageOrigin}' still in the workspace allowlist?): '{pageTitle}'");

            // Give Chromium its subresource requests; every refusal forwards to the ES ring.
            Thread.Sleep(5000);
            log.AppendLine("subresource refusals tallied client-side and forwarded to the Event Server");

            // ---- Management Client phase: open the SAME workspace's Diagnostics tab ----
            mc = ClientSession.Launch(config.AdminClientExe);
            var mcLogin = LoginFlow.WaitForLoginWindow(mc);
            LoginFlow.Login(mc, mcLogin, config);
            LoginFlow.AcceptCertificateTrust(mc);
            mcMain = LoginFlow.WaitForAdminMain(mc);
            ManagementClientUi.CloseStrayLoginDialogs(mc);
            ManagementClientUi.WaitForTreeReady(mcMain);

            ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
            ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
            ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
            ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);

            AllowlistGridUi.OpenProxyTab(mcMain);
            var originalLines = AllowlistGridUi.ReadEntries(mcMain).ToList();
            log.AppendLine($"original allowlist: '{string.Join(" | ", originalLines)}'");

            Assert.True(originalLines.Any(l => string.Equals(WantedOriginOf(l), pageOrigin, StringComparison.OrdinalIgnoreCase)),
                $"the workspace allowlist lacks the page origin '{pageOrigin}' - the SC phase above could not have loaded its page through the proxy");

            // The blocked origin must NOT be covered: any leftover probe line goes first.
            var startLines = WithoutProbeLines(originalLines);
            if (startLines.Count != originalLines.Count)
            {
                log.AppendLine("an earlier probe run's line is still allowlisted - clearing it first");
                AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, startLines, log);
            }

            // 1. The Diagnostics tab renders, and its tab-gated polling is the marker trace.
            SelectTabByAutomationId(mcMain, "tabDiagnostics");
            ManagementClientUi.ExpectControl(mcMain, "dgDiagnosticsBlocked", 15);
            ManagementClientUi.ExpectControl(mcMain, "txtDiagnosticsSummary", 5);
            ManagementClientUi.ExpectControl(mcMain, "btnDiagnosticsRefresh", 5);
            log.AppendLine("diagnostics tab selected: dgDiagnosticsBlocked + summary + refresh rendered");

            var opened = CountStatusMarkers();
            Assert.True(WaitUntil(() => CountStatusMarkers() > opened, 12),
                "no 'Proxy status snapshot/query ... workspace' marker appeared in the Management Client MIP.log after opening the Diagnostics tab - the tab does not drive the status query pair (#84 polling acceptance)");
            log.AppendLine($"polling trace: markers after opening the tab: {CountStatusMarkers() - opened}");

            // 2. The live blocked row: origin + "not allowed" reason, within the cadence.
            var rowText = WaitValue(() => DiagnosticsRowText(mcMain, blockedOrigin), 45);
            Assert.NotNull(rowText);
            log.AppendLine($"diagnostics row: {rowText}");
            Assert.Contains("not allowed", rowText, StringComparison.OrdinalIgnoreCase);
            Artifacts.Window(mcMain, "mc-diagnostics-row");

            // 3. One click: the exact origin lands in the Proxy grid UNSAVED and the pane arms Save.
            var beforePromotion = McLog.CountMatches(PromotionMarker);
            var promoteButton = DiagnosticsPromoteButton(mcMain, blockedOrigin);
            Assert.NotNull(promoteButton);
            try { promoteButton.Patterns.Invoke.Pattern.Invoke(); }
            catch { promoteButton.Click(); }

            var promotionLine = McLog.WaitForNewLine(beforePromotion, 30, PromotionMarker, blockedOrigin);
            Assert.NotNull(promotionLine);
            log.AppendLine($"promotion marker: {promotionLine}");
            Assert.Contains("added to the allowlist grid", promotionLine, StringComparison.Ordinal);

            var annotated = WaitValue(() =>
            {
                var text = DiagnosticsRowText(mcMain, blockedOrigin);
                return text != null && text.Contains("on allowlist", StringComparison.OrdinalIgnoreCase) ? text : null;
            }, 30);
            Assert.NotNull(annotated);
            log.AppendLine($"annotated row: {annotated}");
            Assert.True(WaitUntil(() => DiagnosticsPromoteButton(mcMain, blockedOrigin) == null, 15),
                "the promoted row still offers an Add-to-allowlist button - a second click could stack duplicates");

            // The promotion is a GRID EDIT: the Proxy tab shows the exact row before any save.
            AllowlistGridUi.OpenProxyTab(mcMain);
            var unsavedGrid = AllowlistGridUi.ReadEntries(mcMain);
            Assert.Contains(unsavedGrid, l => string.Equals(WantedOriginOf(l), blockedOrigin, StringComparison.OrdinalIgnoreCase));
            log.AppendLine($"unsaved grid carries the promoted origin: '{string.Join(" | ", unsavedGrid)}'");

            // 4. The page's normal save path + the ordinary readback discipline: audit line names
            //    the promoted origin, and a RELOADED pane re-renders it. This is "a config write
            //    equivalent to the allowlist editor's save, surviving a workspace re-read".
            var savedLines = WithoutProbeLines(originalLines);
            savedLines.Add(blockedOrigin);
            AllowlistGridUi.SaveAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, savedLines, log);

            // 5. After the reload the Diagnostics tab re-annotates from the SAVED grid - the
            //    promotion is visible to the next admin who opens the tab, from stored config.
            SelectTabByAutomationId(mcMain, "tabDiagnostics");
            ManagementClientUi.ExpectControl(mcMain, "dgDiagnosticsBlocked", 15);
            var reread = WaitValue(() =>
            {
                var text = DiagnosticsRowText(mcMain, blockedOrigin);
                return text != null && text.Contains("on allowlist", StringComparison.OrdinalIgnoreCase) ? text : null;
            }, 45);
            Assert.NotNull(reread);
            log.AppendLine($"re-read annotation: {reread}");
            Artifacts.Window(mcMain, "mc-diagnostics-annotated");

            // 6. Restore the workspace's original allowlist verbatim (minus probe lines).
            var restoreLines = WithoutProbeLines(originalLines);
            AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, restoreLines, log);
            log.AppendLine("allowlist restored (probe line removed, verified from the audit line AND a reloaded pane)");
        }
        finally
        {
            Artifacts.WriteText("mc-diagnostics-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain?.Close(); } catch { }
            if (mc != null) ManagementClientUi.AcceptPendingModal(mc, 5);
            ProcessKiller.KillClients("Client", "VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            sc?.Dispose();
            mc?.Dispose();
            server?.Dispose();
        }
    }

    /// <summary>Any earlier probe run's blocked-origin line never stacks into the starting or
    /// restored allowlist (the #73 probe's discipline).</summary>
    private static List<string> WithoutProbeLines(List<string> lines) =>
        lines
            .Where(l => !string.Equals(WantedOriginOf(l), $"http://{BindHost}:{BlockedOriginPort}", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string WantedOriginOf(string entry) => AllowlistGridUi.WantedOrigin(entry);

    // ================= the Diagnostics surface =================

    /// <summary>The flattened text of the blocked-origin row (origin | reason | hits | seen |
    /// status | button), or null while no row carries the origin.</summary>
    private static string DiagnosticsRowText(Window mcMain, string origin)
    {
        var row = DiagnosticsRow(mcMain, origin);
        if (row == null) return null;
        var texts = row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                       .Select(t => t.Name)
                       .Where(n => !string.IsNullOrWhiteSpace(n));
        var buttons = row.FindAllDescendants(cf => cf.ByName("Add to allowlist"))
                         .Select(b => b.Name);
        return string.Join(" | ", texts.Concat(buttons));
    }

    private static AutomationElement DiagnosticsRow(Window mcMain, string origin)
    {
        var grid = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("dgDiagnosticsBlocked"));
        if (grid == null) return null;
        foreach (var row in grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)))
        {
            var text = string.Join(" ", row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                           .Select(t => t.Name)
                                           .Where(n => !string.IsNullOrWhiteSpace(n)));
            if (text.IndexOf(origin, StringComparison.OrdinalIgnoreCase) >= 0) return row;
        }

        return null;
    }

    /// <summary>The row's promotion button (AutomationId pinned by the #84 guard tests), or null
    /// when the row carries none (an allowlisted row must hide it, not disable it).</summary>
    private static AutomationElement DiagnosticsPromoteButton(Window mcMain, string origin)
    {
        var row = DiagnosticsRow(mcMain, origin);
        return row?.FindFirstDescendant(cf => cf.ByAutomationId("diagnosticsRowPromote"));
    }

    private static void SelectTabByAutomationId(Window mcMain, string automationId)
    {
        var tab = ManagementClientUi.ExpectControl(mcMain, automationId, 15);
        try
        {
            if (tab.Patterns.SelectionItem.IsSupported)
            {
                tab.Patterns.SelectionItem.Pattern.Select();
                Thread.Sleep(1200);
                return;
            }
        }
        catch { }

        tab.Click();
        Thread.Sleep(1200);
    }

    // ================= ES-ring / polling trace =================

    /// <summary>Counts the #76 pull markers in the MC's MIP.log (plus archives): every poll of
    /// the ONE status query pair leaves exactly one line, whether Status or Diagnostics armed
    /// the timer.</summary>
    private static int CountStatusMarkers() =>
        McLog.CountMatches(SnapshotMarker) + McLog.CountMatches(QueryFailMarker);

    // ================= waiting helpers (the status probe's discipline) =================

    private static bool WaitUntil(Func<bool> condition, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (condition()) return true; } catch { }
            Thread.Sleep(1000);
        }

        return false;
    }

    private static T WaitValue<T>(Func<T> probe, int timeoutSeconds) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var value = probe();
                if (value != null) return value;
            }
            catch { /* UIA mid-render: poll again */ }

            Thread.Sleep(1000);
        }

        return null;
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
}
