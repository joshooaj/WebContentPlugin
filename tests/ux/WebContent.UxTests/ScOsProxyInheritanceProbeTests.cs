using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FlaUI.Core.AutomationElements;

using Microsoft.Playwright;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Diagnostic probe (#67, verification-matrix SC-8): the OS-configured (WinINET) proxy and the
/// workspace proxy, end to end on the real desktop.
///
///   Row 1 (inheritance): with the per-user WinINET proxy pointed at a local logging forward
///   proxy harness, a NON-proxied view (routing off) of http://wininet-proxy.invalid/ renders
///   the harness's marker page. The host sits on the RFC 2606 .invalid TLD - it can never
///   resolve - so the ONLY route to any bytes is the OS proxy: the rendered page IS the proof.
///   The browser processes are additionally read from their live command lines: no
///   --proxy-server / --no-proxy-server switch anywhere (the plugin injects nothing; WebView2
///   falls back to the system proxy on its own).
///
///   Row 2 (deliberate contrast): the SAME OS proxy still pointed at the harness, the SAME
///   home url, routing ON: the view shows the plugin's own 403 allowlist page, the harness
///   journal stays empty for that origin (the browser never asked the OS proxy), and the
///   live browser process command lines carry the loopback pin -
///   --proxy-server=http=http://127.0.0.1:<listener>;... plus --proxy-bypass-list=<-loopback>.
///
/// The rows mutate live state and restore it: the per-user WinINET proxy settings (registry
/// snapshot/restore, no elevation) and the Demo Workspace's routing toggle + home address
/// (Management Client phase, audit-line verified). Artifacts land in
/// webcontent-os-proxy-probe\&lt;run&gt; like the other proxy probes.
/// </summary>
public class ScOsProxyInheritanceProbeTests
{
    /// <summary>Home url for both rows: a host on the RFC 2606 .invalid TLD can never resolve,
    /// so a served page can only have come through the configured OS proxy - and no sane
    /// workspace allowlist lists it, keeping the row-2 refusal stable.</summary>
    private const string OsProxyHome = "http://wininet-proxy.invalid/";

    /// <summary>The standing lab baseline this probe restores to when its own reads failed.</summary>
    private const string BaselineHome = "http://httpforever.com/";

    private const string DataRootMarker = "SmartClient.WebContentData";

    private readonly ITestOutputHelper _output;

    public ScOsProxyInheritanceProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Row 1: routing OFF + the WinINET system proxy pointed at the logging harness. The
    /// non-proxied WebView2 must inherit the OS proxy: the harness's marker page renders at
    /// the .invalid url (which can never resolve directly), the harness journals the
    /// browser's absolute-form GET, and no browser process carries any proxy switch.
    /// </summary>
    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeNonProxiedViewRoutesThroughOsConfiguredProxy()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-os-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        string failure = null;
        string title = null;
        string bodyText = null;
        string documentUrl = null;
        WorkspaceState workspace = null;
        WinInetProxyHarness harness = null;
        WinInetProxyControl.Snapshot wininet = null;
        var wininetApplied = false;
        ClientSession sc = null;
        List<string> browserArgs = new();
        List<WinInetProxyHarness.JournalEntry> harnessJournal = new();
        try
        {
            try
            {
                workspace = ProvisionWorkspace(config, home: OsProxyHome, routing: false, log);
            }
            catch (Exception ex)
            {
                // The edit may have stored before the verification threw: put the item back to
                // the documented lab baseline (routing on, httpforever home) best-effort.
                log.AppendLine($"provisioning threw ({ex.GetType().Name}: {ex.Message}); restoring the baseline workspace state");
                RestoreToBaseline(config, log);
                throw;
            }

            harness = new WinInetProxyHarness();
            wininet = WinInetProxyControl.Capture();
            WinInetProxyControl.UseLoopbackProxy(harness.Port);
            wininetApplied = true;
            log.AppendLine($"WinINET (HKCU) proxy set to 127.0.0.1:{harness.Port} " +
                           $"(was enable={wininet.ProxyEnable} server='{wininet.ProxyServer}' override='{wininet.ProxyOverride}'); " +
                           $"user-scope WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS='{Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", EnvironmentVariableTarget.User) ?? "<unset>"}'");

            sc = LaunchSmartClient(config, log);
            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, "wininet-proxy.invalid", timeoutSeconds: 90);
            await page.WaitForSelectorAsync("body", new PageWaitForSelectorOptions { Timeout = 30000 });

            documentUrl = page.Url;
            title = await page.TitleAsync();
            var body = await page.QuerySelectorAsync("body");
            bodyText = body == null ? null : await body.TextContentAsync();
            log.AppendLine($"start document -> url='{documentUrl}' title='{title}'");
            log.AppendLine($"body: {Summarize(bodyText)}");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifacts.PathOf("os-proxy-nonproxied-harness-page.png") });
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}{Environment.NewLine}{ex}");
        }
        finally
        {
            // Capture the live browser command lines BEFORE anything is killed: which switches
            // the browser was LAUNCHED with is the routing evidence this row is about.
            browserArgs = BrowserProcessArgs.CommandLines(DataRootMarker);
            harnessJournal = harness?.Journal() ?? new List<WinInetProxyHarness.JournalEntry>();

            ProcessKiller.KillClients("Client");
            ProcessKiller.KillClients("VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers(DataRootMarker);
            sc?.Dispose();
            if (wininetApplied)
            {
                try
                {
                    WinInetProxyControl.Restore(wininet);
                    log.AppendLine("WinINET proxy settings restored to the captured snapshot");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"!!! WinINET restore FAILED: {ex.Message}");
                }
            }

            harness?.Dispose();
            RestoreWorkspace(config, workspace, log);
            Artifacts.WriteText("os-proxy-nonproxied-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");

        // The browser processes the plugin hosts carry NO proxy switch: the plugin injects
        // nothing into a non-proxied browser, and the OS proxy is what routes it.
        log.AppendLine("browser process command lines (from Win32_Process):");
        foreach (var commandLine in browserArgs)
            log.AppendLine($"  {BrowserProcessArgs.Summarize(commandLine)}");
        Assert.NotEmpty(browserArgs);
        Assert.All(browserArgs, cl =>
        {
            Assert.False(cl.Contains("--proxy-server", StringComparison.OrdinalIgnoreCase),
                "a non-proxied view's browser process carries a --proxy-server argument");
            Assert.False(cl.Contains("--no-proxy-server", StringComparison.OrdinalIgnoreCase),
                "a non-proxied view's browser process carries --no-proxy-server (which would defeat OS proxy inheritance)");
        });
        Artifacts.WriteText("os-proxy-nonproxied-browser-args.txt", string.Join(Environment.NewLine, browserArgs));

        // The marker page rendered: the harness served it (the host can never resolve direct).
        Assert.NotNull(documentUrl);
        Assert.StartsWith(OsProxyHome.TrimEnd('/'), documentUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WinInetProxyHarness.PageTitle, title);
        Assert.NotNull(bodyText);
        Assert.Contains(WinInetProxyHarness.BodyMarker, bodyText);
        Assert.Contains("wininet-proxy.invalid", bodyText);

        // The harness's journal shows HOW the browser asked: an absolute-form proxied request.
        var entries = harnessJournal
            .Where(e => e.RequestTarget.IndexOf("wininet-proxy.invalid", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        log.AppendLine($"harness journal for wininet-proxy.invalid: {entries.Count} request(s) of {harnessJournal.Count} total");
        foreach (var entry in entries)
            log.AppendLine($"  {entry.SeenAtUtc:o} {entry.Method} {entry.RequestTarget} UA='{entry.UserAgent}'");
        var get = entries.FirstOrDefault(e => e.Method == "GET" &&
            e.RequestTarget.StartsWith("http://wininet-proxy.invalid", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(get);
        Assert.Contains("Chrome", get.UserAgent, StringComparison.OrdinalIgnoreCase); // the WebView2/Chromium UA
        Artifacts.WriteText("os-proxy-nonproxied-harness-journal.txt",
            string.Join(Environment.NewLine, harnessJournal.Select(e => $"{e.SeenAtUtc:o} {e.Method} {e.RequestTarget} UA='{e.UserAgent}'")));
    }

    /// <summary>
    /// Row 2: routing ON with the SAME WinINET system proxy still pointed at the logging
    /// harness. The proxied browser's loopback pin must win: the plugin's 403 allowlist page
    /// renders, the harness journals NOTHING for the origin (the OS proxy was never asked),
    /// and the live browser command lines show the --proxy-server loopback pin plus the
    /// disabled <-loopback> bypass.
    /// </summary>
    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedViewNeverUsesOsConfiguredProxy()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-os-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        string failure = null;
        string title = null;
        string bodyText = null;
        string documentUrl = null;
        WorkspaceState workspace = null;
        WinInetProxyHarness harness = null;
        WinInetProxyControl.Snapshot wininet = null;
        var wininetApplied = false;
        ClientSession sc = null;
        List<string> browserArgs = new();
        List<WinInetProxyHarness.JournalEntry> harnessJournal = new();
        try
        {
            // The Management Client phase runs BEFORE the OS proxy is pointed at the harness:
            // the MC's own login channel follows the per-user system proxy (found live on this
            // row's first run - with the harness enabled first, MC never reached its main
            // window and the harness journal filled with its refused requests). The OS proxy
            // only needs to be live for the Smart Client phase, which is the phase under test.
            try
            {
                workspace = ProvisionWorkspace(config, home: OsProxyHome, routing: true, log);
            }
            catch (Exception ex)
            {
                // The edit may have stored before the verification threw: put the item back to
                // the documented lab baseline (routing on, httpforever home) best-effort.
                log.AppendLine($"provisioning threw ({ex.GetType().Name}: {ex.Message}); restoring the baseline workspace state");
                RestoreToBaseline(config, log);
                throw;
            }

            harness = new WinInetProxyHarness();
            wininet = WinInetProxyControl.Capture();
            WinInetProxyControl.UseLoopbackProxy(harness.Port);
            wininetApplied = true;
            log.AppendLine($"WinINET (HKCU) proxy set to 127.0.0.1:{harness.Port} (was enable={wininet.ProxyEnable} server='{wininet.ProxyServer}')");

            sc = LaunchSmartClient(config, log);
            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, "wininet-proxy.invalid", timeoutSeconds: 90);
            await page.WaitForSelectorAsync("body", new PageWaitForSelectorOptions { Timeout = 30000 });

            documentUrl = page.Url;
            title = await page.TitleAsync();
            var body = await page.QuerySelectorAsync("body");
            bodyText = body == null ? null : await body.TextContentAsync();
            log.AppendLine($"start document -> url='{documentUrl}' title='{title}'");
            log.AppendLine($"body: {Summarize(bodyText)}");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifacts.PathOf("os-proxy-proxied-403-page.png") });
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}{Environment.NewLine}{ex}");
        }
        finally
        {
            // Capture the live browser command lines and the harness journal BEFORE anything is
            // killed: the launch switches and the (non-)requests are this row's evidence.
            browserArgs = BrowserProcessArgs.CommandLines(DataRootMarker);
            log.AppendLine("browser process command lines (from Win32_Process):");
            foreach (var commandLine in browserArgs)
                log.AppendLine($"  {BrowserProcessArgs.Summarize(commandLine)}");
            Artifacts.WriteText("os-proxy-proxied-browser-args.txt", string.Join(Environment.NewLine, browserArgs));

            harnessJournal = harness?.Journal() ?? new List<WinInetProxyHarness.JournalEntry>();
            log.AppendLine($"harness journal (whole run): {harnessJournal.Count} request(s)");
            foreach (var entry in harnessJournal.Take(40))
                log.AppendLine($"  {entry.SeenAtUtc:o} {entry.Method} {entry.RequestTarget} UA='{entry.UserAgent}'");
            if (harnessJournal.Count > 40)
                log.AppendLine($"  ... {harnessJournal.Count - 40} more request(s) suppressed in the log (full journal in the artifact)");
            Artifacts.WriteText("os-proxy-proxied-harness-journal.txt",
                string.Join(Environment.NewLine, harnessJournal.Select(e => $"{e.SeenAtUtc:o} {e.Method} {e.RequestTarget} UA='{e.UserAgent}'")));

            ProcessKiller.KillClients("Client");
            ProcessKiller.KillClients("VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers(DataRootMarker);
            sc?.Dispose();
            if (wininetApplied)
            {
                try
                {
                    WinInetProxyControl.Restore(wininet);
                    log.AppendLine("WinINET proxy settings restored to the captured snapshot");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"!!! WinINET restore FAILED: {ex.Message}");
                }
            }

            harness?.Dispose();
            RestoreWorkspace(config, workspace, log);
            Artifacts.WriteText("os-proxy-proxied-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");

        // The proxied view's OWN 403 page - if the OS proxy had won, the harness would have
        // served the marker page and this title assertion fails.
        Assert.NotNull(documentUrl);
        Assert.StartsWith(OsProxyHome.TrimEnd('/'), documentUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Web Content proxy: 403", title);
        Assert.NotNull(bodyText);
        Assert.Contains("not in the workspace proxy allowlist", bodyText);
        Assert.Contains("Allowlist entry that would admit this origin: http://wininet-proxy.invalid", bodyText);

        // The loopback pin is on the live command lines: every scheme pinned to the loopback
        // listener, implicit localhost bypass disabled - the system proxy cannot apply.
        Assert.NotEmpty(browserArgs);
        var pinned = browserArgs.Where(cl =>
            cl.Contains("--proxy-server=http=http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) &&
            cl.Contains("--proxy-bypass-list=<-loopback>", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(pinned);

        // The OS proxy was never asked for this origin: an empty journal for it IS the proof
        // the proxied browser ignored the system proxy.
        var askedOsProxy = harnessJournal
            .Where(e => e.RequestTarget.IndexOf("wininet-proxy.invalid", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        Assert.Empty(askedOsProxy);
    }

    // ---- shared phases ------------------------------------------------------------------------

    /// <summary>The workspace state a ProvisionWorkspace call found (and whether it edited).</summary>
    private sealed record WorkspaceState(string Home, bool Routing, bool Changed);

    /// <summary>
    /// The Management Client phase shared by both rows: set the home address and the routing
    /// toggle in ONE MC session, save, verify the plugin's own storage audit line names the
    /// routing state this call wanted, then read both back from a RELOADED pane. Returns the
    /// pre-call state so the probe restores exactly what it found.
    /// </summary>
    private static WorkspaceState ProvisionWorkspace(UxConfig config, string home, bool routing, StringBuilder log)
    {
        ProcessKiller.KillClients("VideoOS.Administration");
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        ManagementClientUi.CloseStrayLoginDialogs(mc);
        ManagementClientUi.WaitForTreeReady(mcMain);

        ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
        ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
        ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
        ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);

        // Settings tab: the home address.
        AllowlistGridUi.OpenSettingsTab(mcMain);
        var homeBox = ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 15);
        var previousHome = ReadValue(homeBox)?.Trim();
        var homeChanged = !string.Equals(previousHome, home, StringComparison.OrdinalIgnoreCase);
        if (homeChanged)
        {
            var pasted = ManagementClientUi.PasteIntoControl(mcMain, "txtHomeAddress", home);
            var edited = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 5))?.Trim();
            if (!string.Equals(edited, home, StringComparison.OrdinalIgnoreCase))
            {
                log.AppendLine($"home paste route {(pasted ? "" : "(clipboard unavailable) ")}did not land (readback '{edited}'); ValuePattern fallback");
                ManagementClientUi.SetValue(mcMain, "txtHomeAddress", home);
                edited = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 5))?.Trim();
            }

            if (!string.Equals(edited, home, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"the home address never read back as '{home}' (reads '{edited}').");
        }

        // Proxy tab: the routing toggle.
        AllowlistGridUi.OpenProxyTab(mcMain);
        var toggle = ManagementClientUi.ExpectControl(mcMain, AllowlistGridUi.ProxyEnabledId, 10);
        var previousRouting = toggle.Patterns.Toggle.IsSupported &&
                              toggle.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On;
        log.AppendLine($"workspace before: home='{previousHome}' routing={(previousRouting ? "on" : "off")}");
        if (previousRouting != routing)
            ManagementClientUi.EnsureCheckBox(mcMain, AllowlistGridUi.ProxyEnabledId, routing);

        if (!homeChanged && previousRouting == routing)
        {
            log.AppendLine($"workspace already home='{home}' routing={(routing ? "on" : "off")}; no Management Client edit needed");
            Retire(mc, mcMain);
            return new WorkspaceState(previousHome, previousRouting, Changed: false);
        }

        if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
        {
            log.AppendLine("Save still disabled after the edits; keyboard-nudging the home box");
            ManagementClientUi.NudgeControlWithKeystroke(mcMain, "txtHomeAddress");
            if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
            {
                log.AppendLine("Save still disabled after the nudge; flipping the routing checkbox twice");
                ManagementClientUi.DirtyPaneViaCheckbox(mcMain, AllowlistGridUi.ProxyEnabledId);
                log.AppendLine($"Save enabled after the checkbox flip: {ManagementClientUi.WaitForSaveEnabled(mcMain, 6)}");
            }
        }

        var paneName = AllowlistGridUi.ReadPaneName(mcMain);
        if (!string.Equals(paneName, config.McWorkspaceName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to Save: the Name field reads '{paneName}', expected '{config.McWorkspaceName}'.");

        AllowlistGridUi.OpenProxyTab(mcMain);
        var auditMarker = $"Stored proxy targets for workspace '{config.McWorkspaceName}'";
        var routingToken = routing ? "routing on" : "routing off";
        var auditBefore = McLog.CountMatches(auditMarker, routingToken);
        if (!ManagementClientUi.Save(mc, mcMain, 20))
            throw new InvalidOperationException($"Save never completed for home='{home}' routing={(routing ? "on" : "off")}.");
        var auditLine = McLog.WaitForNewLine(auditBefore, 20, auditMarker, routingToken);
        if (auditLine == null)
            throw new InvalidOperationException($"the save wrote no 'Stored proxy targets ...' audit line carrying '{routingToken}'.");
        log.AppendLine($"storage audit line: {auditLine}");

        Thread.Sleep(1500);
        AllowlistGridUi.ReloadPane(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
        AllowlistGridUi.OpenProxyTab(mcMain);
        var reloadedToggle = ManagementClientUi.ExpectControl(mcMain, AllowlistGridUi.ProxyEnabledId, 10);
        var reloadedRouting = reloadedToggle.Patterns.Toggle.IsSupported &&
                              reloadedToggle.Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On;
        if (reloadedRouting != routing)
            throw new InvalidOperationException($"after Save + reload the routing toggle reads {(reloadedRouting ? "on" : "off")}, expected {(routing ? "on" : "off")}.");
        AllowlistGridUi.OpenSettingsTab(mcMain);
        var reloadedHome = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 10))?.Trim();
        if (!string.Equals(reloadedHome, home, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"after Save + reload the home address reads '{reloadedHome}', expected '{home}'.");
        log.AppendLine($"workspace set to home='{home}' routing={(routing ? "on" : "off")} (verified from the audit line AND a reloaded pane)");

        Retire(mc, mcMain);
        return new WorkspaceState(previousHome, previousRouting, Changed: true);
    }

    /// <summary>Restores whatever ProvisionWorkspace found; falls back to the standing baseline.</summary>
    private static void RestoreWorkspace(UxConfig config, WorkspaceState state, StringBuilder log)
    {
        if (state == null || !state.Changed) return;
        try
        {
            ProvisionWorkspace(config, state.Home ?? BaselineHome, state.Routing, log);
            log.AppendLine($"workspace restored to home='{state.Home ?? BaselineHome}' routing={(state.Routing ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"!!! workspace restore FAILED ('{state.Home ?? BaselineHome}', routing={(state.Routing ? "on" : "off")}): {ex.Message}");
        }
    }

    /// <summary>The standing lab baseline (routing on, httpforever home), used when the probe's
    /// own read of the pre-call state never completed.</summary>
    private static void RestoreToBaseline(UxConfig config, StringBuilder log)
    {
        try
        {
            ProvisionWorkspace(config, BaselineHome, routing: true, log);
            log.AppendLine($"workspace restored to the baseline home='{BaselineHome}' routing=on");
        }
        catch (Exception ex)
        {
            log.AppendLine($"!!! baseline restore FAILED: {ex.Message}");
        }
    }

    private static ClientSession LaunchSmartClient(UxConfig config, StringBuilder log)
    {
        CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
        ProcessKiller.KillClients("Client");
        ProcessKiller.KillPluginWebView2Browsers(DataRootMarker);

        var sc = ClientSession.Launch(config.SmartClientExe);
        var login = LoginFlow.WaitForLoginWindow(sc);
        LoginFlow.Login(sc, login, config);
        LoginFlow.AcceptCertificateTrust(sc);
        var main = LoginFlow.WaitForSmartClientMain(sc);
        SmartClientUi.Maximize(main);
        Thread.Sleep(1500);
        main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
        main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
        var item = SmartClientUi.WaitForMipViewItem(main);
        if (item == null) throw new InvalidOperationException("no MIP view item after workspace selection");
        return sc;
    }

    private static void Retire(ClientSession mc, Window mcMain)
    {
        try { mcMain.Close(); } catch { }
        ManagementClientUi.AcceptPendingModal(mc, 5);
        ProcessKiller.KillClients("VideoOS.Administration");
    }

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name; }
        catch { return element.Name; }
    }

    private static string Summarize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "<none>";
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Substring(0, Math.Min(220, flat.Length));
    }
}
