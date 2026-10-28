using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// End-to-end UX: create a Web Content workspace in the Management Client, save it,
/// then start Smart Client and verify the new workspace renders the provisioned page;
/// finally delete the throwaway workspace to leave the server clean.
/// </summary>
public class McToSmartClientWorkspaceTests
{
    private readonly ITestOutputHelper _output;

    public McToSmartClientWorkspaceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "ux")]
    public async Task ManagementClientCreateWorkspace_AppearsInSmartClient()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        using var probe = new ProbePageServer();
        CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
        ProcessKiller.KillClients("Client", "VideoOS.Administration");
        ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

        // ---- Management Client: create + configure the probe workspace ----
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        SmartClientUi.Maximize(mcMain);
        Thread.Sleep(1500);
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
        Artifacts.Window(mcMain, "mc-main");

        var node = ManagementClientUi.FindTreeItem(mcMain, ManagementClientUi.WorkspacesNodeName)
            ?? throw new InvalidOperationException("The plugin's 'Web Content Workspaces' node is missing from the Management Client tree.");

        var menu = ManagementClientUi.OpenContextMenu(mc, mcMain, node);
        _output.WriteLine($"node context menu: {string.Join(" | ", menu)}");
        Artifacts.WriteText("mc-node-menu.txt", string.Join(Environment.NewLine, menu));
        var added = ManagementClientUi.ClickMenuItem(mc, "Web Content Workspace")
                    || ManagementClientUi.ClickMenuItem(mc, "Add");
        Assert.True(added, $"No Add entry in the node context menu. Menu was: {string.Join(" | ", menu)}");

        ManagementClientUi.ExpectControl(mcMain, "txtName");
        Artifacts.Window(mcMain, "mc-probe-pane");
        ManagementClientUi.SetValue(mcMain, "txtName", config.ProbeWorkspaceName);
        ManagementClientUi.SetValue(mcMain, "txtHomeAddress", probe.Base + "beta");
        Assert.True(ManagementClientUi.Save(mcMain), "Management Client Save never became enabled/clickable.");
        Artifacts.Window(mcMain, "mc-after-save");

        // ---- Smart Client: the new workspace must appear and render ----
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
            scMain = SmartClientUi.SelectWorkspace(scMain, sc, config.ProbeWorkspaceName, timeoutSeconds: 60);
            var item = SmartClientUi.WaitForMipViewItem(scMain, 60);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot, 90);
            var page = await CdpConnector.WaitForPageAsync(browser, probe.Base + "beta", 90);
            var marker = await page.WaitForSelectorAsync("#probe-marker", new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 30000 });
            Assert.Equal("UX-PROBE-BETA", await marker.TextContentAsync());
            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions { Path = Artifacts.PathOf("e2e-probe-beta.png") });
            Artifacts.Element(item, "e2e-probe-workspace");
        }
        catch
        {
            var fresh = sc.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
            if (fresh != null)
            {
                Artifacts.Window(fresh, "e2e-failure-state");
                Artifacts.WriteText("e2e-failure-tree.txt", TreeDump.Of(fresh, 12));
                Artifacts.WriteText("e2e-overflow.txt", string.Join(", ", SmartClientUi.OpenOverflow(fresh, sc)));
            }
            throw;
        }
        finally
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            // ---- best-effort cleanup: delete the probe workspace from the server ----
            try
            {
                mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
                ManagementClientUi.ExpandWorkspacesNode(mcMain);
                var probeItem = ManagementClientUi.WaitForTreeItem(mcMain, config.ProbeWorkspaceName, 30);
                var cleanupMenu = ManagementClientUi.OpenContextMenu(mc, mcMain, probeItem);
                Artifacts.WriteText("mc-cleanup-menu.txt", string.Join(Environment.NewLine, cleanupMenu));
                if (ManagementClientUi.ClickMenuItem(mc, "Delete") || ManagementClientUi.ClickMenuItem(mc, "Remove"))
                {
                    ManagementClientUi.AcceptConfirmation(mc);
                    ManagementClientUi.Save(mcMain, 5);
                    var gone = ManagementClientUi.FindTreeItem(mcMain, config.ProbeWorkspaceName) == null;
                    _output.WriteLine(gone ? "Probe workspace deleted from server." : "WARNING: probe workspace still present after delete.");
                }
                else
                {
                    _output.WriteLine($"WARNING: no Delete entry in item context menu: {string.Join(" | ", cleanupMenu)}");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"WARNING: probe workspace cleanup failed: {ex.Message}");
            }

            ProcessKiller.KillClients("VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }
    }
}
