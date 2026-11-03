using System.Text;

using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #86 probe: the per-workspace proxy header policy, driven end to end through a live
/// Management Client against the live VMS. Four things get pinned by observation, not narration:
///
/// 1. STORE - a valid policy on BOTH legs (add/override row + strip row per grid) saves and is
///    read back from the plugin's "Stored proxy header policy ..." audit line AND from both grids
///    in a RELOADED pane, order-sensitively: configuration order is part of the policy (for one
///    header name the LAST rule wins, here and on the Event Server).
/// 2. FORBIDDEN GUARDRAIL - rows naming connection-level headers (Host on the request leg,
///    Proxy-Connection on the response leg) refuse the WHOLE save: the Problem cell names the
///    header while the row is typed, the plugin's notice names the refused row verbatim
///    ("Host=evil.example.com" / "-Host" for the strip variant), the MC MIP.log gains the
///    matching 'invalid proxy (request|response) header row(s)' marker - and NOTHING is stored
///    (no new audit line). Ticking Strip does NOT unlock a forbidden name: the guardrail is on
///    the name, not on the direction. Both legs, both variants.
/// 3. UNTOUCHED STORAGE - the refusals change nothing: the grids still display the stored policy
///    and the audit line count never moves while the refused rows sit in the pane.
/// 4. RESTORE - an emptied grid pair stores the EMPTY policy ("request 0 rule(s) []; response
///    0 rule(s) []") the same verified way, leaving the workspace in the pre-#86 posture.
///
/// The live FETCH effect of the policy (headers actually added/stripped against a real target)
/// is pinned by the unit suite (ProxyHeaderPolicyTests + the ServeFetchAsync wiring guards), not
/// here: the Proxy-tab fetch result label reports status/bytes/elapsed only - it cannot show
/// headers through UIA - so this probe pins the UI, the guardrail, and the stored shape, and the
/// Event Server's own refresh-audit line was verified live at deploy time.
/// </summary>
public class McProxyHeaderPolicyProbeTests
{
    /// <summary>The valid policy this probe stores on both legs (add/override + strip per grid).</summary>
    private static HeaderGridUi.Row RequestAdd => new("X-Probe-Request", "probe-request-value", false);
    private static HeaderGridUi.Row RequestStrip => new("X-Probe-Strip-Request", string.Empty, true);
    private static HeaderGridUi.Row ResponseAdd => new("X-Probe-Response", "probe-response-value", false);
    private static HeaderGridUi.Row ResponseStrip => new("X-Probe-Strip-Response", string.Empty, true);

    /// <summary>Forbidden names, one per leg: Host (request side) and a Proxy-* prefix header
    /// (response side) - together they pin both the exact-name set and the Proxy-* rule.</summary>
    private const string ForbiddenRequestHeader = "Host";
    private const string ForbiddenRequestValue = "evil.example.com";
    private const string ForbiddenResponseHeader = "Proxy-Connection";
    private const string ForbiddenResponseValue = "close";

    /// <summary>Substring of Strings.ProxyHeaderRowErrorForbidden, the Problem-column text.</summary>
    private const string ForbiddenProblemFragment = "reserved for the proxy connection";

    /// <summary>Substring of Strings.InvalidProxyHeaderMessage, the whole-save refusal notice.</summary>
    private const string RefusalNotice = "invalid row(s)";

    /// <summary>WorkspaceItemManager's own log markers for the same refusals.</summary>
    private const string RequestRefusalMarker = "invalid proxy request header row(s)";
    private const string ResponseRefusalMarker = "invalid proxy response header row(s)";

    private readonly ITestOutputHelper _output;

    public McProxyHeaderPolicyProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbePerWorkspaceHeaderPolicyStoreRefusalAndRestore()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-header-policy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        HeaderGridUi.Trace = message => log.AppendLine(message);
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

            var storedMarker = $"Stored proxy header policy for workspace '{config.McWorkspaceName}'";

            // ---- Phase 0: baseline from the STORED record (both grids exist and read it) -----
            AllowlistGridUi.OpenProxyTab(mcMain);
            var baselineRequest = HeaderGridUi.ReadRows(mcMain, HeaderGridUi.RequestGridId);
            var baselineResponse = HeaderGridUi.ReadRows(mcMain, HeaderGridUi.ResponseGridId);
            log.AppendLine($"baseline grids: request=[{string.Join(" | ", baselineRequest.Select(r => r.Describe()))}] response=[{string.Join(" | ", baselineResponse.Select(r => r.Describe()))}]");

            // Leftover rows from an interrupted earlier run would silently change what "empty
            // policy" means later (the #17 cap-normalization lesson): normalize to the pre-#86
            // posture first so the run is idempotent.
            if (baselineRequest.Count > 0 || baselineResponse.Count > 0)
            {
                log.AppendLine("baseline grids carry rows - normalizing to the empty policy for an idempotent run");
                HeaderGridUi.RemoveAllRows(mcMain, HeaderGridUi.RequestGridId);
                HeaderGridUi.RemoveAllRows(mcMain, HeaderGridUi.ResponseGridId);
                HeaderGridUi.SavePoliciesAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName,
                    Enumerable.Empty<HeaderGridUi.Row>(), Enumerable.Empty<HeaderGridUi.Row>(), log);
            }

            // ---- Phase 1: a valid policy on BOTH legs stores and reloads ----------------------
            AllowlistGridUi.OpenProxyTab(mcMain);
            HeaderGridUi.SetRows(mcMain, HeaderGridUi.RequestGridId, new[] { RequestAdd, RequestStrip });
            HeaderGridUi.SetRows(mcMain, HeaderGridUi.ResponseGridId, new[] { ResponseAdd, ResponseStrip });
            HeaderGridUi.SavePoliciesAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName,
                new[] { RequestAdd, RequestStrip }, new[] { ResponseAdd, ResponseStrip }, log);
            Artifacts.Window(mcMain, "mc-headers-stored");

            var auditAfterStore = McLog.CountMatches(storedMarker);

            // ---- Phase 2: request-leg forbidden name refuses the whole save, by name ----------
            RefuseForbiddenRow(mc, mcMain, config, log, storedMarker,
                HeaderGridUi.RequestGridId, RequestRefusalMarker,
                new HeaderGridUi.Row(ForbiddenRequestHeader, ForbiddenRequestValue, false),
                $"{ForbiddenRequestHeader}={ForbiddenRequestValue}");

            // ---- Phase 3: strip does NOT unlock a forbidden name (request leg) ----------------
            RefuseForbiddenRow(mc, mcMain, config, log, storedMarker,
                HeaderGridUi.RequestGridId, RequestRefusalMarker,
                new HeaderGridUi.Row(ForbiddenRequestHeader, string.Empty, true),
                $"-{ForbiddenRequestHeader}");

            // ---- Phase 4: response-leg forbidden name (Proxy-* prefix) refuses too -------------
            RefuseForbiddenRow(mc, mcMain, config, log, storedMarker,
                HeaderGridUi.ResponseGridId, ResponseRefusalMarker,
                new HeaderGridUi.Row(ForbiddenResponseHeader, ForbiddenResponseValue, false),
                $"{ForbiddenResponseHeader}={ForbiddenResponseValue}");

            // ---- Phase 5: strip does NOT unlock on the response leg either ---------------------
            RefuseForbiddenRow(mc, mcMain, config, log, storedMarker,
                HeaderGridUi.ResponseGridId, ResponseRefusalMarker,
                new HeaderGridUi.Row(ForbiddenResponseHeader, string.Empty, true),
                $"-{ForbiddenResponseHeader}");

            // ---- Phase 6: storage untouched through every refusal ------------------------------
            // The refused rows came back out of the grids; what remains displayed is exactly the
            // stored policy, and no refusal wrote a thing.
            AllowlistGridUi.OpenProxyTab(mcMain);
            var untouchedRequest = HeaderGridUi.ReadRows(mcMain, HeaderGridUi.RequestGridId);
            var untouchedResponse = HeaderGridUi.ReadRows(mcMain, HeaderGridUi.ResponseGridId);
            Assert.True(HeaderGridUi.SameRows(new[] { RequestAdd, RequestStrip }, untouchedRequest),
                $"after the refusals the request grid reads '{string.Join(" | ", untouchedRequest.Select(r => r.Describe()))}' — the stored policy should be untouched.");
            Assert.True(HeaderGridUi.SameRows(new[] { ResponseAdd, ResponseStrip }, untouchedResponse),
                $"after the refusals the response grid reads '{string.Join(" | ", untouchedResponse.Select(r => r.Describe()))}' — the stored policy should be untouched.");
            var auditAfterRefusals = McLog.CountMatches(storedMarker);
            Assert.Equal(auditAfterStore, auditAfterRefusals);
            log.AppendLine($"storage untouched through all four refusals: audit lines {auditAfterStore} -> {auditAfterRefusals}");
            Artifacts.Window(mcMain, "mc-headers-untouched");

            // ---- Phase 7: empty grids store the EMPTY policy (pre-#86 posture) -----------------
            HeaderGridUi.SetRows(mcMain, HeaderGridUi.RequestGridId, Enumerable.Empty<HeaderGridUi.Row>());
            HeaderGridUi.SetRows(mcMain, HeaderGridUi.ResponseGridId, Enumerable.Empty<HeaderGridUi.Row>());
            HeaderGridUi.SavePoliciesAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName,
                Enumerable.Empty<HeaderGridUi.Row>(), Enumerable.Empty<HeaderGridUi.Row>(), log);
            Artifacts.Window(mcMain, "mc-headers-restored");
        }
        finally
        {
            Artifacts.WriteText("mc-header-policy-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain?.Close(); } catch { }
            // MC-16 lesson: NEVER AcceptPendingModal at teardown - it would persist a mid-run
            // edit. Answer the shell's save prompt with No so the workspace keeps exactly what
            // the probe's own verified restores stored.
            if (mc != null) ManagementClientUi.RejectPendingModal(mc, 5);
            ProcessKiller.KillClients("VideoOS.Administration");
        }
    }

    /// <summary>
    /// One forbidden-row cycle: append the row, pin the live Problem-cell explanation, press
    /// Save and capture the plugin's refusal notice NAMING the row verbatim, pin the matching
    /// MIP.log marker, pin that NOTHING was stored (no new audit line), then take the refused
    /// row back out of the grid.
    /// </summary>
    private static void RefuseForbiddenRow(
        ClientSession mc,
        Window mcMain,
        UxConfig config,
        StringBuilder log,
        string storedMarker,
        string gridId,
        string refusalMarker,
        HeaderGridUi.Row forbidden,
        string expectedNamedRow)
    {
        AllowlistGridUi.OpenProxyTab(mcMain);
        HeaderGridUi.AddRow(mcMain, gridId);
        var index = HeaderGridUi.RowCount(mcMain, gridId) - 1;
        HeaderGridUi.SetLastRow(mcMain, gridId, forbidden);

        var problem = HeaderGridUi.ReadProblem(mcMain, gridId, index);
        Assert.Contains(ForbiddenProblemFragment, problem, StringComparison.OrdinalIgnoreCase);
        log.AppendLine($"{gridId} forbidden row '{forbidden.Describe()}' shows Problem: '{problem}'");

        var markersBefore = McLog.CountMatches(refusalMarker, $"'{config.McWorkspaceName}'");
        var auditBefore = McLog.CountMatches(storedMarker);

        var notice = AllowlistGridUi.SaveAndCaptureNotice(mc, mcMain, RefusalNotice, log);
        Assert.True(notice != null,
            $"saving the forbidden row '{forbidden.Describe()}' raised no refusal notice - the save went through or the row vanished silently.");
        Assert.Contains(expectedNamedRow, notice, StringComparison.Ordinal);
        log.AppendLine($"refusal notice (names the row verbatim): {notice}");

        var markerDelta = 0;
        for (var attempt = 0; attempt < 10 && markerDelta == 0; attempt++)
        {
            Thread.Sleep(1000);
            markerDelta = McLog.CountMatches(refusalMarker, $"'{config.McWorkspaceName}'") - markersBefore;
        }

        Assert.True(markerDelta > 0,
            $"the MC MIP.log gained no '{refusalMarker}' marker for the refused row '{forbidden.Describe()}' ({markersBefore} -> {McLog.CountMatches(refusalMarker, $"'{config.McWorkspaceName}'")}).");

        Thread.Sleep(2000);
        var auditAfter = McLog.CountMatches(storedMarker);
        Assert.Equal(auditBefore, auditAfter);
        log.AppendLine($"nothing stored by the refusal: '{storedMarker}' lines {auditBefore} -> {auditAfter}");

        HeaderGridUi.RemoveRowAt(mcMain, gridId, index);
        log.AppendLine($"forbidden row '{forbidden.Describe()}' removed from {gridId}");
        Artifacts.Window(mcMain, $"mc-headers-refusal-{gridId}-{expectedNamedRow.TrimStart('-')}");
    }
}
