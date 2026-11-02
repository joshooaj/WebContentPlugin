using System.Text;

using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #76 probe: the Management Client Status tab pulls the Event Server's live proxy
/// sessions for the workspace and ONLY while the tab is selected. The polling cadence is proven
/// from the MC's own MIP.log messaging trace (every pull logs exactly one "Proxy status ..."
/// marker): opening the tab starts the pull loop, an open Smart Client proxied view shows up as
/// a session row (XProtect user + client machine) within the cadence, leaving the tab stops the
/// markers, and returning restarts them. Read-only throughout: never clicks Save, never mutates
/// workspace configuration. The role-gate denial half needs a second restricted user and stays
/// manual (MC-11/MC-12 in the verification matrix).
/// </summary>
public class McProxyStatusProbeTests
{
    /// <summary>Marker prefixes logged by WorkspaceConfigPage.RefreshStatus (#76).</summary>
    private const string SnapshotMarker = "Proxy status snapshot for workspace";
    private const string QueryFailMarker = "Proxy status query for workspace";

    private readonly ITestOutputHelper _output;

    public McProxyStatusProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeStatusTabPollsLiveSessionsAndStopsWhenLeft()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-status-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var machine = Environment.MachineName;
        var log = new StringBuilder();
        Window mcMain = null;
        ClientSession mc = null;
        ClientSession sc = null;
        try
        {
            ProcessKiller.KillClients("Client", "VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            // ---- Management Client phase: open the workspace and its Status tab ----
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

            // The tab exists because this user holds Manage (the #74 gate); selecting it starts
            // the pull loop.
            SelectTabByAutomationId(mcMain, "tabStatus");
            ManagementClientUi.ExpectControl(mcMain, "dgStatusSessions", 15);
            ManagementClientUi.ExpectControl(mcMain, "txtStatusSummary", 5);
            ManagementClientUi.ExpectControl(mcMain, "btnStatusRefresh", 5);
            log.AppendLine("status tab selected: dgStatusSessions + summary + refresh rendered");

            // 1. Polling proof from the messaging trace: within the ~3 s cadence plus one
            //    2 s query deadline, the MC's MIP.log must gain a status snapshot marker.
            var opened = CountStatusMarkers();
            Assert.True(WaitUntil(() => CountStatusMarkers() > opened, 12),
                "no 'Proxy status snapshot/query ... workspace' marker appeared in the Management Client MIP.log after opening the Status tab - the pull loop never sent (or the proxy channel is dead)");
            log.AppendLine($"polling trace: markers after opening the tab: {CountStatusMarkers() - opened}");

            // The tab answers honestly: a summary line or a failure banner, never blank silence.
            var quiet = WaitUntil(() => !string.IsNullOrWhiteSpace(SummaryText(mcMain)), 10)
                ? $"summary: '{SummaryText(mcMain)}'"
                : "summary still empty (a failure banner path is pinned by the unit suite)";
            log.AppendLine(quiet);
            Artifacts.Window(mcMain, "mc-status-quiet");

            // 2. Live session row: a proxied Smart Client view on this machine opens an ES
            //    session (SessionEvent Open at listener start); the MC's next polls must render
            //    it - XProtect user from the platform identity, machine from the client report.
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            sc = ClientSession.Launch(config.SmartClientExe);
            var scLogin = LoginFlow.WaitForLoginWindow(sc);
            LoginFlow.Login(sc, scLogin, config);
            LoginFlow.AcceptCertificateTrust(sc);
            var scMain = LoginFlow.WaitForSmartClientMain(sc);
            SmartClientUi.Maximize(scMain);
            Thread.Sleep(1500);
            scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            scMain = SmartClientUi.SelectWorkspace(scMain, sc, config.WorkspaceName);
            Assert.NotNull(SmartClientUi.WaitForMipViewItem(scMain, 60));
            log.AppendLine($"smart client workspace '{config.WorkspaceName}' open - its proxied view should register an Event Server session");

            var rowAppeared = WaitUntil(() => GridShowsMachine(mcMain, machine), 25);
            var summary = SummaryText(mcMain) ?? string.Empty;
            log.AppendLine($"session row visible: {rowAppeared}; machine '{machine}'; summary: '{summary}'");
            Artifacts.Window(mcMain, "mc-status-live");
            Assert.True(rowAppeared,
                "the Status tab never showed a session row for this machine's proxied Smart Client view within 25 s - polling or session tracking is broken end to end");
            Assert.Contains("live proxy session(s)", summary, StringComparison.Ordinal);

            // 3. No polling once the tab is left: switch to Settings, then the marker count must
            //    stay FLAT across several full cadence windows. This is the acceptance criterion's
            //    messaging-trace half - the timer is tied to tab selection, not to the page living.
            SelectTabByAutomationId(mcMain, "tabSettings");
            var baseline = CountStatusMarkers();
            Thread.Sleep(10000);
            var afterAway = CountStatusMarkers();
            log.AppendLine($"away window (10 s, three cadences): markers before={baseline} after={afterAway}");
            Assert.Equal(baseline, afterAway);

            // 4. Returning re-arms the loop: selecting Status again must resume the markers.
            SelectTabByAutomationId(mcMain, "tabStatus");
            var resumed = WaitUntil(() => CountStatusMarkers() > afterAway, 8);
            log.AppendLine($"reopen resumes polling: {resumed}");
            Assert.True(resumed, "selecting the Status tab again never resumed the pull loop");

            // 5. Read-only: none of this enabled the config pane's Save.
            Assert.False(ManagementClientUi.WaitForSaveEnabled(mcMain, 4));
            log.AppendLine("Save stayed disabled through the whole status probe");
        }
        finally
        {
            Artifacts.WriteText("mc-status-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain?.Close(); } catch { }
            if (mc != null) ManagementClientUi.AcceptPendingModal(mc, 5);
            ProcessKiller.KillClients("Client", "VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }
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

    private static string SummaryText(Window mcMain)
    {
        try
        {
            var summary = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("txtStatusSummary"));
            return summary?.Name;
        }
        catch { return null; }
    }

    private static bool GridShowsMachine(Window mcMain, string machine)
    {
        try
        {
            var grid = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("dgStatusSessions"));
            if (grid == null) return false;
            return grid.FindAllDescendants(cf => cf.ByName(machine)).Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Counts this machine's #76 pull markers in the Management Client MIP.log (plus its rolling
    /// archives). Success and query-failure markers both count: every pull leaves exactly one
    /// line, so the count IS the polling trace.
    /// </summary>
    private static int CountStatusMarkers()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Milestone", "XProtect Management Client", "Logs");
        var candidates = new List<string>();
        try
        {
            var top = Path.Combine(root, "MIP.log");
            if (File.Exists(top)) candidates.Add(top);
            var archive = Path.Combine(root, "Archive");
            if (Directory.Exists(archive)) candidates.AddRange(Directory.EnumerateFiles(archive, "MIP*.log"));
        }
        catch { return 0; }

        var count = 0;
        foreach (var file in candidates)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Contains(SnapshotMarker, StringComparison.Ordinal) ||
                        line.Contains(QueryFailMarker, StringComparison.Ordinal))
                    {
                        count++;
                    }
                }
            }
            catch { /* locked mid-write or unreadable: try the next file */ }
        }

        return count;
    }

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
}
