using System.Text;

using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #81 probe: the Management Client's structured allowlist grid on the Proxy tab, driven
/// through a live MC against the live VMS. Four things get pinned, none of them by narration:
///
/// 1. The free-text one-line-per-entry box is GONE (AC: the accept-untrusted token is no longer
///    user-facing anywhere in the MC UI) - no txtProxyTargets control exists in the pane, and the
///    admin-facing hint no longer teaches a typed token.
/// 2. A refused entry is flagged ON ITS ROW (inline Problem cell) and the whole save is refused
///    BY NAME - the plugin's own notice names both bad rows, the MC MIP.log gains the matching
///    'invalid proxy target row(s)' marker, and the rows are NOT silently dropped from the grid.
///    The follow-up save proves nothing partial reached storage: the stored set afterwards is
///    exactly the fixed grid, with no trace of the refused rows.
/// 3. scheme / host / port / accept-untrusted round-trip through the STORED record: the readback
///    is taken from a RELOADED pane (re-selected item), so the flag reading On is the stored
///    record speaking, not the harness's own keystrokes.
/// 4. The full-host-wildcard warning banner is up exactly while a '*' row is in the grid, and the
///    per-row Remove button drops both the row and the banner.
///
/// The probe workspace's original allowlist is restored and verified before the probe exits.
/// Nothing here dials a probe origin: 'gridprobe.invalid' is a reserved, unroutable TLD, and the
/// probe never fetches it - it only proves what the admin page accepts, refuses and stores.
/// </summary>
public class McAllowlistGridProbeTests
{
    private const string ProbeHost = "gridprobe.invalid";
    private const string ProbeEntry = "https://gridprobe.invalid:8443";
    private const string ProbeEntryFlagged = "https://gridprobe.invalid:8443 accept-untrusted";
    private const string BadPatternHost = "foo.*.bar.com";
    private const string BadPortHost = "ok.gridprobe.invalid";
    private const string BadPort = "99999";
    private const string HatchEntry = "http://*";

    /// <summary>The two refused rows exactly as the ROW MODEL describes them (scheme and all). The
    /// refusal dialog and the refusal log marker both print that shape, so asserting these strings
    /// asserts the model's own scheme, not what the dropdown appeared to show.</summary>
    private const string BadPatternRow = "https://foo.*.bar.com";
    private const string BadPortRowEntry = "http://ok.gridprobe.invalid:99999";

    /// <summary>Substring of Strings.InvalidProxyTargetMessage, the notice the plugin's save path
    /// shows for a refused row.</summary>
    private const string RefusalNotice = "is not a valid proxy target";

    /// <summary>WorkspaceItemManager's own log marker for the same refusal.</summary>
    private const string RefusalLogMarker = "invalid proxy target row(s)";

    private readonly ITestOutputHelper _output;

    public McAllowlistGridProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeStructuredAllowlistGridEditsRefusesAndStores()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-allowlist-grid-probe"));
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

            // ---- Phase 0: the free-text editor is gone; rows render from the stored records ----
            var legacyBox = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("txtProxyTargets"));
            Assert.True(legacyBox == null,
                "a txtProxyTargets control exists in the live pane again - the free-text allowlist editor is back (#81 AC1).");

            AllowlistGridUi.OpenProxyTab(mcMain);
            var rendered = AllowlistGridUi.ReadEntries(mcMain);
            log.AppendLine($"stored allowlist rendered as {rendered.Count} row(s): {string.Join(" | ", rendered)}");
            Assert.True(rendered.Count > 0, "the probe workspace renders no allowlist rows at all - nothing to round-trip.");
            Assert.All(rendered, entry => Assert.Contains("://", entry, StringComparison.Ordinal));

            // A probe host row (or a full-host-wildcard hatch row) left over from an interrupted
            // earlier run would double up when the probe adds its own, and the storage de-dupes -
            // which reads as a save mismatch. The probe's baseline is the workspace's allowlist
            // minus anything it owns.
            var original = rendered
                .Where(e => !e.Split(' ')[0].Contains(ProbeHost, StringComparison.OrdinalIgnoreCase)
                            && !e.Split(' ')[0].EndsWith("://*", StringComparison.Ordinal))
                .ToList();
            if (original.Count < rendered.Count)
                log.AppendLine($"baseline excludes {rendered.Count - original.Count} leftover probe-host row(s): {string.Join(" | ", original)}");
            Assert.True(original.Count > 0, "once its own leftovers are excluded, the probe workspace renders no allowlist rows at all.");

            var hint = mcMain.FindFirstDescendant(cf => cf.ByAutomationId(AllowlistGridUi.TargetsHintId));
            Assert.NotNull(hint);
            Assert.DoesNotContain("accept-untrusted", hint.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            log.AppendLine("hint text teaches no accept-untrusted token (the flag is a column now)");

            var renderedHasHatch = rendered.Any(e => e.Split(' ')[0].EndsWith("://*", StringComparison.Ordinal));
            Assert.Equal(renderedHasHatch, AllowlistGridUi.WarningBannerVisible(mcMain));
            log.AppendLine($"baseline warning banner visible={AllowlistGridUi.WarningBannerVisible(mcMain)} (matches a hatch row being present={renderedHasHatch})");

            // ---- Phase 1: a refused row is flagged inline and refuses the save BY NAME -------
            // ---- Phase 1: a refused entry is flagged inline and refuses the save BY NAME -------
            // The bad rows deliberately straddle both schemes: the refusal dialog quotes every row
            // as the ROW MODEL describes it, so "http://...:99999" in that text is the model
            // speaking its own scheme - the one pre-save proof that the scheme dropdown really
            // reached the row (a dropdown that only LOOKED selected stores https and gets caught here).
            AllowlistGridUi.AddRow(mcMain);
            AllowlistGridUi.SetLastRow(mcMain, "https", BadPatternHost, null, false);
            AllowlistGridUi.AddRow(mcMain);
            AllowlistGridUi.SetLastRow(mcMain, "http", BadPortHost, BadPort, false);

            var rows = AllowlistGridUi.ReadRows(mcMain);
            Assert.Equal(rendered.Count + 2, rows.Count);
            var midLabelRow = SingleRowWithHost(rows, BadPatternHost);
            var badPortRow = SingleRowWithHost(rows, BadPortHost);
            Assert.False(string.IsNullOrWhiteSpace(midLabelRow.Problem),
                $"the mid-label wildcard row carries no inline Problem text: '{midLabelRow.Entry}'");
            Assert.False(string.IsNullOrWhiteSpace(badPortRow.Problem),
                $"the out-of-range port row carries no inline Problem text: '{badPortRow.Entry}'");
            log.AppendLine($"inline problems: '{midLabelRow.Problem}' / '{badPortRow.Problem}'");
            Artifacts.Window(mcMain, "mc-grid-refused-rows");

            var markersBefore = McLog.CountMatches(RefusalLogMarker, BadPatternHost);
            var notice = AllowlistGridUi.SaveAndCaptureNotice(mc, mcMain, RefusalNotice, log);
            Assert.True(notice != null,
                "saving two invalid rows raised no refusal notice - the save went through, or the rows vanished silently.");
            Assert.Contains(BadPatternRow, notice, StringComparison.Ordinal);
            Assert.Contains(BadPortRowEntry, notice, StringComparison.Ordinal);
            log.AppendLine($"refusal notice (each row as the row model describes it): {notice}");

            // Never silently dropped: the refused rows are still in the grid, still flagged.
            var afterRefusalRows = AllowlistGridUi.ReadRows(mcMain);
            Assert.Equal(rendered.Count + 2, afterRefusalRows.Count);
            Assert.False(string.IsNullOrWhiteSpace(SingleRowWithHost(afterRefusalRows, BadPatternHost).Problem));
            Assert.False(string.IsNullOrWhiteSpace(SingleRowWithHost(afterRefusalRows, BadPortHost).Problem));

            var markerDelta = 0;
            for (var attempt = 0; attempt < 10 && markerDelta == 0; attempt++)
            {
                Thread.Sleep(1000);
                markerDelta = McLog.CountMatches(RefusalLogMarker, BadPatternHost) - markersBefore;
            }

            Assert.True(markerDelta > 0,
                $"the Management Client MIP.log gained no '{RefusalLogMarker}' marker for the refused save ({markersBefore} -> {McLog.CountMatches(RefusalLogMarker, BadPatternHost)}).");
            log.AppendLine($"MIP.log refusal markers: {markersBefore} -> {McLog.CountMatches(RefusalLogMarker, BadPatternHost)} (the log marker names the refused rows)");

            // ---- Phase 2: fixing the rows saves; nothing partial reached storage --------------
            var fixedLines = new List<string>(original) { ProbeEntry };
            AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, fixedLines, log);
            var storedAfterFix = AllowlistGridUi.ReadEntries(mcMain);
            Assert.Contains(ProbeEntry, storedAfterFix, StringComparer.Ordinal);
            Assert.All(storedAfterFix, entry =>
            {
                Assert.DoesNotContain(BadPatternHost, entry, StringComparison.Ordinal);
                Assert.DoesNotContain(BadPortHost, entry, StringComparison.Ordinal);
            });
            log.AppendLine("the refused rows never reached storage: the reloaded grid carries only the fixed entry");

            // ---- Phase 3: scheme/host/port/accept-untrusted round-trip through the STORED record
            var flaggedLines = new List<string>(original) { ProbeEntryFlagged };
            AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, flaggedLines, log);
            var probeRow = SingleRowWithHost(AllowlistGridUi.ReadRows(mcMain), ProbeHost);
            Assert.Equal("https", probeRow.Scheme);
            Assert.Equal("8443", probeRow.Port.Trim());
            Assert.True(probeRow.Untrusted,
                $"the accept-untrusted flag never came back from the stored record (row reads '{probeRow.Entry}').");
            log.AppendLine($"stored row reads back: {probeRow.Entry} (flag survived save + reload)");

            // ---- Phase 4: the full-host-wildcard banner + the per-row Remove button -----------
            var hatchLines = new List<string>(original) { HatchEntry };
            AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, hatchLines, log);
            Assert.True(AllowlistGridUi.WarningBannerVisible(mcMain),
                $"with '{HatchEntry}' in the grid the full-host-wildcard warning banner never appeared.");

            var hatchIndex = AllowlistGridUi.ReadRows(mcMain).FindIndex(r => r.Host == "*");
            Assert.True(hatchIndex >= 0, $"the '{HatchEntry}' row never rendered into the grid.");
            AllowlistGridUi.RemoveRowAt(mcMain, hatchIndex);
            Assert.False(AllowlistGridUi.WarningBannerVisible(mcMain),
                "the warning banner stayed up after the hatch row's Remove button dropped the row.");

            // ---- Restore: the workspace's own allowlist, verified from a reloaded pane --------
            AllowlistGridUi.SaveAndVerify(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, original, log);
            Assert.False(AllowlistGridUi.WarningBannerVisible(mcMain), "the warning banner is up on a restored allowlist with no hatch row.");
            Assert.All(AllowlistGridUi.ReadEntries(mcMain), entry => Assert.DoesNotContain(ProbeHost, entry, StringComparison.Ordinal));
            log.AppendLine("allowlist restored (probe rows gone, verified from a reloaded pane)");

            Artifacts.Window(mcMain, "mc-grid-restored");
        }
        finally
        {
            Artifacts.WriteText("mc-allowlist-grid-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain?.Close(); } catch { }
            if (mc != null) ManagementClientUi.AcceptPendingModal(mc, 5);
            ProcessKiller.KillClients("VideoOS.Administration");
        }
    }

    private static AllowlistGridUi.RowReading SingleRowWithHost(List<AllowlistGridUi.RowReading> rows, string host)
    {
        var hit = rows.FirstOrDefault(r => string.Equals(r.Host.Trim(), host, StringComparison.OrdinalIgnoreCase));
        Assert.True(hit != null, $"no grid row has host '{host}' (rows: {string.Join(" | ", rows.Select(r => r.Entry))}).");
        return hit;
    }
}
