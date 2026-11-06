using System.Management;
using System.Text.RegularExpressions;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #95 live probes (SC-14): a SECOND Smart Client on the same machine joins the boundary
/// owner's claimed loopback listener instead of colliding on the proxied browser profile.
///
/// The witnesses this ticket demanded:
///  - BOTH clients' proxied views render (the #87 shape was a blank second view with an
///    ownership notice);
///  - exactly ONE browser-process shape: one msedgewebview2 root for the proxied profile,
///    parented to the claim OWNER only — no msedgewebview2 parented to the second client, no
///    --parent-pid naming the second client, and exactly one loopback --proxy-server port in the
///    system (the joiner is a core client of the shared browser process, exactly like the second
///    core of its own normal-profile browser sharing that process);
///  - exactly ONE claim file beside the profile folder, naming the owner pid + listener port;
///  - no profile-ownership notice on either view (that #87 fallback stays only for the
///    browser-held case; the second-Smart-Client case now joins);
///  - the join attempted WHILE the owner's proxied browser is mid-navigation still lands;
///  - the MC Status tab (#69/#76) shows exactly ONE live proxy session with both clients
///    rendering - the joiner creates no second session/forwarder, its traffic shares the owner's.
///
/// Log-line caveat (measured since #87): while one Smart Client lives, the second concurrent one
/// may write NOTHING to the machine-wide MIPLog.txt, so the joiner-side join line is only
/// asserted when this run proves the joiner journalled at all; the rendered second page and the
/// process witnesses above are the authoritative evidence either way.
/// </summary>
public sealed class ScListenerJoinProbeTests
{
    private readonly ITestOutputHelper _output;

    public ScListenerJoinProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>The core shape: second client joins; one claim, one listener, one browser process, both views render.</summary>
    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeSecondClientJoinsOneProxiedBrowserProcess()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-listener-join-probe"));
        var runStarted = DateTime.Now;
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;
        ClientSession owner = null;
        ClientSession joiner = null;

        log.AppendLine("=== SC-14: second Smart Client joins the proxied browser process via the listener claim (#95) ===");
        try
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            Thread.Sleep(2000);

            // ---- Owner client: opens the proxied view, claims the slot, binds the listener, renders. ----
            owner = ClientSession.Launch(config.SmartClientExe);
            var ownerLogin = LoginFlow.WaitForLoginWindow(owner);
            LoginFlow.Login(owner, ownerLogin, config);
            LoginFlow.AcceptCertificateTrust(owner);
            var ownerMain = LoginFlow.WaitForSmartClientMain(owner);
            SmartClientUi.Maximize(ownerMain);
            Thread.Sleep(1500);
            ownerMain = owner.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            ownerMain = SmartClientUi.SelectWorkspace(ownerMain, owner, config.WorkspaceName);
            var ownerItem = SmartClientUi.WaitForMipViewItem(ownerMain);
            log.AppendLine("owner view item open; waiting for the owner's proxied page to render...");

            // CDP is the render witness: ONE browser process answers it, and BOTH clients' pages
            // must show up on that single endpoint.
            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot, 120);
            var homeHost = await WaitForRenderedPageHostAsync(browser, 120);
            log.AppendLine($"owner proxied page rendered (page host '{homeHost}')");

            var ownerPid = owner.Process.Id;

            // ---- Claim witness: exactly one proxied-<guid>.claim naming the owner pid + a live port. ----
            string claimPath = null;
            int claimPort = 0;
            int claimOwnerPid = 0;
            var claimDeadline = DateTime.Now.AddSeconds(60);
            while (claimPath == null && DateTime.Now < claimDeadline)
            {
                var files = Directory.EnumerateFiles(config.WebView2DataRoot, "proxied-*.claim", SearchOption.AllDirectories).ToList();
                if (files.Count > 0)
                {
                    claimPath = files[0];
                    foreach (var line in File.ReadAllLines(claimPath))
                    {
                        var split = line.IndexOf('=');
                        if (split <= 0) continue;
                        var key = line.Substring(0, split).Trim();
                        var value = line.Substring(split + 1).Trim();
                        if (key == "port" && int.TryParse(value, out var port)) claimPort = port;
                        if (key == "ownerPid" && int.TryParse(value, out var pid)) claimOwnerPid = pid;
                    }
                }
                else Thread.Sleep(2000);
            }

            var problems = new List<string>();
            if (claimPath == null)
            {
                problems.Add("no listener claim file (proxied-*.claim) ever appeared under the plugin data root - the owner never claimed the boundary slot");
            }
            else
            {
                log.AppendLine($"claim file: {claimPath} -> ownerPid={claimOwnerPid}, port={claimPort}");
                if (claimOwnerPid != ownerPid)
                {
                    problems.Add($"the claim names owner pid {claimOwnerPid}, but the live owner client is pid {ownerPid}");
                }

                if (claimPort <= 0)
                {
                    problems.Add("the claim names no usable listener port");
                }

                var siblings = Directory.EnumerateFiles(config.WebView2DataRoot, "proxied-*.claim", SearchOption.AllDirectories).ToList();
                if (siblings.Count != 1)
                {
                    problems.Add($"{siblings.Count} claim files exist, expected exactly one: {string.Join("; ", siblings)}");
                }
            }

            // ---- Second client: same machine, same workspace, the SAME proxied boundary profile. ----
            joiner = ClientSession.Launch(config.SmartClientExe);
            var joinerLogin = LoginFlow.WaitForLoginWindow(joiner);
            LoginFlow.Login(joiner, joinerLogin, config);
            LoginFlow.AcceptCertificateTrust(joiner);
            var joinerMain = LoginFlow.WaitForSmartClientMain(joiner);
            SmartClientUi.Maximize(joinerMain);
            Thread.Sleep(1500);
            joinerMain = joiner.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            if (!SmartClientUi.TrySelectWorkspaceWithoutInput(joinerMain, joiner, config.WorkspaceName))
            {
                joinerMain = SmartClientUi.SelectWorkspace(joinerMain, joiner, config.WorkspaceName);
            }

            var joinerItem = SmartClientUi.WaitForMipViewItem(joinerMain);
            log.AppendLine("second client's view item open; waiting for the SECOND proxied page on the shared browser process...");

            // Before #95 the second client's browser never came up at all - exactly one page
            // existed forever. Two page targets on the ONE CDP endpoint is the join witness.
            var joinDeadline = DateTime.Now.AddSeconds(150);
            int pages = 0;
            while (DateTime.Now < joinDeadline)
            {
                pages = CountPagesOnHost(browser, homeHost);
                if (pages >= 2) break;
                Thread.Sleep(2000);
            }

            log.AppendLine($"proxied pages on the browser process: {pages}");
            if (pages < 2)
            {
                Artifacts.Window(joinerMain, "sc-join-second-page-missing");
                throw new InvalidOperationException("the second client's proxied view never rendered - the join contract did not take effect (pages: " + pages + ")");
            }

            Artifacts.Window(ownerMain, "sc-join-owner-view");
            Artifacts.Window(joinerMain, "sc-join-joiner-view");

            // ---- No ownership notice anywhere (this contract replaces the #87 notice for SC+SC). ----
            if (FindDescendant(joinerItem, "profileOwnershipHeadline") != null)
            {
                problems.Add("the second client's view shows the #87 profile-ownership notice - it was refused instead of joining");
            }

            var ownerItem2 = SmartClientUi.FindMipViewItem(ownerMain) ?? ownerItem;
            if (FindDescendant(ownerItem2, "profileOwnershipHeadline") != null)
            {
                problems.Add("the owner's view shows a profile-ownership notice - the contract leaked onto the healthy session");
            }

            // ---- Process witnesses (WMI): ONE proxied browser-process shape. ----
            var joinerPid = joiner.Process.Id;
            var (total, parentedByJoiner, parentedByOwner, joinerParentIdReferences, proxyPorts) = WitnessBrowserProcessShape(ownerPid, joinerPid);
            log.AppendLine($"msedgewebview2 processes under the data root: {total}; parented to the joiner: {parentedByJoiner}; parented to the owner: {parentedByOwner}; --parent-pid naming the joiner: {joinerParentIdReferences}; distinct loopback --proxy-server ports: [{string.Join(", ", proxyPorts)}]");
            if (parentedByJoiner != 0)
            {
                problems.Add($"{parentedByJoiner} msedgewebview2 process(es) are parented to the SECOND client - it launched its own browser process instead of joining the shared one");
            }

            if (joinerParentIdReferences != 0)
            {
                problems.Add($"{joinerParentIdReferences} msedgewebview2 process(es) carry --parent-pid={joinerPid} - the second client launched browser helper processes");
            }

            if (parentedByOwner != 1)
            {
                problems.Add($"{parentedByOwner} msedgewebview2 processes are parented to the owner (expected exactly its one proxied browser root)");
            }

            if (proxyPorts.Count != 1)
            {
                problems.Add($"{proxyPorts.Count} distinct loopback --proxy-server ports exist ({string.Join(", ", proxyPorts)}), expected exactly the owner's one listener");
            }
            else if (claimPort > 0 && proxyPorts[0] != claimPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                problems.Add($"the browser's --proxy-server port {proxyPorts[0]} is not the claimed listener port {claimPort}");
            }

            // ---- MIP log (joiner lines conditionally enforced; measured caveat since #87). ----
            var claimOwnerLines = FindLogLinesSince("loopback listener on 127.0.0.1:", runStarted)
                .Where(l => l.Contains("(claim owner)", StringComparison.Ordinal)).ToList();
            var joinLines = FindLogLinesSince("joined the boundary owner's loopback listener", runStarted);
            var pluginLoads = CountLogLinesSince("Web Content, loaded", runStarted);
            var joinerLogged = pluginLoads >= 2;
            log.AppendLine($"'Web Content' plugin load lines since run start: {pluginLoads} (second client demonstrably logged: {joinerLogged}); owner claim lines: {claimOwnerLines.Count}, join lines: {joinLines.Count}");
            if (claimOwnerLines.Count < 1)
            {
                problems.Add("the owner's 'loopback listener on 127.0.0.1:<port> (claim owner)' creation line is missing from the MIP log");
            }

            if (joinerLogged && joinLines.Count < 1)
            {
                problems.Add("the second client demonstrably journalled to MIPLog.txt, but its 'joined the boundary owner's loopback listener' line is missing");
            }
            else if (!joinerLogged)
            {
                log.AppendLine("NOTE (measured environment fact since #87): while one Smart Client lives, the second concurrent one may write NOTHING to the machine-wide MIPLog.txt - its join line is unenforceable in that shape, and the rendered second page + the process witnesses above are the authoritative evidence.");
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(problems.Count + " join-contract problem(s): " + string.Join("; ", problems));
            }

            log.AppendLine("VERIFIED: both Smart Clients render the proxied view; exactly one claim file (owner pid + port) and one loopback listener port; one msedgewebview2 process shape (root parented to the owner, nothing parented to the second client); no ownership notice on either view.");
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            log.AppendLine("probe failed: " + failure);
            log.AppendLine(ex.ToString());
        }
        finally
        {
            Artifacts.WriteText("sc-listener-join-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { joiner?.Dispose(); } catch { }
            try { owner?.Dispose(); } catch { }
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    /// <summary>The brief's timing row: the join runs while the owner's proxied browser is mid-navigation.</summary>
    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeJoinWhileOwnerIsNavigating()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-listener-join-navigating-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;
        ClientSession owner = null;
        ClientSession joiner = null;

        log.AppendLine("=== SC-14b: join attempted while the owner's proxied browser is mid-navigation ===");
        try
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            Thread.Sleep(2000);

            owner = ClientSession.Launch(config.SmartClientExe);
            var ownerLogin = LoginFlow.WaitForLoginWindow(owner);
            LoginFlow.Login(owner, ownerLogin, config);
            LoginFlow.AcceptCertificateTrust(owner);
            var ownerMain = LoginFlow.WaitForSmartClientMain(owner);
            SmartClientUi.Maximize(ownerMain);
            Thread.Sleep(1500);
            ownerMain = owner.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            ownerMain = SmartClientUi.SelectWorkspace(ownerMain, owner, config.WorkspaceName);
            var ownerItem = SmartClientUi.WaitForMipViewItem(ownerMain);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot, 120);
            var homeHost = await WaitForRenderedPageHostAsync(browser, 120);
            log.AppendLine($"owner proxied page rendered (page host '{homeHost}')");

            joiner = ClientSession.Launch(config.SmartClientExe);
            var joinerLogin = LoginFlow.WaitForLoginWindow(joiner);
            LoginFlow.Login(joiner, joinerLogin, config);
            LoginFlow.AcceptCertificateTrust(joiner);
            var joinerMain = LoginFlow.WaitForSmartClientMain(joiner);
            SmartClientUi.Maximize(joinerMain);
            Thread.Sleep(1500);
            joinerMain = joiner.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            if (!SmartClientUi.TrySelectWorkspaceWithoutInput(joinerMain, joiner, config.WorkspaceName))
            {
                joinerMain = SmartClientUi.SelectWorkspace(joinerMain, joiner, config.WorkspaceName);
            }

            var joinerItem = SmartClientUi.WaitForMipViewItem(joinerMain);
            log.AppendLine("second client's view item open (its proxied browser creation runs NOW) - re-navigating the OWNER mid-join...");

            // The overlap window: the second client's CoreWebView2 creation (consult -> join) runs
            // while the owner's browser is busy re-navigating its proxied page. A consult/claim
            // race would break the owner's rendering or refuse the joiner.
            var ownerItemNow = SmartClientUi.FindMipViewItem(ownerMain) ?? ownerItem;
            var ownerRefresh = FindDescendant(ownerItemNow, "NavReload");
            var navigated = false;
            if (ownerRefresh != null)
            {
                try { ownerRefresh.Patterns.Invoke.Pattern.Invoke(); navigated = true; }
                catch { ownerRefresh.Click(); navigated = true; }

                log.AppendLine("owner view re-navigated (NavReload) mid-join");
            }
            else
            {
                log.AppendLine("NOTE: the owner view's NavReload button was not reachable via UIA in this run - the mid-navigation overlap was not exercised.");
            }

            var joinDeadline = DateTime.Now.AddSeconds(150);
            int pages = 0;
            while (DateTime.Now < joinDeadline)
            {
                pages = CountPagesOnHost(browser, homeHost);
                if (pages >= 2) break;
                Thread.Sleep(2000);
            }

            var problems = new List<string>();
            if (pages < 2)
            {
                Artifacts.Window(joinerMain, "sc-join-navigating-second-page-missing");
                problems.Add("the second client's proxied view never rendered while the owner navigated (pages: " + pages + ")");
            }

            if (FindDescendant(joinerItem, "profileOwnershipHeadline") != null)
            {
                problems.Add("the second client's view shows the profile-ownership notice - the mid-navigation join was refused");
            }

            var ownerItemAfter = SmartClientUi.FindMipViewItem(ownerMain) ?? ownerItem;
            if (FindDescendant(ownerItemAfter, "profileOwnershipHeadline") != null)
            {
                problems.Add("the owner's view shows an ownership notice - the mid-navigation join broke the healthy session");
            }

            if (owner.Process.HasExited)
            {
                problems.Add("the OWNER Smart Client died during the mid-navigation join");
            }

            if (joiner.Process.HasExited)
            {
                problems.Add("the joining Smart Client died during the mid-navigation join");
            }

            Artifacts.Window(joinerMain, "sc-join-navigating-joiner-view");
            Artifacts.Window(ownerMain, "sc-join-navigating-owner-view");

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(problems.Count + " mid-navigation join problem(s): " + string.Join("; ", problems));
            }

            log.AppendLine($"VERIFIED: the join landed while the owner's browser was mid-navigation{(!navigated ? " (navigation overlap unexercised - see NOTE)" : string.Empty)} - both proxied views render, no ownership notice anywhere, both clients alive.");
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            log.AppendLine("probe failed: " + failure);
            log.AppendLine(ex.ToString());
        }
        finally
        {
            Artifacts.WriteText("sc-listener-join-navigating-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { joiner?.Dispose(); } catch { }
            try { owner?.Dispose(); } catch { }
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    /// <summary>
    /// The #69 status row under #95: with BOTH clients rendering proxied views, the MC Status tab
    /// shows exactly ONE live proxy session - the joiner opens no second session/forwarder, its
    /// traffic shares the owner's session.
    /// </summary>
    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeStatusTabShowsSingleSharedSession()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-listener-join-status-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;
        ClientSession owner = null;
        ClientSession joiner = null;
        ClientSession mc = null;
        Window mcMain = null;

        log.AppendLine("=== SC-14c: MC Status tab shows ONE live proxy session with two proxied Smart Clients ===");
        try
        {
            ProcessKiller.KillClients("Client", "VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            Thread.Sleep(2000);

            // ---- MC FIRST: the Status tab must prove a ZERO-session baseline before this row's
            // clients launch. Rows that kill Smart Clients leak their ES sessions until the
            // 3-minute idle backstop (30 s sweep) reclaims them, and a dead session is
            // indistinguishable from a live second one in this grid. ----
            mc = ClientSession.Launch(config.AdminClientExe);
            var mcLogin = LoginFlow.WaitForLoginWindow(mc);
            LoginFlow.Login(mc, mcLogin, config);
            mcMain = LoginFlow.WaitForAdminMain(mc);
            ManagementClientUi.CloseStrayLoginDialogs(mc);
            ManagementClientUi.WaitForTreeReady(mcMain);

            ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
            ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
            ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
            ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);

            var tab = ManagementClientUi.ExpectControl(mcMain, "tabStatus", 15);
            try { tab.Patterns.SelectionItem.Pattern.Select(); } catch { tab.Click(); }
            ManagementClientUi.ExpectControl(mcMain, "dgStatusSessions", 15);
            ManagementClientUi.ExpectControl(mcMain, "txtStatusSummary", 5);
            Thread.Sleep(3000);

            var baselineDeadline = DateTime.Now.AddSeconds(300);
            var baselineSummary = string.Empty;
            while (DateTime.Now < baselineDeadline)
            {
                try
                {
                    var summary = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("txtStatusSummary"));
                    baselineSummary = summary?.Name ?? string.Empty;
                }
                catch { }

                if (baselineSummary.Contains("No live proxy sessions", StringComparison.Ordinal)) break;
                Thread.Sleep(5000);
            }

            if (!baselineSummary.Contains("No live proxy sessions", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("the Status tab baseline never drained to zero live sessions within 300 s (last: '" + baselineSummary + "') - leaked sessions make the single-shared-session witness unmeasurable; retry after the Event Server's idle backstop");
            }

            log.AppendLine($"status baseline proven zero: '{baselineSummary}'");
            log.AppendLine("launching both proxied Smart Clients...");

            owner = ClientSession.Launch(config.SmartClientExe);
            var ownerLogin = LoginFlow.WaitForLoginWindow(owner);
            LoginFlow.Login(owner, ownerLogin, config);
            LoginFlow.AcceptCertificateTrust(owner);
            var ownerMain = LoginFlow.WaitForSmartClientMain(owner);
            SmartClientUi.Maximize(ownerMain);
            Thread.Sleep(1500);
            ownerMain = owner.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            ownerMain = SmartClientUi.SelectWorkspace(ownerMain, owner, config.WorkspaceName);
            SmartClientUi.WaitForMipViewItem(ownerMain);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot, 120);
            var homeHost = await WaitForRenderedPageHostAsync(browser, 120);
            log.AppendLine($"owner proxied page rendered (page host '{homeHost}')");

            joiner = ClientSession.Launch(config.SmartClientExe);
            var joinerLogin = LoginFlow.WaitForLoginWindow(joiner);
            LoginFlow.Login(joiner, joinerLogin, config);
            LoginFlow.AcceptCertificateTrust(joiner);
            var joinerMain = LoginFlow.WaitForSmartClientMain(joiner);
            SmartClientUi.Maximize(joinerMain);
            Thread.Sleep(1500);
            joinerMain = joiner.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            if (!SmartClientUi.TrySelectWorkspaceWithoutInput(joinerMain, joiner, config.WorkspaceName))
            {
                joinerMain = SmartClientUi.SelectWorkspace(joinerMain, joiner, config.WorkspaceName);
            }

            SmartClientUi.WaitForMipViewItem(joinerMain);

            var joinDeadline = DateTime.Now.AddSeconds(150);
            int pages = 0;
            while (DateTime.Now < joinDeadline)
            {
                pages = CountPagesOnHost(browser, homeHost);
                if (pages >= 2) break;
                Thread.Sleep(2000);
            }

            if (pages < 2)
            {
                throw new InvalidOperationException("the second client's proxied view never rendered - this row needs BOTH proxied views up (pages: " + pages + ")");
            }

            log.AppendLine("both proxied views render; watching the status summary...");

            // The status pull cadence is ~3 s plus heartbeats; from the proven-zero baseline the
            // ONLY session that can appear is the owner's one. Regex the count so "1 live proxy
            // session(s)" is never substring-satisfied by "11 ..." and the two-session shape
            // (the naive non-shared fix) fails fast.
            var deadline = DateTime.Now.AddSeconds(180);
            var lastCount = 0;
            var lastSummary = baselineSummary;
            while (DateTime.Now < deadline)
            {
                try
                {
                    var summary = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("txtStatusSummary"));
                    lastSummary = summary?.Name ?? string.Empty;
                }
                catch { }

                var match = Regex.Match(lastSummary, @"(\d+)\s+live proxy session");
                lastCount = match.Success ? int.Parse(match.Groups[1].Value) : 0;
                if (lastCount >= 1) break;
                Thread.Sleep(5000);
            }

            log.AppendLine($"status summary: '{lastSummary}' (session count: {lastCount})");
            Artifacts.Window(mcMain, "sc-join-status-single-session");

            if (lastCount >= 2)
            {
                throw new InvalidOperationException($"the Status tab shows {lastCount} live proxy sessions with two proxied clients - the second client registered its own #69 session instead of sharing the owner's");
            }

            if (lastCount != 1)
            {
                throw new InvalidOperationException("the Status tab never showed exactly '1 live proxy session(s)' with both proxied clients up; last summary: '" + lastSummary + "'");
            }

            var grid = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("dgStatusSessions"));
            if (grid != null)
            {
                var rows = grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length;
                log.AppendLine($"status session grid rows: {rows}");
                if (rows > 1)
                {
                    throw new InvalidOperationException($"the status session grid lists {rows} sessions for one proxied profile - expected the owner's one");
                }
            }

            log.AppendLine("VERIFIED: from a proven-zero baseline, with both Smart Clients rendering the proxied view, the MC Status tab shows exactly ONE live proxy session - the joiner shares the owner's session and traffic.");
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            log.AppendLine("probe failed: " + failure);
            log.AppendLine(ex.ToString());
        }
        finally
        {
            Artifacts.WriteText("sc-listener-join-status-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain?.Close(); } catch { }
            if (mc != null) ManagementClientUi.AcceptPendingModal(mc, 5);
            try { joiner?.Dispose(); } catch { }
            try { owner?.Dispose(); } catch { }
            ProcessKiller.KillClients("Client", "VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    /// <summary>
    /// WMI witness of the proxied browser-process shape under the plugin data root: total
    /// msedgewebview2 processes, how many are parented to each client, how many carry
    /// --parent-pid naming the second client, and the distinct loopback --proxy-server ports.
    /// </summary>
    private static (int Total, int ParentedByJoiner, int ParentedByOwner, int JoinerParentIdReferences, List<string> ProxyPorts)
        WitnessBrowserProcessShape(int ownerPid, int joinerPid)
    {
        var total = 0;
        var parentedByJoiner = 0;
        var parentedByOwner = 0;
        var joinerParentIdReferences = 0;
        var ports = new List<string>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'");
        foreach (ManagementObject mo in searcher.Get())
        {
            var command = (mo["CommandLine"] as string) ?? string.Empty;

            // Scope to the plugin's own browser processes (same marker ProcessKiller uses) so
            // unrelated WebView2 apps on the workstation never count.
            if (!command.Contains("SmartClient.WebContentData", StringComparison.OrdinalIgnoreCase)) continue;
            total++;

            var parent = Convert.ToInt32(mo["ParentProcessId"]);
            if (parent == joinerPid) parentedByJoiner++;
            if (parent == ownerPid) parentedByOwner++;
            if (command.Contains("--parent-pid=" + joinerPid.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)) joinerParentIdReferences++;

            var portMatch = Regex.Match(command, @"--proxy-server=\S*?127\.0\.0\.1:(\d+)");
            if (portMatch.Success) ports.Add(portMatch.Groups[1].Value);
        }

        return (total, parentedByJoiner, parentedByOwner, joinerParentIdReferences, ports.Distinct().ToList());
    }

    /// <summary>
    /// Waits for the owner's proxied page to actually render on the browser process and returns
    /// its URL host - the fragment both clients' pages must then share. Deliberately NOT
    /// config.HomeAddressFragment: the lab workspace's home address is live config (the uxconfig
    /// fragment goes stale), while the rendered page is the ground truth.
    /// </summary>
    private static async Task<string> WaitForRenderedPageHostAsync(Microsoft.Playwright.IBrowser browser, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string seen = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var page = browser.Contexts.SelectMany(c => c.Pages)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p.Url) &&
                    (p.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
            if (page != null)
            {
                if (Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) return uri.Host;
                seen = page.Url;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException("no rendered (http/https) CDP page appeared on the proxied browser process. Last seen: " + seen);
    }

    private static int CountPagesOnHost(Microsoft.Playwright.IBrowser browser, string host)
    {
        return browser.Contexts.SelectMany(c => c.Pages)
            .Count(p => !string.IsNullOrEmpty(p.Url) && p.Url.Contains(host, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> FindLogLinesSince(string needle, DateTime since)
    {
        return ReadScLogLines().Where(l => l.Contains(needle, StringComparison.Ordinal) && LineStamp(l) >= since).Take(40).ToList();
    }

    private static int CountLogLinesSince(string needle, DateTime since)
    {
        return ReadScLogLines().Count(l => l.Contains(needle, StringComparison.Ordinal) && LineStamp(l) >= since);
    }

    /// <summary>MIP-log lines begin "2026-11-03 23:16:56.261-08:00"; unparseable lines count as old.</summary>
    private static DateTime LineStamp(string line)
    {
        var head = line.Length >= 23 ? line.Substring(0, 23) : line;
        return DateTime.TryParse(head, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var stamp)
            ? stamp
            : DateTime.MinValue;
    }

    private static List<string> ReadScLogLines()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Milestone", "XProtect Smart Client");
        var candidates = new List<string>();
        try
        {
            var main = Path.Combine(dir, "MIPLog.txt");
            if (File.Exists(main))
            {
                candidates.Add(main);
            }

            var rolling = Path.Combine(dir, "MIPLogs");
            if (Directory.Exists(rolling))
            {
                candidates.AddRange(Directory.GetFiles(rolling, "MIPLog.*.txt")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .Take(3));
            }
        }
        catch (Exception)
        {
            return new List<string>();
        }

        var lines = new List<string>();
        foreach (var file in candidates)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                lines.AddRange(reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')));
            }
            catch (Exception)
            {
            }
        }

        return lines;
    }

    private static AutomationElement FindDescendant(AutomationElement root, string automationId)
    {
        try
        {
            return root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch
        {
            return null;
        }
    }
}
