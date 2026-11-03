using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #71 live probe (SC-9): the Smart Client properties pane of the WebContent view item
/// inside the admin-defined (managed) workspace renders the security-relevant field — the
/// pinned home/target URL — DISABLED with a "Managed by Administrator" indicator (and the same
/// notice under the read-only Proxy posture), while the cosmetic checkboxes stay enabled and a
/// per-instance choice survives the workspace re-seed that runs whenever the view/properties is
/// regenerated. The toggle cycle proves both halves of the acceptance criteria: locked settings
/// never accept a user edit, unlocked ones save per instance even though the definition seeds
/// their initial value.
/// </summary>
public class ScManagedWorkspaceProbeTests
{
    private const string ManagedNoticeText = "Managed by Administrator";
    private const string LockCheckboxId = "chkClearBrowsingDataOnExit";

    private readonly ITestOutputHelper _output;

    public ScManagedWorkspaceProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "ux")]
    public void ProbeManagedWorkspaceLocksPropertiesAndKeepsPerInstanceChoices()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        ProcessKiller.KillClients("Client");
        ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

        using var sc = ClientSession.Launch(config.SmartClientExe);
        var scLogin = LoginFlow.WaitForLoginWindow(sc);
        LoginFlow.Login(sc, scLogin, config);
        LoginFlow.AcceptCertificateTrust(sc);
        var scMain = LoginFlow.WaitForSmartClientMain(sc);
        SmartClientUi.Maximize(scMain);
        Thread.Sleep(2000);
        scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

        var restoreToggleToOff = false;
        try
        {
            scMain = SmartClientUi.SelectWorkspace(scMain, sc, config.WorkspaceName);
            SmartClientUi.WaitForMipViewItem(scMain, 60);

            // ---- setup phase 1: managed posture ----
            scMain = EnterSetupAndSelectTile(sc, scMain);
            var props = WaitForPropertiesControl(sc);

            var home = Require(props, "txtHomeAddress");
            Assert.False(home.IsEnabled, "the pinned Home address field must render disabled in a managed workspace");
            var homeValue = home.Patterns.Value.IsSupported ? home.Patterns.Value.Pattern.Value.Value : string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(homeValue), "the pinned home URL must still be DISPLAYED (read-only), not blanked out");
            _output.WriteLine($"home field disabled, pinned value shown: {homeValue}");

            var homeNotice = Require(props, "managedHomeNotice");
            var proxyNotice = Require(props, "managedProxyNotice");
            Assert.Equal(ManagedNoticeText, homeNotice.Name);
            Assert.Equal(ManagedNoticeText, proxyNotice.Name);
            Artifacts.Window(scMain, "sc-managed-locked-home");

            var lockCheck = Require(props, LockCheckboxId);
            Assert.True(lockCheck.IsEnabled, "a cosmetic checkbox must stay enabled in a managed workspace (per-instance editable)");

            // Per-instance override of an UNLOCKED setting: flip it on (Toggle pattern works even
            // when the pane scrolled the row out of view; no mouse needed).
            if (lockCheck.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On)
            {
                lockCheck.Patterns.Toggle.Pattern.Toggle();
                restoreToggleToOff = true;
                Thread.Sleep(1200);
            }
            Assert.Equal(ToggleState.On, lockCheck.Patterns.Toggle.Pattern.ToggleState.Value);
            _output.WriteLine("per-instance override stored: ClearBrowsingDataOnExit = on");

            // ---- setup phase 2: regenerate the pane through a full exit/re-enter setup cycle.
            // Leaving and re-entering setup re-runs GeneratePropertiesWpfUserControl, which
            // re-seeds from the admin definition (fingerprint unchanged) - the override must
            // survive and the locks must stay locked.
            SmartClientUi.ClickSetup(scMain);
            Thread.Sleep(2500);
            scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            scMain = EnterSetupAndSelectTile(sc, scMain);
            props = WaitForPropertiesControl(sc);

            var lockCheck2 = Require(props, LockCheckboxId);
            Assert.Equal(ToggleState.On, lockCheck2.Patterns.Toggle.Pattern.ToggleState.Value);
            _output.WriteLine("override survived the workspace re-seed (unlocked keys keep per-instance values)");

            var home2 = Require(props, "txtHomeAddress");
            Assert.False(home2.IsEnabled, "the Home address field must stay disabled after re-seeding");
            Assert.Equal(ManagedNoticeText, Require(props, "managedHomeNotice").Name);
            Assert.Equal(ManagedNoticeText, Require(props, "managedProxyNotice").Name);
            Artifacts.Window(scMain, "sc-managed-after-reseed");

            // The seeding pass logs its decision per view item; when the log is present the
            // marker must exist (missing logs are no proof, per the repo's log caveats).
            if (MipLogFilesPresent())
            {
                Assert.True(MipLogContains("WebContent workspace seed:"),
                    "Smart Client MIPLog must carry the workspace seed decision line");
                _output.WriteLine("MIPLog carries the 'WebContent workspace seed:' line");
            }
            else
            {
                _output.WriteLine("NOTE: no Smart Client MIPLog file readable this session; seed marker not asserted (missing logs are not evidence the plugin never ran).");
            }

            // ---- restore: put the per-instance override back to off. ----
            if (restoreToggleToOff)
            {
                lockCheck2.Patterns.Toggle.Pattern.Toggle();
                Thread.Sleep(1200);
                var props3 = WaitForPropertiesControl(sc);
                Assert.NotEqual(ToggleState.On, Require(props3, LockCheckboxId).Patterns.Toggle.Pattern.ToggleState.Value);
                _output.WriteLine("per-instance override restored to off");
            }
        }
        catch
        {
            var fresh = sc.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
            if (fresh != null)
            {
                Artifacts.Window(fresh, "sc-managed-locks-failure-state");
                Artifacts.WriteText("sc-managed-locks-failure-tree.txt", TreeDump.Of(fresh, 12));
            }
            throw;
        }
        finally
        {
            if (restoreToggleToOff)
            {
                try
                {
                    var pane = sc.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
                    var checkbox = pane?.FindFirstDescendant(cf => cf.ByAutomationId(LockCheckboxId));
                    if (checkbox != null && checkbox.Patterns.Toggle.IsSupported &&
                        checkbox.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On)
                    {
                        checkbox.Patterns.Toggle.Pattern.Toggle();
                    }
                }
                catch { }
            }
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }
    }

    /// <summary>Enters setup mode, opens the Properties side panel when closed, and clicks the
    /// top of the WebContent tile to select the gadget (same selection posture as the README
    /// screenshot run: near the top so focus never goes deep into the embedded browser).</summary>
    private Window EnterSetupAndSelectTile(ClientSession sc, Window scMain)
    {
        SmartClientUi.ClickSetup(scMain);
        Thread.Sleep(3000);
        scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

        if (!SmartClientUi.SidePanelsOpen(scMain))
            SmartClientUi.TryInvokeById(scMain, "PanesSetupProperties");
        Thread.Sleep(1500);
        scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

        var tile = SmartClientUi.WaitForMipViewItem(scMain, 30);
        var tr = tile.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point((int)(tr.X + tr.Width / 2), (int)(tr.Y + 15)));
        Thread.Sleep(2500);
        return sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
    }

    /// <summary>Waits for the plugin's properties UserControl to render into the setup Properties
    /// panel (it only exists once the gadget is selected) and returns it.</summary>
    private AutomationElement WaitForPropertiesControl(ClientSession sc)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var main = sc.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
            var props = main?.FindFirstDescendant(cf => cf.ByClassName("WebContentPropertiesUserControl"));
            if (props != null) return props;
            Thread.Sleep(1000);
        }
        throw new TimeoutException("The plugin's WebContentPropertiesUserControl never rendered in the setup Properties panel.");
    }

    private static AutomationElement Require(AutomationElement props, string automationId)
    {
        var element = props.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element == null)
            throw new InvalidOperationException($"properties-pane control '{automationId}' not found - the pane rendered without it.");
        return element;
    }

    private static bool MipLogFilesPresent()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Milestone", "XProtect Smart Client");
        return File.Exists(Path.Combine(root, "MIPLog.txt")) || Directory.Exists(Path.Combine(root, "MIPLogs"));
    }

    /// <summary>Searches the Smart Client MIP logs (MIPLog.txt plus rolling MIPLogs copies).</summary>
    private static bool MipLogContains(string marker)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Milestone", "XProtect Smart Client");
        var candidates = new List<string>();
        try
        {
            var top = Path.Combine(root, "MIPLog.txt");
            if (File.Exists(top)) candidates.Add(top);
            var rollDir = Path.Combine(root, "MIPLogs");
            if (Directory.Exists(rollDir)) candidates.AddRange(Directory.EnumerateFiles(rollDir, "MIPLog*.txt"));
        }
        catch { return false; }

        foreach (var file in candidates)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Contains(marker, StringComparison.Ordinal)) return true;
                }
            }
            catch { /* locked mid-write or unreadable: try the next file */ }
        }
        return false;
    }
}
