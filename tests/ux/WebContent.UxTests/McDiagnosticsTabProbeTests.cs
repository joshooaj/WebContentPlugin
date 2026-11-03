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
/// Ordering is load-bearing: the allowlist cleanup (strip any earlier run's probe line) happens
/// BEFORE the Smart Client page load - live on the second MC-16 run, a leftover probe line made
/// the SC phase FORWARD the subresources (Event Server fetched them and timed out) instead of the
/// listener refusing them, and the ring row arrived as "target timed out", not "not allowed".
/// The probe mutates the workspace allowlist briefly and restores it verbatim (the #73 probe's
/// WithoutProbeLines discipline), so it leaves no residue; teardown answers the shell's
/// "Save changes?" prompt with NO (RejectPendingModal) - live on the first MC-16 run, an
/// AcceptPendingModal at close silently persisted the mid-run promotion into the stored item.
/// Run alone.
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

    /// <summary>The shared blocked-row reason label for a NotAllowed refusal - the reason fragment
    /// that identifies OUR row inside a ring that can hold older entries for the same origin
    /// (a stale "target timed out" block from a mis-ordered run must not satisfy the assertions).</summary>
    private const string NotAllowedReason = "not allowed";

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

            // ---- Management Client phase 1: clean the allowlist BEFORE the SC page load. ----
            // The cleanup must precede the Smart Client session: with an earlier run's probe line
            // still stored, the listener would FORWARD the subresources instead of refusing them,
            // and the ring row would arrive as the Event Server's own fetch failure, not as a
            // client-side "not allowed" block.
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
                $"the workspace allowlist lacks the page origin '{pageOrigin}' - the SC phase below could not load its page through the proxy");

            // The blocked origin must NOT be covered: any leftover probe line goes first.
            var startLines = WithoutProbeLines(originalLines);
            if (startLines.Count != originalLines.Count)
            {
                log.AppendLine("an earlier probe run's line is still allowlisted - clearing it BEFORE the Smart Client phase");
                AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, startLines, log);
            }

            // ---- Smart Client phase: put a fresh NOT-ALLOWED blocked origin into the ES ring ----
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

            // ---- Management Client phase 2: the SAME workspace's Diagnostics tab. ----
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

            // 2. The live blocked row: origin + "not allowed" reason, within the cadence. The
            //    reason fragment identifies THIS run's row inside a ring that can still carry
            //    older same-origin entries.
            var rowText = WaitValue(() => DiagnosticsRowText(mcMain, blockedOrigin, NotAllowedReason), 45);
            Assert.NotNull(rowText);
            log.AppendLine($"diagnostics row: {rowText}");
            Assert.Contains("not allowed", rowText, StringComparison.OrdinalIgnoreCase);
            Artifacts.Window(mcMain, "mc-diagnostics-row");

            // 3. One click: the exact origin lands in the Proxy grid UNSAVED and the pane arms Save.
            var beforePromotion = McLog.CountMatches(PromotionMarker);
            var promoteButton = DiagnosticsVisiblePromoteButton(mcMain, blockedOrigin, NotAllowedReason);
            Assert.NotNull(promoteButton);
            try { promoteButton.Patterns.Invoke.Pattern.Invoke(); }
            catch { promoteButton.Click(); }

            var promotionLine = McLog.WaitForNewLine(beforePromotion, 30, PromotionMarker, blockedOrigin);
            Assert.NotNull(promotionLine);
            log.AppendLine($"promotion marker: {promotionLine}");
            Assert.Contains("added to the allowlist grid", promotionLine, StringComparison.Ordinal);

            var annotated = WaitValue(() =>
            {
                var text = DiagnosticsRowText(mcMain, blockedOrigin, NotAllowedReason);
                return text != null && text.Contains("on allowlist", StringComparison.OrdinalIgnoreCase) ? text : null;
            }, 30);
            Assert.NotNull(annotated);
            log.AppendLine($"annotated row: {annotated}");
            log.AppendLine(DumpDiagnosticsGrid(mcMain, "after promotion"));
            Artifacts.Window(mcMain, "mc-diagnostics-after-promote");
            Assert.True(WaitUntil(() => DiagnosticsVisiblePromoteButton(mcMain, blockedOrigin, NotAllowedReason) == null, 15),
                "the promoted row still offers a VISIBLE Add-to-allowlist button - a second click could stack duplicates");

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
                var text = DiagnosticsRowText(mcMain, blockedOrigin, NotAllowedReason);
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
            // NO at the "Save changes?" prompt: a run that died mid-promotion must never persist
            // its unsaved pane (the first MC-16 run's AcceptPendingModal at close did exactly
            // that, and the residue poisoned the next run's Smart Client phase).
            if (mc != null) ManagementClientUi.RejectPendingModal(mc, 5);
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
    //
    // Row addressing is STRUCTURAL, never a descendant-name join. Live on the MC-16 runs,
    // FindAllDescendants over a DataGrid row surfaced SIBLING rows' promote buttons (UIA tree
    // containment lies mid-virtualization), so "the promoted row's button is gone" cannot be
    // decided by a row-scoped Name/AutomationId search. The authority is the UIA Grid pattern -
    // IGridProvider.GetItem(row, column) is the structured table API, and a Collapsed WPF button
    // has no peer in its template cell at all, which makes "button gone" an honest absence.
    // The geometry fallback (button centre inside the matched row's rect) covers a host that
    // refuses the Grid pattern; the raw descendant join is the last resort, trusted only for the
    // LOG line, never for an assertion.

    /// <summary>The grid's column order as declared in WorkspaceConfigPage.xaml (#84): Origin,
    /// Reason, Hits, Last seen, Status, then the promote-button template column.</summary>
    private const int OriginColumn = 0;
    private const int ReasonColumn = 1;
    private const int LastColumn = 4;
    private const int PromoteColumn = 5;

    /// <summary>The flattened text of the blocked-origin row (origin | reason | hits | seen |
    /// status | button), or null while no row carries the origin. The optional reason fragment
    /// pins THIS refusal category when the ring also holds older same-origin entries.</summary>
    private static string DiagnosticsRowText(Window mcMain, string origin, string reasonFragment = null)
    {
        var grid = DiagnosticsGrid(mcMain);
        if (grid == null) return null;

        var cells = GridRowCells(grid, origin, reasonFragment);
        if (cells != null)
        {
            var button = IfVisible(GridPromoteButton(grid, origin, reasonFragment));
            return string.Join(" | ", cells.Concat(button == null ? new string[0] : new[] { button.Name ?? "Add to allowlist" }));
        }

        // Fallback shape (log only): the raw descendant join, known to bleed sibling buttons.
        var row = DiagnosticsRowElement(grid, origin, reasonFragment);
        if (row == null) return null;
        var texts = row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                       .Select(t => t.Name)
                       .Where(n => !string.IsNullOrWhiteSpace(n));
        return string.Join(" | ", texts);
    }

    private static AutomationElement DiagnosticsGrid(Window mcMain) =>
        mcMain.FindFirstDescendant(cf => cf.ByAutomationId("dgDiagnosticsBlocked"));

    /// <summary>The row CONTAINER carrying the origin (only for the log fallback and the
    /// geometry-fallback button scan - Grid-pattern items are the addressing authority).</summary>
    private static AutomationElement DiagnosticsRowElement(AutomationElement grid, string origin, string reasonFragment = null)
    {
        foreach (var row in grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)))
        {
            var text = string.Join(" ", row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                           .Select(t => t.Name)
                                           .Where(n => !string.IsNullOrWhiteSpace(n)));
            if (text.IndexOf(origin, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (reasonFragment != null && text.IndexOf(reasonFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
            return row;
        }

        return null;
    }

    /// <summary>The row's promotion button (AutomationId pinned by the #84 guard tests), or null
    /// when the row carries none (an allowlisted row must HIDE it, not disable it). Grid pattern
    /// first; the descendant scan only counts a button whose centre sits inside the matched
    /// row's own rectangle - the containment lesson.</summary>
    private static AutomationElement DiagnosticsPromoteButton(Window mcMain, string origin, string reasonFragment = null)
    {
        var grid = DiagnosticsGrid(mcMain);
        if (grid == null) return null;

        var rowIndex = GridRowIndex(grid, origin, reasonFragment);
        if (rowIndex >= 0)
        {
            return GridPromoteButton(grid, origin, reasonFragment, rowIndex);
        }

        var row = DiagnosticsRowElement(grid, origin, reasonFragment);
        if (row == null) return null;

        var rowRect = SafeRect(row);
        foreach (var button in row.FindAllDescendants(cf => cf.ByAutomationId("diagnosticsRowPromote")))
        {
            var rect = SafeRect(button);
            if (rowRect.IsEmpty || rect.IsEmpty) return button;
            var center = new System.Drawing.Point((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            if (rowRect.Contains(center)) return button;
        }

        return null;
    }

    /// <summary>A promote button counts as OFFERED only when the admin can actually see it: the
    /// page hides a covered row's button with Visibility=Collapsed, and UIA can still hand back a
    /// peer for a cell whose content never made it on screen (zero rect / IsOffscreen) - an
    /// off-screen peer is not a second click waiting to happen, so it is not an affordance.</summary>
    private static AutomationElement DiagnosticsVisiblePromoteButton(Window mcMain, string origin, string reasonFragment = null)
        => IfVisible(DiagnosticsPromoteButton(mcMain, origin, reasonFragment));

    /// <summary>Filters a promote peer down to one that is really on screen (see the note on
    /// <see cref="DiagnosticsVisiblePromoteButton"/>).</summary>
    private static AutomationElement IfVisible(AutomationElement button)
    {
        if (button == null) return null;

        var rect = SafeRect(button);
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return null;
        try { if (button.IsOffscreen) return null; }
        catch { /* unreadable state: the rect already said it is on screen */ }

        return button;
    }

    /// <summary>What the Diagnostics grid actually holds, cell by cell, when an assertion about a
    /// row is about to fail - the shape of the grid (rows/columns), and every promote peer UIA
    /// can see with its row, rect and off-screen state. Turns a "the button is still there" failure
    /// into a readable statement about which cell carries a visible button.</summary>
    private static string DumpDiagnosticsGrid(Window mcMain, string label)
    {
        var grid = DiagnosticsGrid(mcMain);
        if (grid == null) return $"grid dump [{label}]: no dgDiagnosticsBlocked peer";

        var sb = new StringBuilder($"grid dump [{label}]:");
        int rows = -1;
        int cols = -1;
        try
        {
            if (grid.Patterns.Grid.IsSupported)
            {
                rows = grid.Patterns.Grid.Pattern.RowCount;
                cols = grid.Patterns.Grid.Pattern.ColumnCount;
            }
        }
        catch { }

        sb.AppendLine($"  grid pattern: supported={rows >= 0} rows={rows} cols={cols}");

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var cell = GridItem(grid, r, c);
                if (cell == null)
                {
                    sb.AppendLine($"  [{r},{c}] <no peer>");
                    continue;
                }

                var promote = cell.FindFirstDescendant(cf => cf.ByAutomationId("diagnosticsRowPromote"));
                if (promote == null)
                {
                    sb.AppendLine($"  [{r},{c}] id='{Safe(c == PromoteColumn ? cell.AutomationId : null)}' text='{CellText(cell)}'");
                    continue;
                }

                var rect = SafeRect(promote);
                var offscreen = "?";
                try { offscreen = promote.IsOffscreen ? "offscreen" : "on-screen"; }
                catch { }

                sb.AppendLine($"  [{r},{c}] PROMOTE PEER name='{Safe(promote.Name)}' {offscreen} rect={rect}");
            }
        }

        var loose = 0;
        try
        {
            foreach (var button in grid.FindAllDescendants(cf => cf.ByAutomationId("diagnosticsRowPromote")))
            {
                loose++;
                var rect = SafeRect(button);
                var offscreen = "?";
                try { offscreen = button.IsOffscreen ? "offscreen" : "on-screen"; }
                catch { }

                sb.AppendLine($"  descendant promote #{loose}: name='{Safe(button.Name)}' {offscreen} rect={rect}");
            }
        }
        catch { }

        sb.AppendLine($"  descendant promote peers total: {loose}");
        return sb.ToString();
    }

    private static string Safe(string value) => value ?? string.Empty;

    // ---- Grid-pattern plumbing ----

    /// <summary>The Grid-pattern row index whose Origin cell names the origin (and whose Reason
    /// cell, when a fragment is given, carries it), or -1 when the pattern is unsupported or no
    /// row matches. The cell's text is read through its TextBlock peer when the cell peer itself
    /// is nameless.</summary>
    private static int GridRowIndex(AutomationElement grid, string origin, string reasonFragment = null)
    {
        try
        {
            if (!grid.Patterns.Grid.IsSupported) return -1;
            var rows = grid.Patterns.Grid.Pattern.RowCount;
            for (var r = 0; r < rows; r++)
            {
                var originCell = GridItem(grid, r, OriginColumn);
                if (originCell == null) continue;
                if (CellText(originCell).IndexOf(origin, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (reasonFragment != null)
                {
                    var reasonCell = CellText(GridItem(grid, r, ReasonColumn));
                    if (reasonCell.IndexOf(reasonFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }

                return r;
            }
        }
        catch { }

        return -1;
    }

    private static AutomationElement GridItem(AutomationElement grid, int row, int column)
    {
        try { return grid.Patterns.Grid.Pattern.GetItem(row, column); }
        catch { return null; }
    }

    private static AutomationElement GridPromoteButton(AutomationElement grid, string origin, string reasonFragment = null, int? knownRow = null)
    {
        var rowIndex = knownRow ?? GridRowIndex(grid, origin, reasonFragment);
        if (rowIndex < 0) return null;

        var cell = GridItem(grid, rowIndex, PromoteColumn);
        if (cell == null) return null;
        if (string.Equals(cell.AutomationId, "diagnosticsRowPromote", StringComparison.Ordinal)) return cell;
        return cell.FindFirstDescendant(cf => cf.ByAutomationId("diagnosticsRowPromote"));
    }

    /// <summary>The five read-only cell texts of the origin's row, in column order (a Grid-pattern
    /// miss returns null so the caller falls back).</summary>
    private static string[] GridRowCells(AutomationElement grid, string origin, string reasonFragment = null)
    {
        var rowIndex = GridRowIndex(grid, origin, reasonFragment);
        if (rowIndex < 0) return null;

        var texts = new List<string>();
        for (var c = OriginColumn; c <= LastColumn; c++)
        {
            var cell = GridItem(grid, rowIndex, c);
            var text = cell == null ? string.Empty : CellText(cell);
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(text.Trim());
        }

        return texts.Count == 0 ? null : texts.ToArray();
    }

    /// <summary>A cell's text. Empty WPF DataGridCells answer a SYNTHESIZED UIA Name
    /// ("Item: <type>, Column Display Index: N") - live on the MC-16 re-run, the blank Status
    /// cell leaked exactly that string into the row text - so the TextBlock peers decide first,
    /// and a synthesized fallback name is read as empty.</summary>
    private static string CellText(AutomationElement cell)
    {
        if (cell == null) return string.Empty;

        try
        {
            var texts = string.Join(" ", cell.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                             .Select(t => t.Name)
                                             .Where(n => !string.IsNullOrWhiteSpace(n)));
            if (!string.IsNullOrWhiteSpace(texts)) return texts;
        }
        catch { }

        try
        {
            var name = cell.Name ?? string.Empty;
            if (name.StartsWith("Item:", StringComparison.Ordinal) && name.Contains("Column Display Index", StringComparison.Ordinal))
                return string.Empty;
            return name;
        }
        catch { return string.Empty; }
    }

    private static System.Drawing.Rectangle SafeRect(AutomationElement element)
    {
        try { return element.BoundingRectangle; }
        catch { return System.Drawing.Rectangle.Empty; }
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
