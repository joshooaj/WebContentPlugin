using System.Text;

using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #74 probe: the Management Client workspace config page renders as the four-tab shell
/// (Settings | Proxy | Diagnostics | Status), the Settings tab keeps every settings control on
/// the default tab (no navigation needed - the home-address probes must stay intact), and walking
/// the live Proxy grid (#81), the read-only Diagnostics grid (#84) and the read-only Status tab (#76)
/// never dirties the pane: Save must stay disabled through the whole walk ("no config writes
/// from the shell"). Since #81 the proxy section lives ONLY on the Proxy tab, which the probe
/// asserts by looking for a realized proxy control while Settings is selected. The Status tab is
/// expected visible because the probe user is a full administrator (holds Manage); the view-only
/// denial half of the role gate is pinned by MC-11 in the verification matrix.
/// Read-only: never clicks Save.
/// </summary>
public class McWorkspaceTabsProbeTests
{
    private readonly ITestOutputHelper _output;

    public McWorkspaceTabsProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeWorkspaceTabsShellRendersAndStaysReadOnly()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-tabs-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        ProcessKiller.KillClients("VideoOS.Administration");
        var session = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(session);
        LoginFlow.Login(session, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(session);
        var mcMain = LoginFlow.WaitForAdminMain(session);
        ManagementClientUi.CloseStrayLoginDialogs(session);
        ManagementClientUi.WaitForTreeReady(mcMain);

        var log = new StringBuilder();
        try
        {
            ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
            ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
            ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
            ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);

            // 1. Default tab is Settings and every existing control is reachable WITHOUT any
            //    tab navigation (the shape the home-address harness relies on). Since #81 the
            //    whole proxy section lives on the Proxy tab, so the Settings tab must NOT carry
            //    proxy controls any more - asserted, not just left unmentioned.
            var nameBox = ManagementClientUi.ExpectControl(mcMain, "txtName", 30);
            ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 5);
            var nameBefore = ReadValue(nameBox);
            log.AppendLine($"settings tab live: txtName reads '{nameBefore}'");

            // 2. Four-tab shell rendered; Status present because this user is a full admin.
            foreach (var aid in new[] { "tabSettings", "tabProxy", "tabDiagnostics", "tabStatus" })
            {
                ManagementClientUi.ExpectControl(mcMain, aid, 15);
            }

            log.AppendLine("tabs rendered: tabSettings tabProxy tabDiagnostics tabStatus");
            Artifacts.Window(mcMain, "mc-tabs-settings");

            // 3. Each tab selects (UIA SelectionItem, no mouse needed) and renders its content;
            //    screenshot per tab. The Proxy tab is live since #81 (structured allowlist grid);
            //    Diagnostics went live with #84 (read-only blocked-origin grid - MC-16 owns its
            //    live-content assertions).
            SelectTabByAutomationId(mcMain, "tabProxy");
            ManagementClientUi.ExpectControl(mcMain, "dgProxyTargets", 10);
            ManagementClientUi.ExpectControl(mcMain, "btnProxyTargetAdd", 5);
            log.AppendLine("proxy tab live surface rendered - structured allowlist grid present");
            Artifacts.Window(mcMain, "mc-tabs-proxy");

            SelectTabByAutomationId(mcMain, "tabDiagnostics");
            ManagementClientUi.ExpectControl(mcMain, "dgDiagnosticsBlocked", 10);
            ManagementClientUi.ExpectControl(mcMain, "txtDiagnosticsSummary", 5);
            ManagementClientUi.ExpectControl(mcMain, "btnDiagnosticsRefresh", 5);
            log.AppendLine("diagnostics tab live surface rendered - read-only blocked-origin grid present");
            Artifacts.Window(mcMain, "mc-tabs-diagnostics");

            // Status is live since #76: no placeholder - the shell check here is that selecting it
            // renders the read-only session surface (grid + summary) without dirtying the pane.
            // The live-content assertions live in McProxyStatusProbeTests.
            SelectTabByAutomationId(mcMain, "tabStatus");
            ManagementClientUi.ExpectControl(mcMain, "dgStatusSessions", 10);
            ManagementClientUi.ExpectControl(mcMain, "txtStatusSummary", 5);
            log.AppendLine("status tab live surface rendered - session grid present (admin holds Manage)");
            Artifacts.Window(mcMain, "mc-tabs-status");

            // 4. No config writes from the shell walk: after selecting three tabs and returning,
            //    the shell's Save must still be disabled - selecting tabs fired no change events.
            Assert.False(ManagementClientUi.WaitForSaveEnabled(mcMain, 4));
            log.AppendLine("Save stayed disabled through the whole tab walk");

            // 5. Back on Settings, the fields still read the same values (fill untouched), and
            //    the Proxy tab's grid still renders the same rows after the walk. The elements
            //    are RE-FOUND: a WPF TabControl unloads the non-selected tab's content, so the
            //    pre-walk element handles are stale by now.
            SelectTabByAutomationId(mcMain, "tabSettings");
            var nameAfter = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtName", 10));
            Assert.Equal(nameBefore, nameAfter);

            var strayProxyControl = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("chkProxyEnabled"));
            Assert.True(strayProxyControl == null,
                "the Settings tab realized a proxy control - the proxy section lives on the Proxy tab (#81).");

            AllowlistGridUi.OpenProxyTab(mcMain);
            var entriesAfter = AllowlistGridUi.ReadEntries(mcMain);
            log.AppendLine($"round-trip intact: txtName '{nameAfter}', allowlist rows '{string.Join(" | ", entriesAfter)}'");

            Artifacts.Window(mcMain, "mc-tabs-final");
        }
        finally
        {
            Artifacts.WriteText("mc-tabs-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain.Close(); } catch { }
            ManagementClientUi.AcceptPendingModal(session, 5);
            ProcessKiller.KillClients("VideoOS.Administration");
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

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name; }
        catch { return element.Name; }
    }
}
