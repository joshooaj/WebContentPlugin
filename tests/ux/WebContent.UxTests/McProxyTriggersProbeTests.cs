using System.Text;

using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #78 probe: the per-workspace proxy RULE TRIGGER grid, driven end to end through a live
/// Management Client against the live VMS. Four things get pinned by observation, not narration:
///
/// 1. STORE - two valid rows save and are read back from the plugin's "Stored proxy triggers ..."
///    audit line AND from a RELOADED grid, order-sensitively: configuration order is part of the
///    policy (the Event Server's matcher evaluates top-down and raises at most the FIRST matching
///    row's event, here and on the server). The read-only Event raised column shows the fixed
///    event type ("WebContent proxy request") on EVERY row - the one event type the whole plugin
///    raises - including after a reload.
/// 2. GRAMMAR GUARDRAIL - a row outside the grammar refuses the WHOLE save: the Problem cell names
///    the failing member while the row is typed, the plugin's notice names the refused row
///    verbatim ("url=... method=... status=..."), and the MC MIP.log gains the matching 'invalid
///    proxy trigger row(s)' marker - and NOTHING is stored (no new audit line). Three variants:
///    a mid-host wildcard url (outside the *.tail grammar), a status outside 1xx-5xx/100-599, and
///    a method chosen with no url at all (a verb without a url is a refusal, not a blank row).
///    The Problem text and the refusal posture are the SAME grammar the Event Server's matcher
///    applies to stored rows (ProxyTriggerPolicy), pinned against drift by the unit suite.
/// 3. UNTOUCHED STORAGE - the refusals change nothing: the grid still displays the stored rows and
///    the audit line count never moves while the refused rows sit in the pane.
/// 4. RESTORE - an emptied grid stores NO trigger section ("0 row(s) []"), leaving the workspace
///    in the pre-#78 posture (no configured triggers).
///
/// The live EVENT effect (a matching fetch actually raising the XProtect event) is pinned by the
/// unit suite (ProxyTriggerMatcherTests) and was verified on the Event Server at the #65 deploy
/// (self-test probe, ES-3); this probe pins the MC UI, the guardrail, and the stored shape.
/// </summary>
public class McProxyTriggersProbeTests
{
    /// <summary>The valid two-row policy this probe stores - deliberately different shapes so the
    /// audit line pins path globs, an exact status, a host wildcard, a class status, and the
    /// any-method dropdown state side by side.</summary>
    private static TriggerGridUi.Row RowExact => new("http://probe.triggers.example.com/videos/*", "GET", "404");

    private static TriggerGridUi.Row RowWildcard => new("https://*.probe.triggers.example.com/*", "*", string.Empty);

    /// <summary>Outside-grammar rows, one per failing member: a mid-host wildcard (the host
    /// wildcard grammar is exact / *.tail / lone *), status 600 (no 6xx class, outside 100-599),
    /// and a POST filter with no url at all.</summary>
    private static TriggerGridUi.Row BadUrlRow => new("http://foo.*.bar.com/", "*", string.Empty);

    private static TriggerGridUi.Row BadStatusRow => new("http://probe.triggers.example.com/ok", "*", "600");

    private static TriggerGridUi.Row NoUrlRow => new(string.Empty, "POST", string.Empty);

    /// <summary>Substrings of the Problem-column strings, per failing member.</summary>
    private const string UrlProblemFragment = "is not a valid trigger URL pattern";
    private const string StatusProblemFragment = "is not a status filter";
    private const string UrlRequiredFragment = "URL pattern is required";

    /// <summary>Substring of Strings.InvalidProxyTriggerMessage, the whole-save refusal notice.</summary>
    private const string RefusalNotice = "invalid row(s)";

    /// <summary>WorkspaceItemManager's own log marker for the refusals.</summary>
    private const string RefusalMarker = "invalid proxy trigger row(s)";

    /// <summary>ProxyTriggerPolicy.EventType - the fixed event type the read-only Event column shows.</summary>
    private const string EventType = "WebContent proxy request";

    private readonly ITestOutputHelper _output;

    public McProxyTriggersProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeProxyRuleTriggersStoreRefusalAndRestore()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-proxy-triggers-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        TriggerGridUi.Trace = message => log.AppendLine(message);
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

            var storedMarker = $"Stored proxy triggers for workspace '{config.McWorkspaceName}'";

            // ---- Phase 0: baseline from the STORED record (the grid exists and reads it) --------
            AllowlistGridUi.OpenProxyTab(mcMain);
            var baseline = TriggerGridUi.ReadRows(mcMain);
            log.AppendLine($"baseline grid: [{string.Join(" | ", baseline.Select(r => r.Describe()))}]");

            // Leftover rows from an interrupted earlier run would silently change what "no
            // triggers" means later (the #17 cap-normalization lesson): normalize to the empty
            // posture first so the run is idempotent.
            if (baseline.Count > 0)
            {
                log.AppendLine("baseline grid carries rows - normalizing to no triggers for an idempotent run");
                TriggerGridUi.RemoveAllRows(mcMain);
                TriggerGridUi.SaveTriggersAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName,
                    Enumerable.Empty<TriggerGridUi.Row>(), log);
            }

            // ---- Phase 1: two valid rows store and reload, order-sensitive, Event column shown --
            AllowlistGridUi.OpenProxyTab(mcMain);
            TriggerGridUi.SetRows(mcMain, new[] { RowExact, RowWildcard });
            var typed = TriggerGridUi.ReadRows(mcMain);
            foreach (var row in typed)
            {
                Assert.Equal(EventType, row.Event);
            }

            log.AppendLine($"every typed row shows the read-only Event column: '{EventType}'");
            TriggerGridUi.SaveTriggersAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName,
                new[] { RowExact, RowWildcard }, log);
            var reloaded = TriggerGridUi.ReadRows(mcMain);
            foreach (var row in reloaded)
            {
                Assert.Equal(EventType, row.Event);
            }

            log.AppendLine($"Event column survives the reload: every row reads '{EventType}'");
            Artifacts.Window(mcMain, "mc-triggers-stored");

            var auditAfterStore = McLog.CountMatches(storedMarker);

            // ---- Phase 2: mid-host wildcard url refuses the whole save, by name -----------------
            RefuseInvalidRow(mc, mcMain, config, log, storedMarker, BadUrlRow, UrlProblemFragment,
                "url=http://foo.*.bar.com/ method=any status=any");

            // ---- Phase 3: status 600 (outside the class/exact grammar) refuses too --------------
            RefuseInvalidRow(mc, mcMain, config, log, storedMarker, BadStatusRow, StatusProblemFragment,
                "url=http://probe.triggers.example.com/ok method=any status=600");

            // ---- Phase 4: a method with no url is a refusal, not a blank row --------------------
            RefuseInvalidRow(mc, mcMain, config, log, storedMarker, NoUrlRow, UrlRequiredFragment,
                "method=POST");

            // ---- Phase 5: storage untouched through every refusal --------------------------------
            // The refused rows came back out of the grid; what remains displayed is exactly the
            // stored policy, and no refusal wrote a thing.
            AllowlistGridUi.OpenProxyTab(mcMain);
            var untouched = TriggerGridUi.ReadRows(mcMain);
            Assert.True(TriggerGridUi.SameRows(new[] { RowExact, RowWildcard }, untouched),
                $"after the refusals the trigger grid reads '{string.Join(" | ", untouched.Select(r => r.Describe()))}' — the stored policy should be untouched.");
            var auditAfterRefusals = McLog.CountMatches(storedMarker);
            Assert.Equal(auditAfterStore, auditAfterRefusals);
            log.AppendLine($"storage untouched through all three refusals: audit lines {auditAfterStore} -> {auditAfterRefusals}");
            Artifacts.Window(mcMain, "mc-triggers-untouched");

            // ---- Phase 6: empty grid stores NO trigger section (pre-#78 posture) -----------------
            TriggerGridUi.SetRows(mcMain, Enumerable.Empty<TriggerGridUi.Row>());
            TriggerGridUi.SaveTriggersAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName,
                Enumerable.Empty<TriggerGridUi.Row>(), log);
            Artifacts.Window(mcMain, "mc-triggers-restored");
        }
        finally
        {
            Artifacts.WriteText("mc-proxy-triggers-probe-results.txt", log.ToString());
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
    /// One invalid-row cycle: append the row, pin the live Problem-cell explanation NAMING the
    /// failing member, press Save and capture the plugin's refusal notice NAMING the row verbatim,
    /// pin the matching MIP.log marker, pin that NOTHING was stored (no new audit line), then take
    /// the refused row back out of the grid.
    /// </summary>
    private static void RefuseInvalidRow(
        ClientSession mc,
        Window mcMain,
        UxConfig config,
        StringBuilder log,
        string storedMarker,
        TriggerGridUi.Row invalid,
        string problemFragment,
        string expectedNamedFragment)
    {
        AllowlistGridUi.OpenProxyTab(mcMain);
        TriggerGridUi.AddRow(mcMain);
        var index = TriggerGridUi.RowCount(mcMain) - 1;
        TriggerGridUi.SetLastRow(mcMain, invalid);

        var problem = TriggerGridUi.ReadProblem(mcMain, index);
        Assert.Contains(problemFragment, problem, StringComparison.OrdinalIgnoreCase);
        log.AppendLine($"invalid row '{invalid.Describe()}' shows Problem: '{problem}'");

        var markersBefore = McLog.CountMatches(RefusalMarker, $"'{config.McWorkspaceName}'");
        var auditBefore = McLog.CountMatches(storedMarker);

        var notice = AllowlistGridUi.SaveAndCaptureNotice(mc, mcMain, RefusalNotice, log);
        Assert.True(notice != null,
            $"saving the invalid row '{invalid.Describe()}' raised no refusal notice - the save went through or the row vanished silently.");
        Assert.Contains(expectedNamedFragment, notice, StringComparison.Ordinal);
        log.AppendLine($"refusal notice (names the row verbatim): {notice}");

        var markerDelta = 0;
        for (var attempt = 0; attempt < 10 && markerDelta == 0; attempt++)
        {
            Thread.Sleep(1000);
            markerDelta = McLog.CountMatches(RefusalMarker, $"'{config.McWorkspaceName}'") - markersBefore;
        }

        Assert.True(markerDelta > 0,
            $"the MC MIP.log gained no '{RefusalMarker}' marker for the refused row '{invalid.Describe()}' ({markersBefore} -> {McLog.CountMatches(RefusalMarker, $"'{config.McWorkspaceName}'")}).");

        Thread.Sleep(2000);
        var auditAfter = McLog.CountMatches(storedMarker);
        Assert.Equal(auditBefore, auditAfter);
        log.AppendLine($"nothing stored by the refusal: '{storedMarker}' lines {auditBefore} -> {auditAfter}");

        TriggerGridUi.RemoveRowAt(mcMain, index);
        log.AppendLine($"invalid row '{invalid.Describe()}' removed from the trigger grid");
        Artifacts.Window(mcMain, $"mc-triggers-refusal-{index}-{problemFragment.Split(' ').First()}");
    }
}
