using FlaUI.Core.AutomationElements;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Diagnostic probe (#54): launches and logs in a Management Client, opens the workspace
/// definition page's "Test proxy fetch" row, and captures the actual proxy-fetch outcomes
/// (results land in the artifacts directory, proxy-probe-*.txt / .png). Case 1 exercises the
/// server-side allowlist refusal round-trip, case 2 the allowlisted chunked success round-trip.
/// </summary>
public class McProxyFetchProbeTests
{
    private const string WorkspaceName = "Demo Workspace";

    private readonly ITestOutputHelper _output;

    public McProxyFetchProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeProxyTestFetchOutcomes()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        ProcessKiller.KillClients("VideoOS.Administration");
        var session = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(session);
        LoginFlow.Login(session, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(session);
        var mcMain = LoginFlow.WaitForAdminMain(session);
        ManagementClientUi.CloseStrayLoginDialogs(session);

        ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, WorkspaceName);
        ManagementClientUi.SelectTreeItem(mcMain, WorkspaceName);

        var urlBox = ManagementClientUi.ExpectControl(mcMain, "txtProxyTestUrl", 30);
        Artifacts.Window(mcMain, "proxy-probe-page");

        var chk = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("chkProxyEnabled"));
        string proxyToggle = chk != null && chk.Patterns.Toggle.IsSupported
            ? chk.Patterns.Toggle.Pattern.ToggleState.Value.ToString()
            : "unknown";
        var targets = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("txtProxyTargets"));
        var targetsText = targets == null ? "(no txtProxyTargets control)" : ReadValue(targets);

        var log = new System.Text.StringBuilder();
        log.AppendLine($"workspace='{WorkspaceName}' proxyEnabledToggle={proxyToggle} targets={targetsText}");

        // 1) Origin no proxied workspace allowlists: proves the request reaches the Event Server
        //    fetcher, the server-side allowlist gate answers, and the answer travels back (a
        //    NotAllowed failure frame, not a client-side timeout).
        log.AppendLine($"non-allowlisted http://10.255.255.1/ -> {RunFetch(mcMain, urlBox, "http://10.255.255.1/")}");

        // 2) The workspace's allowlisted proxy target (the stored txtProxyTargets entry): proves
        //    the ES-side workspace config resolve succeeded and the chunked success path
        //    round-trips. The ES fetcher reaches https fine; only the SC browser's loopback leg
        //    is http-only.
        log.AppendLine($"allowlisted https://joshooaj.com/ -> {RunFetch(mcMain, urlBox, "https://joshooaj.com/")}");

        Artifacts.WriteText("proxy-probe-results.txt", log.ToString());
        _output.WriteLine(log.ToString());

        var result = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("lblProxyTestResult"));
        if (result != null)
        {
            Artifacts.WindowCrop(mcMain, "proxy-probe-result", urlBox.BoundingRectangle, result.BoundingRectangle);
        }
    }

    private string RunFetch(Window mcMain, AutomationElement urlBox, string url)
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
}
