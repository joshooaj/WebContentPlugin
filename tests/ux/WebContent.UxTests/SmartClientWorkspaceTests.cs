using FlaUI.Core.WindowsAPI;
using WebContent.UxTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Smart Client UX: log in, open the configured Web Content workspace, drive the
/// toolbar/address bar, and verify the rendered page through the WebView2 CDP endpoint.
/// </summary>
public class SmartClientWorkspaceTests
{
    private readonly ITestOutputHelper _output;

    public SmartClientWorkspaceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "ux")]
    public async Task WebContentWorkspace_RendersNavigatesAndRecovers()
    {
        var config = UxConfig.Load();
        Artifacts.Init(config.ArtifactsRoot);
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        using var probe = new ProbePageServer();
        CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
        ProcessKiller.KillClients("Client");
        ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

        using var sc = ClientSession.Launch(config.SmartClientExe);
        var login = LoginFlow.WaitForLoginWindow(sc);
        Artifacts.Window(login, "sc-login");
        LoginFlow.Login(sc, login, config);
        LoginFlow.AcceptCertificateTrust(sc);
        var main = LoginFlow.WaitForSmartClientMain(sc);
        SmartClientUi.Maximize(main);
        Thread.Sleep(1500);
        main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

        try
        {
            main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(main);
            Artifacts.Element(item, "sc-workspace-tab");
            Assert.NotNull(item);

            var addressBar = item.FindFirstDescendant(cf => cf.ByAutomationId("addressBar"))
                ?? throw new InvalidOperationException("No addressBar in the plugin view.");

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot);

            addressBar.Click();
            Thread.Sleep(400);
            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.CONTROL);
            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.KEY_A);
            FlaUI.Core.Input.Keyboard.Release(VirtualKeyShort.CONTROL);
            FlaUI.Core.Input.Keyboard.Type(probe.Base + "alpha");
            Thread.Sleep(300);
            FlaUI.Core.Input.Keyboard.Press(VirtualKeyShort.RETURN);

            var page = await CdpConnector.WaitForPageAsync(browser, probe.Base + "alpha");
            var consoleErrors = new List<string>();
            page.Console += (_, msg) =>
            {
                if (msg.Type == "error") consoleErrors.Add(msg.Text);
            };

            var marker = await page.WaitForSelectorAsync("#probe-marker", new Microsoft.Playwright.PageWaitForSelectorOptions { Timeout = 30000 });
            Assert.NotNull(marker);
            Assert.Equal("UX-PROBE-ALPHA", await marker.TextContentAsync());
            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = Artifacts.PathOf("sc-probe-alpha.png"),
            });
            Assert.Empty(consoleErrors);
            Artifacts.Window(sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow"), "sc-probe-alpha-window");

            SmartClientUi.NavigateAddress(addressBar, "definitely not a url");
            Thread.Sleep(3000);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            var warning = main.FindFirstDescendant(cf => cf.ByAutomationId("addressWarning"));
            Artifacts.Element(main.FindFirstDescendant(cf => cf.ByClassName("WebContentUserControl")), "sc-invalid-address");
            Assert.NotNull(warning);

            var home = item.FindFirstDescendant(cf => cf.ByAutomationId("NavHome"));
            home.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(4000);
            await CdpConnector.WaitForAnyPageAsync(browser, new[] { config.HomeAddressFragment, "auth.joshooaj.com" }, 45);
            Artifacts.Element(main.FindFirstDescendant(cf => cf.ByClassName("WebContentUserControl")), "sc-home-restored");
        }
        catch
        {
            var fresh = sc.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow");
            if (fresh != null)
            {
                Artifacts.Window(fresh, "sc-failure-state");
                Artifacts.WriteText("sc-failure-tree.txt", TreeDump.Of(fresh, 12));
            }
            throw;
        }
        finally
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }
    }
}
