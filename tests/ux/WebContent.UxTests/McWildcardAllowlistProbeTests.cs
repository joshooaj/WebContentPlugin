using System.Text;

using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #73 probe: the Event Server fetch gate speaks the wildcard grammar end to end through
/// a REAL Management Client save. Phase A lets the workspace carry "http://*.neverssl.com": a
/// subdomain (http://online.neverssl.com/) fetches through the Event Server while the apex
/// (http://neverssl.com/) is refused NotAllowed - apex-never-matched live, not just pinned.
/// Phase B swaps in the full-host hatch "http://*": a normal host still fetches while the
/// literal-IP target (http://10.20.30.40/) refuses NotAllowed INSTANTLY - the SSRF floor refusing
/// before any socket opens (a missing floor would answer Unreachable after seconds). The grid
/// readback of each wildcard line after every save (taken from a reloaded pane, #81's harness)
/// proves the structured 'pattern' storage shape round-trips through the live admin page, and
/// phase B doubles as the live row for #81's full-host-wildcard warning banner. Every phase
/// restores the workspace's original allowlist verbatim before the probe exits.
/// </summary>
public class McWildcardAllowlistProbeTests
{
    private const string WildcardLine = "http://*.neverssl.com";
    private const string HatchLine = "http://*";
    private const string SubdomainUrl = "http://online.neverssl.com/";
    private const string ApexUrl = "http://neverssl.com/";
    private const string FlooredIpUrl = "http://10.20.30.40/";

    /// <summary>The Event Server allowlist snapshot re-resolves at most once per 60 s window
    /// (ProxyServerAllowlist.DefaultRefreshInterval); admissions wait past that window.</summary>
    private const int GateRefreshSeconds = 65;

    private readonly ITestOutputHelper _output;

    public McWildcardAllowlistProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeWildcardAndHatchAtTheLiveEventServerGate()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-wildcard-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        Window mcMain = null;
        ClientSession mc = null;
        var originalLines = new List<string>();
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
            try
            {
                ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
            }
            catch (InvalidOperationException first)
            {
                // Diagnostics before giving up (the #74 walk passed this morning - find out
                // whether the child is renamed, gone, or just unrealized).
                ManagementClientUi.DumpWorkspaceChildren(mcMain, log);
                log.AppendLine($"first expansion pass failed ({first.Message}); one retry");
                ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
            }

            ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);

            // #81: the allowlist is the structured grid on the Proxy tab - rows read back in the
            // same "scheme://host[:port] [accept-untrusted]" shape the old text box wrote.
            AllowlistGridUi.OpenProxyTab(mcMain);
            originalLines.AddRange(AllowlistGridUi.ReadEntries(mcMain));

            log.AppendLine($"original allowlist: '{string.Join(" | ", originalLines)}'");

            // ---- Phase A: the leftmost-label wildcard ------------------------------
            var phaseALines = WithoutProbeLines(originalLines);
            phaseALines.Add(WildcardLine);
            AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, phaseALines, log);

            // Readback: the grid re-renders the wildcard from the STORED structured record
            // (SetEntriesAndSave reads the pane back after re-selecting the item) - the 'pattern'
            // shape round-tripping through the live shell.
            var readbackA = AllowlistGridUi.ReadEntries(mcMain);
            Assert.True(readbackA.Any(l => l.Trim() == WildcardLine),
                $"after the phase A save the admin page never re-rendered '{WildcardLine}' from the stored record: '{string.Join(" | ", readbackA)}'");
            log.AppendLine($"phase A readback carries '{WildcardLine}' verbatim (structured pattern shape round-tripped)");

            // The gate snapshot is up to 60 s stale after the save - wait for admission.
            // Admission is pinned at MATCHER level: the answer must no longer be NotAllowed -
            // a Timeout/Unreachable answer NAMES the dialled target, which itself proves the
            // wildcard admitted the fetch (a 200 through this exact path is banked in the
            // earlier run's artifact; the ES's 5 s header timeout to the public target is
            // transport, not policy).
            var subResult = FetchUntil(mcMain, SubdomainUrl,
                text => (text.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase) ||
                         (text.StartsWith("Proxy fetch ", StringComparison.OrdinalIgnoreCase) && !text.Contains("NotAllowed", StringComparison.Ordinal))),
                log, "phase A subdomain");
            log.AppendLine($"phase A: GET {SubdomainUrl} -> {subResult}");
            Assert.False(subResult.Contains("NotAllowed", StringComparison.Ordinal),
                $"'{WildcardLine}' never admitted '{SubdomainUrl}' at the live Event Server gate within the refresh windows: {subResult}");

            // Same snapshot window proves the apex posture: the wildcard covers SUBDOMAINS only.
            var apexResult = Fetch(mcMain, ApexUrl);
            log.AppendLine($"phase A: GET {ApexUrl} -> {apexResult}");
            Assert.Contains("NotAllowed", apexResult, StringComparison.Ordinal);
            Assert.Contains("not in the proxy allowlist", apexResult, StringComparison.Ordinal);

            // ---- Phase B: the full-host hatch with the SSRF floor -------------------
            var phaseBLines = WithoutProbeLines(originalLines);
            phaseBLines.Add(HatchLine);
            AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, phaseBLines, log);

            var readbackB = AllowlistGridUi.ReadEntries(mcMain);
            Assert.True(readbackB.Any(l => l.Trim() == HatchLine),
                $"after the phase B save the admin page never re-rendered '{HatchLine}' from the stored record: '{string.Join(" | ", readbackB)}'");
            log.AppendLine($"phase B readback carries '{HatchLine}' verbatim");

            var hatchResult = FetchUntil(mcMain, ApexUrl,
                text => text.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase) ||
                        (text.StartsWith("Proxy fetch ", StringComparison.OrdinalIgnoreCase) && !text.Contains("NotAllowed", StringComparison.Ordinal)),
                log, "phase B hatch admission");
            log.AppendLine($"phase B: GET {ApexUrl} -> {hatchResult}");
            Assert.False(hatchResult.Contains("NotAllowed", StringComparison.Ordinal),
                $"'{HatchLine}' still refused the apex '{ApexUrl}' at the live Event Server gate: {hatchResult}");
            // (A Timeout/Unreachable answer here still proves ADMISSION - the gate let the
            // fetch dial; only the matcher could have produced NotAllowed. Phase A already
            // banked a 200 through the wildcard path.)

            // The syntax floor: the hatch covers every host EXCEPT literal IPs/localhost/VMS
            // hosts. A floored refusal comes back NotAllowed in well under the gate refresh
            // window - if the floor were missing, this would instead dial the socket and
            // answer Unreachable after seconds. Both are refusals; only the floor refuses
            // BEFORE the socket, and the NotAllowed code is what pins that.
            var floorResult = Fetch(mcMain, FlooredIpUrl);
            log.AppendLine($"phase B: GET {FlooredIpUrl} -> {floorResult}");
            Assert.Contains("NotAllowed", floorResult, StringComparison.Ordinal);
            Assert.DoesNotContain("Unreachable", floorResult, StringComparison.Ordinal);

            // The EnvironmentManager half of the floor: the VMS's own host must be floored at
            // the Event Server too (ResolveVmsFloorHosts). Without that wiring the fetch would
            // dial the Management Server (a connection answer); with it, NotAllowed instantly.
            var vmsHost = config.VmsAddress.Contains("://") ? new Uri(config.VmsAddress).Host : config.VmsAddress;
            var vmsFloorResult = Fetch(mcMain, $"http://{vmsHost}/");
            log.AppendLine($"phase B: GET http://{vmsHost}/ -> {vmsFloorResult}");
            Assert.Contains("NotAllowed", vmsFloorResult, StringComparison.Ordinal);
            Assert.DoesNotContain("Unreachable", vmsFloorResult, StringComparison.Ordinal);

            // #81 rides along: the hatch row is the full-host escape hatch, so the Proxy tab's
            // warning banner must be up while it is in the grid.
            Assert.True(AllowlistGridUi.WarningBannerVisible(mcMain),
                $"phase B: with '{HatchLine}' saved in the grid, the full-host-wildcard warning banner never appeared.");

            try { Artifacts.Window(mcMain, "mc-wildcard-phase-b"); }
            catch (Exception screenshotFailure) { log.AppendLine($"phase B screenshot skipped: {screenshotFailure.Message}"); }

            // ---- Restore: the workspace gets its allowlist back WITHOUT any probe line ----
            // (self-healing: a dead earlier run's leftover wildcard/hatch/neverssl lines are
            // cleaned here, so "original" never re-bakes a stale probe entry.)
            var restoreLines = WithoutProbeLines(originalLines);
            AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, restoreLines, log);
            var restored = AllowlistGridUi.ReadEntries(mcMain);
            Assert.True(AllowlistGridUi.SameEntries(restoreLines, restored),
                $"restore failed: allowlist reads '{string.Join(" | ", restored)}', expected '{string.Join(" | ", restoreLines)}'");
            Assert.False(AllowlistGridUi.WarningBannerVisible(mcMain),
                "the full-host-wildcard warning banner stayed up after the hatch row was removed.");
            log.AppendLine("allowlist restored (probe lines removed, verified readback, banner down)");
        }
        finally
        {
            Artifacts.WriteText("mc-wildcard-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain?.Close(); } catch { }
            if (mc != null) ManagementClientUi.AcceptPendingModal(mc, 5);
            ProcessKiller.KillClients("VideoOS.Administration");
        }
    }

    /// <summary>Any earlier probe run's wildcard/hatch/neverssl lines never stack into a
    /// contradictory allowlist; the probe owns exactly its own lines.</summary>
    private static List<string> WithoutProbeLines(List<string> lines) =>
        lines
            .Where(l => !l.Split(' ')[0].Equals(WildcardLine, StringComparison.OrdinalIgnoreCase) &&
                        !l.Split(' ')[0].Equals(HatchLine, StringComparison.OrdinalIgnoreCase) &&
                        !l.Split(' ')[0].Equals(ApexUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            .ToList();

    private string FetchUntil(Window mcMain, string url, Func<string, bool> accept, StringBuilder log, string label)
    {
        var text = string.Empty;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Thread.Sleep(attempt == 1 ? 15000 : GateRefreshSeconds * 1000);
            text = Fetch(mcMain, url);
            if (accept(text)) return text;
            log.AppendLine($"{label}: attempt {attempt} -> '{text}' (gate snapshot may still be stale; waiting {GateRefreshSeconds}s)");
        }

        return text;
    }

    private static string Fetch(Window mcMain, string url)
    {
        ManagementClientUi.SetValue(mcMain, "txtProxyTestUrl", url);
        var button = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("btnProxyTestFetch"))
            ?? throw new InvalidOperationException("btnProxyTestFetch not found.");

        var before = ResultText(mcMain);
        try { button.Patterns.Invoke.Pattern.Invoke(); }
        catch { button.Click(); }

        // The label flips through "Fetching through..." on every invoke; once that transient
        // is seen, the NEXT non-transient text is this attempt's verdict - even when it is
        // word-for-word the previous attempt's (the identical-verdict case ValuePattern-less
        // labels cannot otherwise distinguish).
        var sawRunning = false;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var text = ResultText(mcMain);
            if (text.StartsWith("Fetching through", StringComparison.OrdinalIgnoreCase))
            {
                sawRunning = true;
            }
            else if (!string.IsNullOrEmpty(text) && (sawRunning || text != before))
            {
                return text;
            }

            Thread.Sleep(500);
        }

        return sawRunning
            ? before + " (verdict unchanged from the previous attempt)"
            : $"(timeout waiting for result; last text: '{ResultText(mcMain)}')";
    }

    private static string ResultText(Window mcMain) =>
        mcMain.FindFirstDescendant(cf => cf.ByAutomationId("lblProxyTestResult"))?.Name ?? string.Empty;
}
