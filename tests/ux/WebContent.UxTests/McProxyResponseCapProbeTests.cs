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
/// 1. DEFAULT POSTURE - with no stored cap the Event Server fetcher stops the same 10 MB download
///    at the 5 MB default and the MC harness shows the TooLarge frame NAMING the 5242880-byte
///    cap the refusal came from.
/// 2. RAISED CAP ENFORCED - saving 12 MB on the Proxy tab (audit line + reloaded-pane readback)
///    lets the SAME download through within the store's 60 s refresh window: the result flips
///    from TooLarge to "10485760 bytes fetched". That round trip also proves the client receive
///    guard was raised to the 20 MB protocol ceiling - a client still guarding at the old 5 MB
///    default would reject the 10 MB answer on the way back in.
/// 3. REFUSAL BY NAME - "99999" in the cap field refuses the WHOLE save: the plugin's notice
///    names the rejected text, the MC MIP.log gains the matching 'invalid proxy response cap'
///    marker, and storage stays untouched - no new cap audit line, and the Event Server keeps
///    serving the last stored cap while the pane still displays the refused edit.
/// 4. LOWERING RE-ARMS - restoring the default cap brings TooLarge back at 5242880 within one
///    refresh window; the cap is live in both directions.
/// 5. STORAGE SHAPE - every accepted save writes the plugin's "Stored proxy response cap ..."
///    audit line with the effective MB value, "(default)" for 5 MB.
///
/// The probe fetches for real (proof.ovh.net's fixed-size 10 MB file), so it TEMPORARILY allowlists
/// that origin on the probe workspace and restores the workspace's own allowlist and cap before it
/// exits - verified from reloaded panes, and the MC-14 teardown discipline (RejectPendingModal)
/// keeps a crashed mid-run edit from persisting.
/// </summary>
public class McProxyResponseCapProbeTests
{
    private const string ProbeHost = "proof.ovh.net";

    /// <summary>https, not http: proof.ovh.net answers 301 on the plain-http URL (learned live on
    /// the first MC-17 run - 'HTTP 301 - 162 bytes fetched' instead of the body), and the fetch
    /// harness does not follow cross-scheme redirects. ES-side https fetching is first-class
    /// (#56 TLS modes), and this host carries a properly chained certificate, so the plain
    /// ValidateNormally path succeeds. 10Mb.dat, not 6Mb.dat: the 6 MB file is gone (404 on the
    /// second live run) while 10Mb.dat is the host's fixed-size classic.</summary>
    private const string ProbeEntry = "https://proof.ovh.net";
    private const string ProbeFileUrl = "https://proof.ovh.net/files/10Mb.dat";

    /// <summary>The file's exact size and the default cap the refusal must name.</summary>
    private const string ProbeBodyBytes = "10485760";
    private const string DefaultCapBytes = "5242880";

    /// <summary>Comfortably above the 10 MB file, comfortably below the 20 MB ceiling.</summary>
    private const string RaisedCapMegabytes = "12";

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

            // A raised cap equal to THIS probe's raise value is leftover from an interrupted
            // earlier run (the same self-healing the allowlist does for probe-host rows): the
            // pristine baseline is the default.
            if (string.Equals(baselineCap, RaisedCapMegabytes, StringComparison.Ordinal))
            {
                log.AppendLine("baseline cap equals this probe's raise value - treating it as leftover and normalizing to the default on restore");
                baselineCap = "5";
            }

            var rendered = AllowlistGridUi.ReadEntries(mcMain);
            var original = rendered
                .Where(e => !e.Split(' ')[0].Contains(ProbeHost, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (original.Count < rendered.Count)
                log.AppendLine($"baseline excludes {rendered.Count - original.Count} leftover probe-host row(s)");
            log.AppendLine($"stored allowlist baseline: {string.Join(" | ", original)}");

            // ---- Phase 1: allowlist the probe origin AND normalize the cap to the default ------
            // One save writes both halves of the pane, and normalizing the cap here makes the run
            // idempotent: an earlier interrupted run may have LEFT a raised stored cap (learned
            // live - the refusal-phase crash left 12 stored), which would silently invalidate
            // Phase 2's default-posture expectation.
            var probeLines = new List<string>(original) { ProbeEntry };
            AllowlistGridUi.OpenProxyTab(mcMain);
            AllowlistGridUi.SetEntries(mcMain, probeLines);
            AllowlistGridUi.SetResponseCapText(mcMain, "5");
            AllowlistGridUi.SaveAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, probeLines, log);
            var provisionedCap = AllowlistGridUi.ReadResponseCapText(mcMain);
            Assert.Equal("5", provisionedCap);
            log.AppendLine($"provisioned: allowlist + normalized cap '{provisionedCap}' (one save, verified from the audit line AND a reloaded pane)");

            // ---- Phase 2: default posture - 10 MB refused at the 5242880 default cap ----------
            var defaultRun = RunFetch(mcMain, ProbeFileUrl, log, 60);
            log.AppendLine($"default-cap fetch -> {defaultRun}");
            Assert.Contains("TooLarge", defaultRun, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(DefaultCapBytes, defaultRun, StringComparison.Ordinal);
            Artifacts.Window(mcMain, "mc-cap-default-toolarge");

            // ---- Phase 3: raise the cap to 12 MB (audit line + reloaded-pane readback) --------
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
                $"with a {RaisedCapMegabytes} MB stored cap the {ProbeBodyBytes}-byte download never succeeded within the refresh window (last: '{raisedRun}').");
            Assert.Contains(ProbeBodyBytes, raisedRun, StringComparison.Ordinal);
            log.AppendLine($"raised-cap fetch -> {raisedRun} (client guard raised to the protocol ceiling: the 10 MB answer reassembled)");
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

            // Nothing partial reached storage, proven server-side rather than from the pane:
            // (a) the save audit line gained no new "Stored proxy response cap" entry at all, and
            // (b) the Event Server keeps SERVING the last stored cap - the 10 MB download still
            // succeeds while the pane still displays the refused 99999 edit. (Reading the pane
            // instead would be a lie here: MC's reselect cycle runs the platform fill through a
            // state where its dirty chain throws 'Not valid to set dirty state at this time', and
            // a TextBox interrupted mid-fill keeps the typed text - MC-17 live lesson.)
            var capAuditMarker = $"Stored proxy response cap for workspace '{config.McWorkspaceName}'";
            var capAuditBefore = McLog.CountMatches(capAuditMarker);
            var serverRun = RunFetch(mcMain, ProbeFileUrl, log, 60);
            var capAuditAfter = McLog.CountMatches(capAuditMarker);
            log.AppendLine($"cap audit lines after the refused save: {capAuditBefore} -> {capAuditAfter}");
            Assert.Equal(capAuditBefore, capAuditAfter);
            Assert.Contains("bytes fetched", serverRun, StringComparison.OrdinalIgnoreCase);
            log.AppendLine($"post-refusal fetch -> {serverRun} (the stored cap still governs: nothing partial stored)");
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
            // SaveAndVerify stores what the grid SHOWS and verifies it against the expected set,
            // so the probe's own row has to come out of the grid first.
            AllowlistGridUi.OpenProxyTab(mcMain);
            AllowlistGridUi.SetEntries(mcMain, original);
            AllowlistGridUi.SaveAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, original, log);
            AllowlistGridUi.OpenProxyTab(mcMain);
            Assert.DoesNotContain(ProbeHost, string.Join(" | ", AllowlistGridUi.ReadEntries(mcMain)), StringComparison.OrdinalIgnoreCase);

            // A workspace whose own cap is NOT the default gets its own value back the same
            // verified way (today's probe workspaces all sit at the default, so this is a no-op
            // save-skip in practice).
            if (!string.Equals(AllowlistGridUi.ReadResponseCapText(mcMain), baselineCap, StringComparison.Ordinal))
                AllowlistGridUi.SetCapAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, baselineCap, log);
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
