using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using System.Diagnostics;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Captures the README screenshot scenarios: Smart Client showing the Demo View
/// (sidebar closed) and its setup-mode properties panel, plus Management Client
/// workspace configuration and role security screens. Artifacts are inspected and
/// hand-promoted to docs/assets/images, so these tests intentionally assert little.
/// </summary>
public class ReadmeScreenshotTests
{
    private readonly ITestOutputHelper _output;

    public ReadmeScreenshotTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "ux-screenshots")]
    public void SmartClient_CapturesViewAndProperties()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        ProcessKiller.KillClients("Client");
        ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

        using var sc = ClientSession.Launch(config.SmartClientExe);
        var login = LoginFlow.WaitForLoginWindow(sc);
        LoginFlow.Login(sc, login, config);
        LoginFlow.AcceptCertificateTrust(sc);
        var main = LoginFlow.WaitForSmartClientMain(sc);
        SmartClientUi.Maximize(main);
        Thread.Sleep(2000);
        main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

        try
        {
            // --- hero shot: Demo View selected, sidebar closed ---
            main = SmartClientUi.SelectWorkspace(main, sc, config.ViewName, 60);
            var tile = SmartClientUi.WaitForMipViewItem(main, 60);
            SmartClientUi.CloseSidePanels(main);
            Thread.Sleep(1000);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            Assert.False(SmartClientUi.SidePanelsOpen(main), "Sidebar could not be closed before the hero shot.");
            Artifacts.Window(main, "readme-sc-01-view");

            // --- properties shot: setup mode, select the web content tile, show the Properties panel ---
            SmartClientUi.ClickSetup(main);
            Thread.Sleep(3000);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            Artifacts.WriteText("sc-setup-tree.txt", TreeDump.Of(main, 12));
            Artifacts.Window(main, "sc-02-setup-open");

            // Re-open the side pane if the earlier close survived entering setup mode.
            if (!SmartClientUi.SidePanelsOpen(main))
                SmartClientUi.TryInvokeById(main, "PanesSetupProperties");
            var sidePanels = SmartClientUi.FindSidePanels(main)
                ?? throw new InvalidOperationException("Side panels not visible in setup mode.");
            Assert.True(SmartClientUi.SidePanelsOpen(main), "Setup mode did not open the side panels.");

            tile = SmartClientUi.WaitForMipViewItem(main, 30);
            var tr = tile.BoundingRectangle;
            // Click near the top of the tile: selects the gadget in setup mode without
            // driving focus deep into the embedded browser page.
            Mouse.Click(new System.Drawing.Point((int)(tr.X + tr.Width / 2), (int)(tr.Y + 15)));
            Thread.Sleep(2500);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            Artifacts.WriteText("sc-setup-selected-tree.txt", TreeDump.Of(main, 12));
            Artifacts.Window(main, "sc-03-tile-selected");

            // The Properties panel must be fully inside the window for a good crop. Neighbouring
            // setup panels (System overview, Overlay buttons) steal height; collapse only those
            // that are actually expanded, and re-check until the plugin controls fit on screen.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var ctl = main.FindFirstDescendant(cf => cf.ByClassName("WebContentPropertiesUserControl"));
                if (ctl != null && ctl.BoundingRectangle.Bottom < main.BoundingRectangle.Bottom - 40) break;
                foreach (var (panelClass, toggleId) in new[]
                {
                    ("SystemPanelView", "PanesSetupSystemOverview"),
                    ("OverlayButtonsPanelView", "PanesSetupOverlayButtons"),
                })
                {
                    var panel = main.FindFirstDescendant(cf => cf.ByClassName(panelClass));
                    if (panel != null && panel.BoundingRectangle.Height > 150)
                        SmartClientUi.TryInvokeById(main, toggleId, 1200);
                }
                main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            }

            // Park the pointer over the second tile so no checkbox tooltip appears in the shot.
            var vr = main.BoundingRectangle;
            Mouse.Position = new System.Drawing.Point((int)(vr.X + vr.Width * 0.62), (int)(vr.Y + vr.Height * 0.55));
            Thread.Sleep(1200);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            Artifacts.WriteText("sc-setup-scrolled-tree.txt", TreeDump.Of(main, 14));
            Artifacts.Window(main, "sc-04-properties-panel");

            var propsPanel = main.FindFirstDescendant(cf => cf.ByClassName("PropertiesPanelView"))
                ?? throw new InvalidOperationException("PropertiesPanelView not found in setup mode.");
            var propsCtl = main.FindFirstDescendant(cf => cf.ByClassName("WebContentPropertiesUserControl"));
            var cropRects = new List<System.Drawing.Rectangle> { propsPanel.BoundingRectangle };
            if (propsCtl != null) cropRects.Add(propsCtl.BoundingRectangle);
            // Crop the rendered window (not the element rect) so nothing below the window edge appears black.
            Artifacts.WindowCrop(main, "readme-sc-02-properties", cropRects.ToArray());
        }
        catch
        {
            var fresh = sc.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
            if (fresh != null)
            {
                Artifacts.Window(fresh, "sc-shot-failure-state");
                Artifacts.WriteText("sc-shot-failure-tree.txt", TreeDump.Of(fresh, 12));
            }
            throw;
        }
        finally
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }
    }

    [Fact]
    [Trait("Category", "ux-screenshots")]
    public void ManagementClient_CapturesWorkspaceAndRoleSecurity()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        ClientSession mc = null;
        try
        {
            ProcessKiller.KillClients("VideoOS.Administration");

            mc = ClientSession.Launch(config.AdminClientExe);
            var mcLogin = LoginFlow.WaitForLoginWindow(mc);
            LoginFlow.Login(mc, mcLogin, config);
            LoginFlow.AcceptCertificateTrust(mc);
            var mcMain = LoginFlow.WaitForAdminMain(mc);
            SmartClientUi.Maximize(mcMain);
            MaximizeWindow(mc.Process);
            Thread.Sleep(3000);
            mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
            ManagementClientUi.CloseStrayLoginDialogs(mc);
            mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));

            // Tree hygiene for clean shots: Home open, its children collapsed, plugin branch open.
            ManagementClientUi.CollapseTreeToRoot(mcMain, allowExpanded: new[] { config.PluginNodeName });
            mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
            Artifacts.Window(mcMain, "mc-00-tree-reset");
            ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
            ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
            ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);
            ManagementClientUi.ExpectControl(mcMain, "txtName", 30);
            Thread.Sleep(1500);
            mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));

            Artifacts.WriteText("mc-workspace-tree.txt", TreeDump.Of(mcMain, 8));
            var tree = mcMain.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree))
                ?? throw new InvalidOperationException("Management Client tree not found.");
            Artifacts.WindowCrop(mcMain, "readme-mc-01-workspace",
                TreeRange(mcMain, tree, "Home", "Alarms"),
                DetailRange(mcMain, tree));

            // --- role security tab: Security > Roles, pick the role row in the list pane, plugin tab ---
            ManagementClientUi.EnsureExpanded(mcMain, "Security", "Roles");
            ManagementClientUi.SelectTreeItem(mcMain, "Roles");
            Thread.Sleep(2000);
            var roleRow = ManagementClientUi.WaitAndSelectListRow(mcMain, config.RoleName, 20);
            Thread.Sleep(2000);
            var tab = ManagementClientUi.SelectTab(mcMain, config.PluginNodeName, 20);
            Thread.Sleep(1000);
            // In the tab's security tree: expand the plugin root via its [+], then select the
            // workspace leaf so the "Security settings for" area shows real permission rows.
            var detailTree = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("treeViewItems"));
            if (detailTree != null)
            {
                AutomationElement Leaf() => detailTree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .FirstOrDefault(t => string.Equals(t.Name, ManagementClientUi.WorkspacesNodeName, StringComparison.OrdinalIgnoreCase));

                var leaf = Leaf();
                if (leaf == null)
                {
                    var root = detailTree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                        .FirstOrDefault(t => string.Equals(t.Name, config.PluginNodeName, StringComparison.OrdinalIgnoreCase));
                    if (root != null)
                    {
                        // treeViewItems is a native SysTreeView32: ExpandCollapse and the RIGHT key both work.
                        try { if (root.Patterns.ExpandCollapse.IsSupported) root.Patterns.ExpandCollapse.Pattern.Expand(); } catch { }
                        Thread.Sleep(800);
                        leaf = Leaf();
                        if (leaf == null)
                        {
                            try { root.Focus(); } catch { }
                            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
                            Thread.Sleep(800);
                            leaf = Leaf();
                        }
                    }
                }
                if (leaf != null)
                {
                    var dr = leaf.BoundingRectangle;
                    if (dr.Width > 0)
                        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(dr.X + Math.Max(25, dr.Width / 2), dr.Y + dr.Height / 2));
                    Thread.Sleep(1500);
                }

                // Re-click the (already-selected) role row on its label so the grid shows the
                // selection highlight again without triggering a selection-changed reload.
                var rr2 = roleRow.BoundingRectangle;
                if (rr2.Width > 0)
                    FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(rr2.X + Math.Min(rr2.Width - 5, 150), rr2.Y + rr2.Height / 2));
                Thread.Sleep(800);
            }
            mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
            Artifacts.WriteText("mc-role-tree.txt", TreeDump.Of(mcMain, 12));
            Artifacts.WindowCrop(mcMain, "readme-mc-02-role-security",
                TreeRange(mcMain, tree, "Home", "Alarms"),
                DetailRange(mcMain, tree, tab.BoundingRectangle, roleRow.BoundingRectangle));
        }
        catch
        {
            try
            {
                var fresh = mc?.Windows().FirstOrDefault(w => (w.Name ?? string.Empty).Contains("Management Client"));
                if (fresh != null)
                {
                    Artifacts.Window(fresh, "mc-shot-failure-state");
                    Artifacts.WriteText("mc-shot-failure-tree.txt", TreeDump.Of(fresh, 8));
                }
            }
            catch { }
            throw;
        }
        finally
        {
            mc?.Dispose();
            ProcessKiller.KillClients("VideoOS.Administration");
        }
    }

    /// <summary>Forces maximize via Win32; the WindowPattern route is unreliable for WinForms MC.</summary>
    private static void MaximizeWindow(Process process)
    {
        try
        {
            ShowWindow(process.MainWindowHandle, 3); // SW_MAXIMIZE
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>Tree column, freshly resolved via the TreeNavigation automationId, trimmed between the named nodes.</summary>
    private static System.Drawing.Rectangle TreeRange(Window mcMain, AutomationElement tree, string topNode, string bottomNode)
    {
        var nav = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("TreeNavigation")) ?? tree;
        var tr = nav.BoundingRectangle;
        var items = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem));
        var top = items.FirstOrDefault(t => (t.Name ?? string.Empty).StartsWith(topNode, StringComparison.OrdinalIgnoreCase))?.BoundingRectangle.Top
            ?? tr.Top;
        var bottom = items.FirstOrDefault(t => string.Equals(t.Name, bottomNode, StringComparison.OrdinalIgnoreCase))?.BoundingRectangle.Bottom
            ?? tr.Bottom;
        if (bottom < top + 60) bottom = top + 60;
        return new System.Drawing.Rectangle(tr.X, (int)top, tr.Width, (int)(bottom - top));
    }

    /// <summary>Everything rendered in the detail pane (right of the tree): breadcrumb, tabs, labels, inputs.</summary>
    private static System.Drawing.Rectangle DetailRange(Window mcMain, AutomationElement tree, params System.Drawing.Rectangle[] extra)
    {
        var treeRight = tree.BoundingRectangle.X + tree.BoundingRectangle.Width;
        var rects = mcMain.FindAllDescendants(cf =>
                cf.ByControlType(ControlType.Edit).Or(cf.ByControlType(ControlType.CheckBox))
                    .Or(cf.ByControlType(ControlType.ComboBox)).Or(cf.ByControlType(ControlType.Text))
                    .Or(cf.ByControlType(ControlType.Tab)))
            .Select(e => e.BoundingRectangle)
            .Where(r => r.X >= treeRight - 20 && r.Width > 0 && r.Height > 0)
            .ToList();
        rects.AddRange(extra);
        if (rects.Count == 0)
            return new System.Drawing.Rectangle((int)treeRight, 60, 900, 600);
        var left = rects.Min(r => (double)r.X);
        var top = rects.Min(r => (double)r.Y);
        top = Math.Max(0, top - 28); // include the page-header/breadcrumb row above the first control
        var right = rects.Max(r => (double)(r.X + r.Width));
        var bottom = rects.Max(r => (double)(r.Y + r.Height));
        return new System.Drawing.Rectangle((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
}
