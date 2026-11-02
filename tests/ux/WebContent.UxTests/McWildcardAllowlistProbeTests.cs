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
/// before any socket opens (a missing floor would answer Unreachable after seconds). The MC
/// readback of each wildcard line between saves proves the structured 'pattern' storage shape
/// round-trips through the live admin page. Every phase restores the workspace's original
/// allowlist verbatim before the probe exits.
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
            ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
            ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);

            var targetsBox = ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 30);
            foreach (var line in (ReadValue(targetsBox) ?? string.Empty)
                         .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                originalLines.Add(line.Trim());
            }

            log.AppendLine($"original allowlist: '{string.Join(" | ", originalLines)}'");

            // ---- Phase A: the leftmost-label wildcard ------------------------------
            var phaseALines = WithoutProbeLines(originalLines);
            phaseALines.Add(WildcardLine);
            SaveTargets(mc, mcMain, config.McWorkspaceName, phaseALines, log);

            // Readback: the admin page re-renders the wildcard from the STORED structured
            // record - the 'pattern' shape round-tripping through the live shell.
            var readbackA = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 15));
            Assert.True(Split(readbackA).Any(l => l.Trim() == WildcardLine),
                $"after the phase A save the admin page never re-rendered '{WildcardLine}' from the stored record: '{readbackA}'");
            log.AppendLine($"phase A readback carries '{WildcardLine}' verbatim (structured pattern shape round-tripped)");

            // The gate snapshot is up to 60 s stale after the save - wait for admission.
            var subResult = FetchUntil(mcMain, SubdomainUrl, text => text.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase), log, "phase A subdomain");
            log.AppendLine($"phase A: GET {SubdomainUrl} -> {subResult}");
            Assert.True(subResult.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase),
                $"'{WildcardLine}' never admitted '{SubdomainUrl}' at the live Event Server gate within the refresh windows: {subResult}");

            // Same snapshot window proves the apex posture: the wildcard covers SUBDOMAINS only.
            var apexResult = Fetch(mcMain, ApexUrl);
            log.AppendLine($"phase A: GET {ApexUrl} -> {apexResult}");
            Assert.Contains("NotAllowed", apexResult, StringComparison.Ordinal);
            Assert.Contains("not in the proxy allowlist", apexResult, StringComparison.Ordinal);

            // ---- Phase B: the full-host hatch with the SSRF floor -------------------
            var phaseBLines = WithoutProbeLines(originalLines);
            phaseBLines.Add(HatchLine);
            SaveTargets(mc, mcMain, config.McWorkspaceName, phaseBLines, log);

            var readbackB = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 15));
            Assert.True(Split(readbackB).Any(l => l.Trim() == HatchLine),
                $"after the phase B save the admin page never re-rendered '{HatchLine}' from the stored record: '{readbackB}'");
            log.AppendLine($"phase B readback carries '{HatchLine}' verbatim");

            var hatchResult = FetchUntil(mcMain, SubdomainUrl, text => text.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase), log, "phase B hatch");
            log.AppendLine($"phase B: GET {SubdomainUrl} -> {hatchResult}");
            Assert.True(hatchResult.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase),
                $"'{HatchLine}' never admitted '{SubdomainUrl}' at the live Event Server gate: {hatchResult}");

            // The floor: the hatch covers every host EXCEPT literal IPs/localhost/VMS hosts. A
            // floored refusal comes back NotAllowed in well under the gate refresh window - if
            // the floor were missing, this would instead dial the socket and answer
            // Unreachable after seconds. Both are refusals; only the floor refuses BEFORE the
            // socket, and the NotAllowed code is what pins that.
            var floorResult = Fetch(mcMain, FlooredIpUrl);
            log.AppendLine($"phase B: GET {FlooredIpUrl} -> {floorResult}");
            Assert.Contains("NotAllowed", floorResult, StringComparison.Ordinal);
            Assert.DoesNotContain("Unreachable", floorResult, StringComparison.Ordinal);

            Artifacts.Window(mcMain, "mc-wildcard-phase-b");

            // ---- Restore: the workspace gets its ORIGINAL allowlist back verbatim ----
            SaveTargets(mc, mcMain, config.McWorkspaceName, originalLines, log);
            var restored = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 15));
            Assert.True(SameLines(originalLines, restored),
                $"restore failed: allowlist reads '{restored}', expected '{string.Join(" | ", originalLines)}'");
            log.AppendLine("allowlist restored to the original lines (verified readback)");
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

    /// <summary>The ProxyTargetProvisioning save discipline, whole-box version: focus-gated edit,
    /// dirty guards, the pre-Save Name check, and a verified readback.</summary>
    private static void SaveTargets(ClientSession mc, Window mcMain, string workspaceName, List<string> lines, StringBuilder log)
    {
        var joined = string.Join("\n", lines);
        ManagementClientUi.EnsureCheckBox(mcMain, "chkProxyEnabled", true);

        var pasted = ManagementClientUi.PasteIntoControl(mcMain, "txtProxyTargets", joined);
        var edited = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 5));
        if (!SameLines(lines, edited))
        {
            log.AppendLine($"paste route {(pasted ? "" : "(clipboard unavailable) ")}did not land (readback '{edited}'); ValuePattern fallback");
            ManagementClientUi.SetValue(mcMain, "txtProxyTargets", joined);
            edited = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 5));
        }

        if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
        {
            log.AppendLine("Save still disabled after edit; flipping chkProxyEnabled twice to dirty the pane");
            ManagementClientUi.DirtyPaneViaCheckbox(mcMain, "chkProxyEnabled");
            if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
            {
                ManagementClientUi.NudgeControlWithKeystroke(mcMain, "txtProxyTargets");
            }
        }

        var paneName = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtName", 5));
        if (!string.Equals(paneName, workspaceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to Save: the Name field reads '{paneName}', expected '{workspaceName}'.");

        if (!ManagementClientUi.Save(mc, mcMain, 20))
            throw new InvalidOperationException($"Save never became enabled for allowlist '{joined}'.");

        Thread.Sleep(1500);
        var after = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 15));
        if (!SameLines(lines, after))
            throw new InvalidOperationException($"After Save, the proxy targets read back '{after}' — expected '{joined}'.");
        log.AppendLine($"saved allowlist '{joined}' (verified readback)");
    }

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

        var deadline = DateTime.UtcNow.AddSeconds(45);
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

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name; }
        catch { return element.Name; }
    }

    private static bool SameLines(IEnumerable<string> expected, string actual) =>
        Norm(expected).SequenceEqual(Norm(Split(actual)));

    private static string[] Split(string s) =>
        (s ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    private static string[] Norm(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
}
