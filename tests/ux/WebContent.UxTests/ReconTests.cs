using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Discovery pass: launches both clients, logs in, captures screenshots and UIA tree
/// dumps so the scenario tests can target stable locators. Re-run whenever the client
/// UI changes.
/// </summary>
public class ReconTests
{
    private readonly ITestOutputHelper _output;

    public ReconTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "ux-recon")]
    public void Probe_ManagementClientTreePatterns()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        ProcessKiller.KillClients("VideoOS.Administration");
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        Thread.Sleep(3000);
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));

        var sb = new System.Text.StringBuilder();
        var items = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem));
        foreach (var item in items)
        {
            var ecSupported = false;
            var state = "?";
            var clickable = "?";
            try { ecSupported = item.Patterns.ExpandCollapse.IsSupported; } catch { }
            if (ecSupported)
            {
                try { state = item.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString(); } catch (Exception ex) { state = "ERR:" + ex.GetType().Name; }
            }
            try { clickable = item.GetClickablePoint().ToString(); } catch (Exception ex) { clickable = "ERR:" + ex.GetType().Name; }
            sb.AppendLine($"'{item.Name}' ec={ecSupported} state={state} pt={clickable}");
        }
        Artifacts.WriteText("mc-tree-probe.txt", sb.ToString());
        ProcessKiller.KillClients("VideoOS.Administration");
    }

    [Fact]
    [Trait("Category", "ux-recon")]
    public void Probe_ManagementClientTreeToggle()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        ProcessKiller.KillClients("VideoOS.Administration");
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        Thread.Sleep(3000);
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
        ManagementClientUi.CloseStrayLoginDialogs(mc);
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));

        var sb = new System.Text.StringBuilder();
        void Log(string s) { sb.AppendLine(s); _output.WriteLine(s); }
        int Count() => mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Length;
        bool Has(string n) => mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .Any(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase));

        // A) LegacyIAccessible DoDefaultAction on 'Servers' (expect collapse).
        var servers = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(t => t.Name == "Servers");
        if (servers != null)
        {
            var c0 = Count();
            try
            {
                if (servers.Patterns.LegacyIAccessible.IsSupported)
                {
                    servers.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                    Thread.Sleep(1200);
                }
                else Log("Servers: no LegacyIAccessible");
            }
            catch (Exception ex) { Log("DoDefaultAction ERR:" + ex.GetType().Name + " " + ex.Message); }
            var c1 = Count();
            Log($"A) DoDefaultAction 'Servers': count {c0}->{c1} RecordingServersStill={Has("Recording Servers")}");
            try
            {
                servers.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                Thread.Sleep(1200);
            }
            catch { }
            Log($"   toggle back: count={Count()} RecordingServers={Has("Recording Servers")}");
        }

        // B) Real mouse double-click on a node label (expect toggle).
        var devices = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(t => t.Name == "Devices");
        if (devices != null)
        {
            var r = devices.BoundingRectangle;
            Log($"B) Devices rect={r}");
            var c0 = Count();
            FlaUI.Core.Input.Mouse.Position = new System.Drawing.Point(r.X + 30, r.Y + r.Height / 2);
            Thread.Sleep(200);
            FlaUI.Core.Input.Mouse.DoubleClick();
            Thread.Sleep(1500);
            Log($"   double-click 'Devices': count {c0}->{Count()} CamerasStill={Has("Cameras")}");
            FlaUI.Core.Input.Mouse.DoubleClick();
            Thread.Sleep(1500);
            Log($"   toggle back: count={Count()} Cameras={Has("Cameras")}");
        }

        // C) Select() a deep off-screen node, then check rect and double-click toggle there.
        var cascadia = ManagementClientUi.WaitForTreeItemContaining(mcMain, "Cascadia", 5) != null;
        Log($"C) Cascadia realized before select: {cascadia}");
        var node = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(t => (t.Name ?? "").Contains("Cascadia", StringComparison.OrdinalIgnoreCase));
        if (node != null)
        {
            try { node.Patterns.SelectionItem.Pattern.Select(); } catch (Exception ex) { Log("select ERR:" + ex.GetType().Name); }
            Thread.Sleep(1000);
            var r = node.BoundingRectangle;
            Log($"   after Select: rect={r}");
            var workspacesBefore = Has(ManagementClientUi.WorkspacesNodeName);
            var r2 = node.BoundingRectangle;
            FlaUI.Core.Input.Mouse.Position = new System.Drawing.Point(r2.X + 30, r2.Y + r2.Height / 2);
            Thread.Sleep(200);
            FlaUI.Core.Input.Mouse.DoubleClick();
            Thread.Sleep(1500);
            var workspacesAfter = Has(ManagementClientUi.WorkspacesNodeName);
            Log($"   double-click 'Cascadia Technology': workspaces {workspacesBefore}->{workspacesAfter}");
            FlaUI.Core.Input.Mouse.DoubleClick();
            Thread.Sleep(1500);
            Log($"   toggle back: workspaces={Has(ManagementClientUi.WorkspacesNodeName)}");
            // Re-expand + screenshot for eyeballing.
            FlaUI.Core.Input.Mouse.DoubleClick();
            Thread.Sleep(1500);
        }
        Artifacts.Window(mcMain, "mc-tree-toggle-final");
        Artifacts.WriteText("mc-tree-toggle.txt", sb.ToString());
        ProcessKiller.KillClients("VideoOS.Administration");
    }

    [Fact]
    [Trait("Category", "ux-recon")]
    public void Probe_ManagementClientTreeInteraction()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        ProcessKiller.KillClients("VideoOS.Administration");
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        Thread.Sleep(3000);
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));
        ManagementClientUi.CloseStrayLoginDialogs(mc);
        mcMain = mc.Windows().First(w => (w.Name ?? string.Empty).Contains("Management Client"));

        var sb = new System.Text.StringBuilder();
        void Log(string s) { sb.AppendLine(s); _output.WriteLine(s); }

        var tree = mcMain.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        Log("tree found: " + (tree != null) + " rect=" + (tree?.BoundingRectangle.ToString() ?? "-"));
        if (tree != null)
        {
            try { Log("tree patterns: " + string.Join(",", tree.GetSupportedPatterns().Select(p => p.ToString()))); }
            catch (Exception ex) { Log("tree patterns ERR:" + ex.GetType().Name); }
            var treeParent = tree.Parent;
            if (treeParent != null)
                try { Log("tree parent: " + treeParent.ControlType + " cls=" + treeParent.ClassName + " patterns=" +
                        string.Join(",", treeParent.GetSupportedPatterns().Select(p => p.ToString()))); }
                catch (Exception ex) { Log("tree parent ERR:" + ex.GetType().Name); }
        }

        var items = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).ToList();
        Log("item count: " + items.Count);
        foreach (var probe in new[] { "Home", "Servers", "Recording Servers", "Cascadia Technology" })
        {
            var it = items.FirstOrDefault(x => string.Equals(x.Name, probe, StringComparison.OrdinalIgnoreCase));
            if (it == null) { Log($"'{probe}': not realized"); continue; }
            string[] pats;
            try { pats = it.GetSupportedPatterns().Select(p => p.ToString()).ToArray(); }
            catch (Exception ex) { pats = new[] { "ERR:" + ex.GetType().Name }; }
            Log($"'{probe}': patterns={string.Join(",", pats)} rect={it.BoundingRectangle}");
        }

        // 1) Real click on a visible row, then check UIA focus + keyboard effect.
        var servers = items.FirstOrDefault(x => string.Equals(x.Name, "Servers", StringComparison.OrdinalIgnoreCase));
        if (servers != null)
        {
            var r = servers.BoundingRectangle;
            FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(r.X + 10, r.Y + r.Height / 2));
            Thread.Sleep(800);

            var before = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Length;
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.LEFT);
            Thread.Sleep(1200);
            var afterLeft = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Length;
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
            Thread.Sleep(1200);
            var afterRight = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Length;
            Log($"item counts: before={before} afterLEFT={afterLeft} afterRIGHT={afterRight}");
        }

        // 2) Mouse wheel over the tree (SendInput) to scroll off-screen nodes into view.
        var tr = tree?.BoundingRectangle ?? default;
        if (tr.Width > 0)
        {
            var cx = (int)(tr.X + tr.Width / 2);
            var cy = (int)(tr.Y + Math.Min(tr.Height / 2, 300));
            FlaUI.Core.Input.Mouse.Position = new System.Drawing.Point(cx, cy);
            Thread.Sleep(300);
            for (var i = 0; i < 5; i++) { WheelDown(cx, cy); Thread.Sleep(200); }
            Thread.Sleep(1000);
            items = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).ToList();
            var cascadia = items.FirstOrDefault(x => (x.Name ?? "").Contains("Cascadia", StringComparison.OrdinalIgnoreCase));
            string cp = "-";
            if (cascadia != null) { try { cp = cascadia.GetClickablePoint().ToString(); } catch (Exception ex) { cp = "ERR:" + ex.GetType().Name; } }
            Log($"after wheel x5: cascadia realized={cascadia != null} clickable={cp} count={items.Count}");
            sb.AppendLine("visible-after-wheel: " + string.Join(" | ", items.Select(x => $"{x.Name}@{x.BoundingRectangle.Y}")));
        }

        // 3) SelectionItem.Select on an off-screen node.
        items = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).ToList();
        var deep = items.FirstOrDefault(x => (x.Name ?? "").Contains("Web Content Workspaces", StringComparison.OrdinalIgnoreCase))
                ?? items.LastOrDefault();
        if (deep != null)
        {
            try
            {
                if (deep.Patterns.SelectionItem.IsSupported)
                {
                    deep.Patterns.SelectionItem.Pattern.Select();
                    Thread.Sleep(1200);
                    var cp2 = "?";
                    try { cp2 = deep.GetClickablePoint().ToString(); } catch (Exception ex) { cp2 = "ERR:" + ex.GetType().Name; }
                    Log($"after Select on '{deep.Name}': clickable={cp2}");
                }
                else Log($"'{deep.Name}' has no SelectionItem pattern");
            }
            catch (Exception ex) { Log($"Select on '{deep.Name}' ERR:{ex.GetType().Name}: {ex.Message}"); }
        }

        // 4) Double-click on a visible expandable row's label (WinForms toggles expansion).
        var recording = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(x => string.Equals(x.Name, "Recording Servers", StringComparison.OrdinalIgnoreCase));
        if (recording != null)
        {
            var cnt0 = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Length;
            recording.DoubleClick();
            Thread.Sleep(1200);
            var cnt1 = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Length;
            Log($"DoubleClick 'Recording Servers': count {cnt0} -> {cnt1}");
        }

        Artifacts.Window(mcMain, "mc-tree-interact-final");
        Artifacts.WriteText("mc-tree-interact.txt", sb.ToString());
        ProcessKiller.KillClients("VideoOS.Administration");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static void WheelDown(int x, int y)
    {
        // Absolute (normalized) coordinates for the wheel event so it lands over the tree.
        var vw = GetSystemMetrics(0); // SM_CXSCREEN (virtual desktop approx: primary)
        var vh = GetSystemMetrics(1);
        var nx = x * 65535 / Math.Max(1, vw - 1);
        var ny = y * 65535 / Math.Max(1, vh - 1);
        var inputs = new[]
        {
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = 0x0001 | 0x0002 | 0x0800 | 0x2000, mouseData = 0 } }, // MOVE|ABS|VIRTUALDESK
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = 0x0800, mouseData = -120 } }, // WHEEL
        };
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    [Fact]
    [Trait("Category", "ux-recon")]
    public async Task Recon_SmartClient_And_ManagementClient()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        using var probe = new ProbePageServer();
        _output.WriteLine($"Probe pages: {probe.Base}alpha {probe.Base}beta");

        CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
        ProcessKiller.KillClients("Client", "VideoOS.Administration");
        ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

        using         var sc = ClientSession.Launch(config.SmartClientExe);
        var scLogin = LoginFlow.WaitForLoginWindow(sc);
        Artifacts.Window(scLogin, "sc-00-login");
        Artifacts.WriteText("sc-00-login-tree.txt", TreeDump.Of(scLogin, 12));
        LoginFlow.Login(sc, scLogin, config);
        var scAfterConnect = sc.FindWindow(w =>
            w.FindFirstDescendant(cf => cf.ByAutomationId("_btnConnect")) != null ||
            w.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton")) != null);
        if (scAfterConnect != null)
        {
            Artifacts.Window(scAfterConnect, "sc-00-after-connect");
            Artifacts.WriteText("sc-00-after-connect-tree.txt", TreeDump.Of(scAfterConnect, 12));
        }
        LoginFlow.AcceptCertificateTrust(sc);
        var scMain = LoginFlow.WaitForSmartClientMain(sc);
        SmartClientUi.Maximize(scMain);
        Thread.Sleep(1500);
        scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
        Artifacts.Window(scMain, "sc-01-main");
        Artifacts.WriteText("sc-01-main-tree.txt", TreeDump.Of(scMain, 12));

        scMain = SmartClientUi.SelectWorkspace(scMain, sc, config.WorkspaceName);
        Artifacts.Window(scMain, "sc-02-webcontent-tab");

        var item = SmartClientUi.WaitForMipViewItem(scMain);
        Artifacts.Element(item, "sc-03-mipviewitem");
        var addressBar = item.FindFirstDescendant(cf => cf.ByAutomationId("addressBar"));
        SmartClientUi.NavigateAddress(addressBar, probe.Base + "alpha");

        await using var cdp = new CdpConnector();
        var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot, 60);
        var pageList = new List<string>();
        foreach (var context in browser.Contexts)
        {
            foreach (var page in context.Pages)
            {
                pageList.Add($"{page.Url} | {await page.TitleAsync()}");
            }
        }
        _output.WriteLine("CDP connected. Pages: " + string.Join(" ;; ", pageList));
        Artifacts.WriteText("sc-04-cdp-pages.txt", string.Join(Environment.NewLine, pageList));

        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        Artifacts.Window(mcLogin, "mc-00-login");
        Artifacts.WriteText("mc-00-login-tree.txt", TreeDump.Of(mcLogin, 12));
        LoginFlow.Login(mc, mcLogin, config);
        var mcAfterConnect = mc.FindWindow(w =>
            w.FindFirstDescendant(cf => cf.ByAutomationId("_btnConnect")) != null ||
            w.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton")) != null);
        if (mcAfterConnect != null)
        {
            Artifacts.Window(mcAfterConnect, "mc-00-after-connect");
            Artifacts.WriteText("mc-00-after-connect-tree.txt", TreeDump.Of(mcAfterConnect, 12));
        }
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        Artifacts.Window(mcMain, "mc-01-main");
        Artifacts.WriteText("mc-01-main-tree.txt", TreeDump.Of(mcMain, 12));

        ProcessKiller.KillClients("Client", "VideoOS.Administration");
        ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        _output.WriteLine($"Done. Artifacts: {Artifacts.RunDirectory}");
    }
}
