using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Bug regression for "Hide address hides the address box but keeps the navigation buttons":
/// the address bar used to stay visible for the whole session because the computed
/// AddressVisibility binding evaluated before the ViewItemManager was attached and never
/// re-raised. The Management Client half sets the intended state through the WPF-hosted
/// checkboxes (and removes probe workspaces left over from interrupted runs); the Smart
/// Client half asserts the resulting toolbar contract live.
/// </summary>
public class ToolbarOptionsTests
{
    private readonly ITestOutputHelper _output;

    public ToolbarOptionsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "ux")]
    public async Task HideAddress_WithVisibleToolbar_RemovesAddressBar()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
        ProcessKiller.KillClients("Client", "VideoOS.Administration");
        ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

        // ---- Management Client: hide-address state on the configured workspace ----
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        SmartClientUi.Maximize(mcMain);
        Thread.Sleep(3000);
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
        ManagementClientUi.CloseStrayLoginDialogs(mc);
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));

        ManagementClientUi.CollapseTreeToRoot(mcMain, allowExpanded: new[] { config.PluginNodeName });
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
        ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
        ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);

        // Remove probe workspaces left behind by interrupted end-to-end runs.
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
        for (var n = 0; n < 3; n++)
        {
            if (!ManagementClientUi.DeleteWorkspaceIfPresent(mc, mcMain, config.ProbeWorkspaceName))
            {
                ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
                break;
            }
            _output.WriteLine($"Deleted stray probe workspace '{config.ProbeWorkspaceName}'.");
            ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
        }

        ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);
        ManagementClientUi.ExpectControl(mcMain, "chkHideToolbar", 30);

        // The bug scenario: toolbar visible, address box suppressed.
        ManagementClientUi.EnsureCheckBox(mcMain, "chkHideToolbar", false);
        ManagementClientUi.EnsureCheckBox(mcMain, "chkHideAddress", true);
        Artifacts.Window(mcMain, "mc-toolbar-options");
        // EnsureCheckBox only toggles when the UIA state differs, so the checkboxes can already
        // be in the target state (manual setup or a previous run). Then Save never enables and
        // the desired state is already persisted on the server; treat that as satisfied.
        if (ManagementClientUi.Save(mcMain, 15))
        {
            Artifacts.Window(mcMain, "mc-toolbar-saved");
        }
        else
        {
            _output.WriteLine("Save never became enabled: workspace already had the desired hide-toolbar/hide-address state.");
        }

        // ---- Smart Client: toolbar keeps its buttons, loses the address box ----
        using var sc = ClientSession.Launch(config.SmartClientExe);
        var scLogin = LoginFlow.WaitForLoginWindow(sc);
        LoginFlow.Login(sc, scLogin, config);
        LoginFlow.AcceptCertificateTrust(sc);
        var scMain = LoginFlow.WaitForSmartClientMain(sc);
        SmartClientUi.Maximize(scMain);
        Thread.Sleep(2000);
        scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

        try
        {
            scMain = SmartClientUi.SelectWorkspace(scMain, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(scMain, 60);

            var addressBar = item.FindFirstDescendant(cf => cf.ByAutomationId("addressBar"));
            Assert.Null(addressBar);

            var navHome = item.FindFirstDescendant(cf => cf.ByAutomationId("NavHome"))
                ?? throw new InvalidOperationException("Hide toolbar OFF must keep the navigation buttons; NavHome vanished.");
            Assert.NotNull(item.FindFirstDescendant(cf => cf.ByAutomationId("NavBack")));
            Assert.NotNull(item.FindFirstDescendant(cf => cf.ByAutomationId("NavReload")));
            Artifacts.Element(item, "sc-hideaddress-contract");

            // The browser still sits on the home address and is reachable through the toolbar.
            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForAnyPageAsync(browser, new[] { config.HomeAddressFragment, "auth.joshooaj.com" }, 60);
            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = Artifacts.PathOf("sc-probe-home-only.png"),
            });
        }
        catch
        {
            var fresh = sc.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
            if (fresh != null)
            {
                Artifacts.Window(fresh, "sc-toolbar-failure-state");
                Artifacts.WriteText("sc-toolbar-failure-tree.txt", TreeDump.Of(fresh, 12));
            }
            throw;
        }
        finally
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }
    }
}
