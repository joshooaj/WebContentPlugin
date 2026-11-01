using FlaUI.Core.AutomationElements;
using Microsoft.Playwright;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Diagnostic probe (#58, verification-matrix SC-5): the proxied Smart Client view must show an
/// EXPLICIT proxy error page - never a spinner, never a page fetched direct from the client
/// machine - for the two refusal families:
///   1. a start (home) URL whose origin is not on the workspace proxy allowlist renders the 403
///      page as the workspace's start document;
///   2. the Event Server (host of the server-side proxy component) going down mid-session renders
///      the 503 page naming that component, and a fresh client session recovers once the service
///      is back.
/// The outage row mutates the live VMS (stops/starts MilestoneEventServerService over WinRM,
/// AGENTS.md conventions) and only runs with WEBCONTENT_ES_STOP=1. Artifacts land in
/// webcontent-sc-proxy-probe\&lt;run&gt; like the other proxy probes.
/// </summary>
public class ScProxyErrorProbeTests
{
    private const string FetchUrl = "http://httpforever.com/";
    private const string AllowlistEntry = "http://httpforever.com";

    /// <summary>
    /// A plain-HTTP origin deliberately NOT on the allowlist: as the workspace home it exercises
    /// the start-URL refusal without the CONNECT posture (https refusals render at the Chromium
    /// error surface, so they would not prove OUR page).
    /// </summary>
    private const string OffAllowlistHome = "http://neverssl.com/";

    /// <summary>
    /// Split live-host run (SC-5): the Management Client phase and the Smart Client view may run on
    /// different boxes against the same VMS, because the workspace state lives on the server. With this
    /// control set the view machine trusts the state the other box prepared and only drives the browser;
    /// whoever sets it owns restoring the workspace home address afterwards.
    /// </summary>
    private static bool SkipMcProvisioning => Environment.GetEnvironmentVariable("UX_SKIP_MC_PROVISIONING") == "1";

    private readonly ITestOutputHelper _output;

    public ScProxyErrorProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeProxiedStartUrlOutsideAllowlistShows403Page()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;
        string title = null;
        string bodyText = null;
        string previousHome = null;
        ClientSession sc = null;
        try
        {
            if (SkipMcProvisioning)
            {
                log.AppendLine("CONTROL: UX_SKIP_MC_PROVISIONING=1 - the workspace allowlist entry and the off-allowlist home " +
                               $"address were applied from another box against the same VMS ({config.VmsAddress}); this run only drives the view.");
            }
            else
            {
                ProxyTargetProvisioning.Ensure(config, AllowlistEntry, log);
                previousHome = WorkspaceHomeAddress.Ensure(config, OffAllowlistHome, log);
            }

            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            sc = ClientSession.Launch(config.SmartClientExe);
            var login = LoginFlow.WaitForLoginWindow(sc);
            LoginFlow.Login(sc, login, config);
            LoginFlow.AcceptCertificateTrust(sc);
            var main = LoginFlow.WaitForSmartClientMain(sc);
            SmartClientUi.Maximize(main);
            Thread.Sleep(1500);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

            main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(main);
            Assert.NotNull(item);

            // The fingerprint guard restarts the browser at the NEW home after a definition edit;
            // the first document is the refusal page itself - no address-bar interaction.
            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, "neverssl.com", timeoutSeconds: 90);
            await page.WaitForSelectorAsync("body", new PageWaitForSelectorOptions { Timeout = 30000 });

            title = await page.TitleAsync();
            var body = await page.QuerySelectorAsync("body");
            bodyText = body == null ? null : await body.TextContentAsync();
            log.AppendLine($"start document -> title='{title}'");
            log.AppendLine($"body: {Summarize(bodyText)}");

            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifacts.PathOf("sc-error-probe-starturl403.png") });
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}{Environment.NewLine}{ex}");
        }
        finally
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillClients("VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            sc?.Dispose();
            if (previousHome != null)
            {
                try
                {
                    WorkspaceHomeAddress.Ensure(config, previousHome.Trim(), log);
                    log.AppendLine($"workspace home address restored to '{previousHome.Trim()}'");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"!!! home restore FAILED ('{previousHome.Trim()}'): {ex.Message}");
                }
            }
            Artifacts.WriteText("sc-error-probe-starturl403-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
        Assert.Equal("Web Content proxy: 403", title);
        Assert.NotNull(bodyText);
        Assert.Contains("not in the workspace proxy allowlist", bodyText);
    }

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeEventServerOutageShowsProxyErrorPage()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        if (!EventServerControl.OutageRowEnabled)
        {
            throw new InvalidOperationException(
                "control INVALID: this row stops and starts the live Event Server service; run with WEBCONTENT_ES_STOP=1 to opt in");
        }

        var log = new System.Text.StringBuilder();
        string failure = null;
        string healthyTitle = null;
        string outageTitle = null;
        string outageBody = null;
        int outageStatus = -1;
        string recoveredTitle = null;
        string recoveredBody = null;
        ClientSession sc = null;
        ClientSession fresh = null;
        var serviceStopped = false;
        try
        {
            if (SkipMcProvisioning)
                log.AppendLine($"CONTROL: UX_SKIP_MC_PROVISIONING=1 - the '{AllowlistEntry}' allowlist entry was applied from another box against {config.VmsAddress}.");
            else
                ProxyTargetProvisioning.Ensure(config, AllowlistEntry, log);

            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            sc = ClientSession.Launch(config.SmartClientExe);
            var login = LoginFlow.WaitForLoginWindow(sc);
            LoginFlow.Login(sc, login, config);
            LoginFlow.AcceptCertificateTrust(sc);
            var main = LoginFlow.WaitForSmartClientMain(sc);
            SmartClientUi.Maximize(main);
            Thread.Sleep(1500);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

            main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(main);
            Assert.NotNull(item);
            var addressBar = item.FindFirstDescendant(cf => cf.ByAutomationId("addressBar"));
            if (addressBar == null)
            {
                throw new InvalidOperationException("No addressBar in the proxied workspace view; cannot drive the outage probe.");
            }

            Thread.Sleep(6000);
            SmartClientUi.NavigateAddress(addressBar, FetchUrl);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);
            var page = await CdpConnector.WaitForPageAsync(browser, "httpforever.com");
            await page.WaitForSelectorAsync("body", new PageWaitForSelectorOptions { Timeout = 45000 });
            healthyTitle = await page.TitleAsync();
            log.AppendLine($"baseline (Event Server UP) -> title='{healthyTitle}'");
            if (healthyTitle?.StartsWith("Web Content proxy:") == true)
            {
                throw new InvalidOperationException($"baseline is already a proxy error page: '{healthyTitle}'");
            }
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifacts.PathOf("sc-error-probe-healthy.png") });

            // The demo itself: the whole server-side proxy component disappears; the next reload
            // must show the explicit proxy error page, not a spinner and not a direct hit.
            EventServerControl.Stop(config, log);
            serviceStopped = true;
            log.AppendLine("Event Server stopped; reloading the proxied page...");

            try
            {
                var response = await page.ReloadAsync(new PageReloadOptions { Timeout = 60000 });
                outageStatus = response?.Status ?? -1;
                log.AppendLine($"reload response status: {outageStatus}");
            }
            catch (PlaywrightException reloadError)
            {
                log.AppendLine($"reload surfaced '{reloadError.Message}' before committing; inspecting the committed document anyway");
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            do
            {
                outageTitle = await page.TitleAsync();
                if (outageTitle?.StartsWith("Web Content proxy:") == true) break;
                await Task.Delay(1000);
            } while (DateTime.UtcNow < deadline);

            var body = await page.QuerySelectorAsync("body");
            outageBody = body == null ? null : await body.TextContentAsync();
            log.AppendLine($"reload (Event Server DOWN) -> title='{outageTitle}'");
            log.AppendLine($"body: {Summarize(outageBody)}");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Artifacts.PathOf("sc-error-probe-es-down.png") });

            // AC4 evidence (soft): the Smart Client MIP log names the failing side. The file can
            // be mid-rotation or logging disabled, so absence is recorded, not asserted.
            log.AppendLine(MipLogContains("ServerUnavailable")
                ? "MIPLog.txt marker found: Proxy fetch ... failed on the Smart Client side ... (code=ServerUnavailable)"
                : "NOTE: no 'ServerUnavailable' marker found in the Smart Client MIPLog.txt snapshot (logging may be disabled or rotated)");

            // Recovery half of the demo: the restart disposes every connected client's messaging
            // channel (AGENTS.md), so recovery is a FRESH Smart Client session, not a reload of
            // this one. The new session must get the real page back - no error page, no spinner.
            EventServerControl.Start(config, log);
            serviceStopped = false;
            Thread.Sleep(10000);   // the fetcher's plugin load happens during service start

            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            sc.Dispose();
            sc = null;
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);

            fresh = ClientSession.Launch(config.SmartClientExe);
            var freshLogin = LoginFlow.WaitForLoginWindow(fresh);
            LoginFlow.Login(fresh, freshLogin, config);
            LoginFlow.AcceptCertificateTrust(fresh);
            var freshMain = LoginFlow.WaitForSmartClientMain(fresh);
            SmartClientUi.Maximize(freshMain);
            Thread.Sleep(1500);
            freshMain = fresh.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            freshMain = SmartClientUi.SelectWorkspace(freshMain, fresh, config.WorkspaceName);
            var freshItem = SmartClientUi.WaitForMipViewItem(freshMain);
            Assert.NotNull(freshItem);
            var freshAddressBar = freshItem.FindFirstDescendant(cf => cf.ByAutomationId("addressBar"));
            if (freshAddressBar == null)
            {
                throw new InvalidOperationException("No addressBar in the recovered session's view; cannot prove recovery.");
            }

            Thread.Sleep(6000);
            SmartClientUi.NavigateAddress(freshAddressBar, FetchUrl);
            await using (var recovered = new CdpConnector())
            {
                var recoveredBrowser = await recovered.ConnectFirstAvailableAsync(config.WebView2DataRoot);
                var recoveredPage = await CdpConnector.WaitForPageAsync(recoveredBrowser, "httpforever.com");
                await recoveredPage.WaitForSelectorAsync("body", new PageWaitForSelectorOptions { Timeout = 45000 });
                recoveredTitle = await recoveredPage.TitleAsync();
                var recoveredBodyElement = await recoveredPage.QuerySelectorAsync("body");
                recoveredBody = recoveredBodyElement == null ? null : await recoveredBodyElement.TextContentAsync();
                log.AppendLine($"fresh session after the restart -> title='{recoveredTitle}'");
                log.AppendLine($"body: {Summarize(recoveredBody)}");
                await recoveredPage.ScreenshotAsync(new PageScreenshotOptions { Path = Artifacts.PathOf("sc-error-probe-recovered.png") });
            }
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}{Environment.NewLine}{ex}");
        }
        finally
        {
            if (serviceStopped)
            {
                try
                {
                    EventServerControl.Start(config, log);
                    // The fetcher needs its service-start plugin load; give it the documented head start.
                    Thread.Sleep(10000);
                }
                catch (Exception ex)
                {
                    log.AppendLine($"!!! Event Server START FAILED - manual recovery required on {config.VmsAddress}: {ex.Message}");
                }
            }
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillClients("VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            sc?.Dispose();
            fresh?.Dispose();
            Artifacts.WriteText("sc-error-probe-es-outage-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
        Assert.NotNull(outageTitle);
        Assert.Equal("Web Content proxy: 503", outageTitle);
        Assert.NotNull(outageBody);
        Assert.Contains("VMS server proxy component is not available", outageBody);
        // Recovery: a session started after the service is back gets the real document again.
        Assert.NotNull(recoveredTitle);
        Assert.False(recoveredTitle.StartsWith("Web Content proxy:"),
            $"a fresh session after the restart still shows a proxy error page: '{recoveredTitle}'");
        Assert.Equal(healthyTitle, recoveredTitle);
        Assert.DoesNotContain("Web Content proxy:", recoveredBody);
    }

    private static string Summarize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "<none>";
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Substring(0, Math.Min(220, flat.Length));
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
