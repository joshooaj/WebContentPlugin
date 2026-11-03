using System.Text;

using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #85 probe: the per-workspace max-response-bytes cap, driven end to end through a live
/// Management Client against the live VMS. Five things get pinned by observation, not narration:
///
/// 1. DEFAULT POSTURE - with no stored cap the Event Server fetcher stops the same 6 MB download
///    at the 5 MB default and the MC harness shows the TooLarge frame NAMING the 5242880-byte
///    cap the refusal came from.
/// 2. RAISED CAP ENFORCED - saving 8 MB on the Proxy tab (audit line + reloaded-pane readback)
///    lets the SAME download through within the store's 60 s refresh window: the result flips
///    from TooLarge to "6291456 bytes fetched". That round trip also proves the client receive
///    guard was raised to the 20 MB protocol ceiling - a client still guarding at the old 5 MB
///    default would reject the 6 MB answer on the way back in.
/// 3. REFUSAL BY NAME - "99999" in the cap field refuses the WHOLE save: the plugin's notice
///    names the rejected text, the MC MIP.log gains the matching 'invalid proxy response cap'
///    marker, and a reloaded pane proves nothing reached storage (the cap still reads 8).
/// 4. LOWERING RE-ARMS - restoring the default cap brings TooLarge back at 5242880 within one
///    refresh window; the cap is live in both directions.
/// 5. STORAGE SHAPE - every accepted save writes the plugin's "Stored proxy response cap ..."
///    audit line with the effective MB value, "(default)" for 5 MB.
///
/// The probe fetches for real (proof.ovh.net's fixed-size 6 MB file), so it TEMPORARILY allowlists
/// that origin on the probe workspace and restores the workspace's own allowlist and cap before it
/// exits - verified from reloaded panes, and the MC-14 teardown discipline (RejectPendingModal)
/// keeps a crashed mid-run edit from persisting.
/// </summary>
public class McProxyResponseCapProbeTests
{
    private const string ProbeHost = "proof.ovh.net";
    private const string ProbeEntry = "http://proof.ovh.net";
    private const string ProbeFileUrl = "http://proof.ovh.net/files/6Mb.dat";

    /// <summary>The file's exact size and the default cap the refusal must name.</summary>
    private const string ProbeBodyBytes = "6291456";
    private const string DefaultCapBytes = "5242880";

    private const string RaisedCapMegabytes = "8";

    /// <summary>Substring of Strings.InvalidProxyCapMessage, the notice for a refused cap.</summary>
    private const string RefusalNotice = "is not a valid proxy response size";

    /// <summary>WorkspaceItemManager's own log marker for the same refusal.</summary>
    private const string RefusalLogMarker = "invalid proxy response cap";

    private readonly ITestOutputHelper _output;

    public McProxyResponseCapProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbePerWorkspaceResponseCapDefaultRaiseRefusalAndRestore()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-response-cap-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        AllowlistGridUi.Trace = message => log.AppendLine(message);
        Window mcMain = null;
        ClientSession mc = null;
        try
        {
            ProcessKiller.KillClients("VideoOS.Administration");
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

            // ---- Phase 0: baseline from the STORED record (cap field exists and reads it) -----
            AllowlistGridUi.OpenProxyTab(mcMain);
            var baselineCap = AllowlistGridUi.ReadResponseCapText(mcMain);
            log.AppendLine($"stored cap field baseline reads '{baselineCap}'");

            var rendered = AllowlistGridUi.ReadEntries(mcMain);
            var original = rendered
                .Where(e => !e.Split(' ')[0].Contains(ProbeHost, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (original.Count < rendered.Count)
                log.AppendLine($"baseline excludes {rendered.Count - original.Count} leftover probe-host row(s)");
            log.AppendLine($"stored allowlist baseline: {string.Join(" | ", original)}");

            // ---- Phase 1: allowlist the probe origin, cap untouched (stays the default) -------
            var probeLines = new List<string>(original) { ProbeEntry };
            AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, probeLines, log);

            // ---- Phase 2: default posture - 6 MB refused at the 5242880 default cap -----------
            var defaultRun = RunFetch(mcMain, ProbeFileUrl, log, 60);
            log.AppendLine($"default-cap fetch -> {defaultRun}");
            Assert.Contains("TooLarge", defaultRun, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(DefaultCapBytes, defaultRun, StringComparison.Ordinal);
            Artifacts.Window(mcMain, "mc-cap-default-toolarge");

            // ---- Phase 3: raise the cap to 8 MB (audit line + reloaded-pane readback) ---------
            AllowlistGridUi.SetCapAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, RaisedCapMegabytes, log);

            // ---- Phase 4: same download inside the 60 s refresh window now succeeds ------------
            // Polling the fetch itself is the proof: the Event Server store re-resolves workspace
            // caps at most every 60 s, and only the answer's SIZE can show which cap governed.
            var raisedDeadline = DateTime.UtcNow.AddSeconds(120);
            string raisedRun = null;
            while (DateTime.UtcNow < raisedDeadline)
            {
                raisedRun = RunFetch(mcMain, ProbeFileUrl, log, 60);
                if (raisedRun.Contains("bytes fetched", StringComparison.OrdinalIgnoreCase))
                    break;
                log.AppendLine($"raised-cap fetch still -> {raisedRun} (Event Server cap store refreshes within 60 s; re-polling)");
                Thread.Sleep(5000);
            }

            Assert.True(raisedRun != null && raisedRun.Contains("bytes fetched", StringComparison.OrdinalIgnoreCase),
                $"with an 8 MB stored cap the {ProbeBodyBytes}-byte download never succeeded within the refresh window (last: '{raisedRun}').");
            Assert.Contains(ProbeBodyBytes, raisedRun, StringComparison.Ordinal);
            log.AppendLine($"raised-cap fetch -> {raisedRun} (client guard raised to the protocol ceiling: the 6 MB answer reassembled)");
            Artifacts.Window(mcMain, "mc-cap-raised-success");

            // ---- Phase 5: an out-of-range cap REFUSES the whole save, by name -----------------
            AllowlistGridUi.OpenProxyTab(mcMain);
            AllowlistGridUi.SetResponseCapText(mcMain, "99999");
            var markersBefore = McLog.CountMatches(RefusalLogMarker);
            var notice = AllowlistGridUi.SaveAndCaptureNotice(mc, mcMain, RefusalNotice, log);
            Assert.True(notice != null,
                "saving cap '99999' raised no refusal notice - the save went through or the field vanished silently.");
            Assert.Contains("99999", notice, StringComparison.Ordinal);
            log.AppendLine($"refusal notice: {notice}");

            var markerDelta = 0;
            for (var attempt = 0; attempt < 10 && markerDelta == 0; attempt++)
            {
                Thread.Sleep(1000);
                markerDelta = McLog.CountMatches(RefusalLogMarker) - markersBefore;
            }

            Assert.True(markerDelta > 0,
                $"the MC MIP.log gained no '{RefusalLogMarker}' marker for the refused save ({markersBefore} -> {McLog.CountMatches(RefusalLogMarker)}).");

            // Nothing partial reached storage: the reloaded pane still reads the last stored cap.
            AllowlistGridUi.ReloadPane(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
            var afterRefusal = AllowlistGridUi.ReadResponseCapText(mcMain);
            Assert.Equal(RaisedCapMegabytes, afterRefusal);
            log.AppendLine($"after the refused save the reloaded pane still reads cap '{afterRefusal}' (nothing partial stored)");
            Artifacts.Window(mcMain, "mc-cap-refusal");

            // ---- Phase 6: lower back to the default - TooLarge re-arms within a refresh window
            AllowlistGridUi.SetCapAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, "5", log);
            var restoreDeadline = DateTime.UtcNow.AddSeconds(120);
            string restoredRun = null;
            while (DateTime.UtcNow < restoreDeadline)
            {
                restoredRun = RunFetch(mcMain, ProbeFileUrl, log, 60);
                if (restoredRun.Contains("TooLarge", StringComparison.OrdinalIgnoreCase)
                    && restoredRun.Contains(DefaultCapBytes, StringComparison.Ordinal))
                    break;
                log.AppendLine($"restore-cap fetch still -> {restoredRun} (waiting for the lowered cap to take effect)");
                Thread.Sleep(5000);
            }

            Assert.True(restoredRun != null
                    && restoredRun.Contains("TooLarge", StringComparison.OrdinalIgnoreCase)
                    && restoredRun.Contains(DefaultCapBytes, StringComparison.Ordinal),
                $"after restoring the default cap the {ProbeBodyBytes}-byte download never met TooLarge at {DefaultCapBytes} again (last: '{restoredRun}').");
            log.AppendLine($"restored-cap fetch -> {restoredRun} (cap live in both directions)");

            // ---- Restore: the workspace's own allowlist, verified from a reloaded pane --------
            AllowlistGridUi.SaveAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, original, log);
            AllowlistGridUi.OpenProxyTab(mcMain);
            Assert.DoesNotContain(ProbeHost, string.Join(" | ", AllowlistGridUi.ReadEntries(mcMain)), StringComparison.OrdinalIgnoreCase);
            var finalCap = AllowlistGridUi.ReadResponseCapText(mcMain);
            Assert.Equal(baselineCap, finalCap);
            log.AppendLine($"allowlist and cap ('{finalCap}') restored (verified from reloaded panes)");
            Artifacts.Window(mcMain, "mc-cap-restored");
        }
        finally
        {
            Artifacts.WriteText("mc-response-cap-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain?.Close(); } catch { }
            // MC-16 lesson: NEVER AcceptPendingModal at teardown - it would persist a mid-run
            // cap edit. Answer the shell's save prompt with No so the workspace keeps exactly
            // what the probe's own verified restores stored.
            if (mc != null) ManagementClientUi.RejectPendingModal(mc, 5);
            ProcessKiller.KillClients("VideoOS.Administration");
        }
    }

    /// <summary>One Proxy-tab harness fetch: set the URL, invoke, wait for a settled result label
    /// (the running placeholder is 'Fetching through ...'). Same reader discipline as McProxyFetchProbeTests.</summary>
    private static string RunFetch(Window mcMain, string url, StringBuilder log, int timeoutSeconds)
    {
        AllowlistGridUi.OpenProxyTab(mcMain);
        ManagementClientUi.SetValue(mcMain, "txtProxyTestUrl", url);
        var button = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("btnProxyTestFetch"))
            ?? throw new InvalidOperationException("btnProxyTestFetch not found.");

        var before = ResultText(mcMain);
        try { button.Patterns.Invoke.Pattern.Invoke(); }
        catch { button.Click(); }

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var text = ResultText(mcMain);
            if (!string.IsNullOrEmpty(text)
                && text != before
                && !text.StartsWith("Fetching through", StringComparison.OrdinalIgnoreCase))
                return text;
            Thread.Sleep(500);
        }

        return $"(timeout waiting for result; last text: '{ResultText(mcMain)}')";
    }

    private static string ResultText(Window mcMain) =>
        mcMain.FindFirstDescendant(cf => cf.ByAutomationId("lblProxyTestResult"))?.Name ?? string.Empty;
}
