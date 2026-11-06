using System.IO;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #87 live-shape guards: the ownership notice is rendered by WPF views and driven by
    /// WebView2 init events the unit harness can never host, so these source pins hold the
    /// composition the live two-client probe (SC-13) drives - the failed PROXIED initialization
    /// routes through the pure classifier into ONE browser-owned log line and the in-view
    /// notice, the notice elements exist and start collapsed, the blank WebView steps aside
    /// while the notice stands, Refresh on an errored proxied view re-attempts creation through
    /// the re-creation path (and NEVER Reload), and a recreation while the view is open re-attaches
    /// the fresh control. The decisions themselves sit in BrowserInitFailureTests and
    /// BrowserCommandAdmissionTests; the live row is in docs/verification-matrix.md.
    /// </summary>
    public class ProxiedInitErrorWiringGuardTests
    {
        private static string ReadClient(string file)
        {
            return File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "Client", file));
        }

        private static string Collapse(string source)
        {
            return System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ");
        }

        [Fact]
        public void AFailedProxiedInitializationPublishesTheContractExactlyOnce()
        {
            var collapsed = Collapse(ReadClient("BrowserInstance.cs"));

            // The event handler routes the failure through the pure classifier...
            Assert.Contains("if (BrowserInitFailure.TryPresent(IsProxied, Id, e.InitializationException?.Message, out var presentation))", collapsed);
            // ...through the publisher that logs the ONE presentation line and raises PropertyChanged,
            // and the non-proxied branch keeps its unchanged log-only line.
            Assert.Contains("PublishInitFailure(presentation);", collapsed);
            Assert.Contains("EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), presentation.LogLine);", collapsed);
            Assert.Contains("OnPropertyChanged(nameof(InitFailure));", collapsed);
            Assert.Contains(
                "EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), $\"CoreWebView2 initialization failed. InitializationException: {e.InitializationException?.Message}\");",
                collapsed);

            // A new attempt retires the stale notice; success clears it.
            Assert.Contains("if (InitFailure != null) { InitFailure = null; OnPropertyChanged(nameof(InitFailure)); }", collapsed);
        }

        [Fact]
        public void RefreshOnAnErroredProxiedViewRecreatesAndNeverReloadsDead()
        {
            var collapsed = Collapse(ReadClient("BrowserCommand.cs"));

            // Admission: the recreation route is vouched for only by proxied + NeedsRecreation.
            Assert.Contains("instance != null && instance.IsProxied && instance.NeedsRecreation;", collapsed);
            Assert.Contains("CanRecreateProxiedProfile(_instance));", collapsed);

            // Execute: Reload only with a live CoreWebView2; the proxied error state routes to
            // the ViewModel's re-creation entry point; everything else keeps the refused-log path.
            Assert.Contains("_instance.WebView.Reload();", collapsed);
            Assert.Contains("else if (CanRecreateProxiedProfile(_instance))", collapsed);
            Assert.Contains("_viewModel.RecreateBrowserForRefresh();", collapsed);
            Assert.Contains("LogUninitialized(_action);", collapsed);
        }

        [Fact]
        public void TheRefreshRecreationUsesTheSameAddressAndIsolationRuleAsViewInit()
        {
            var collapsed = Collapse(ReadClient("WebContentViewModel.cs"));

            // Same restore-address fallback and same isolation setting the view's Init path uses.
            Assert.Contains("var address = ResolveRestoreAddress() ?? Home;", collapsed);
            Assert.Contains("browser.RecreateWebView(", collapsed);
            Assert.Contains("_viewItemManager?.Settings.IsolateBrowserInstances ?? false);", collapsed);

            // Failed-init/WebView changes re-query the Refresh gate (no navigation event exists there).
            Assert.Contains("_reloadCommand?.RaiseCanExecuteChanged();", collapsed);
        }

        [Fact]
        public void TheRecreatedWebViewAnnouncesItselfAndTheOpenViewReAttachesIt()
        {
            var instance = Collapse(ReadClient("BrowserInstance.cs"));
            var control = Collapse(ReadClient("WebContentUserControl.xaml.cs"));

            // Recreation fires PropertyChanged(WebView) before re-initializing...
            Assert.Contains("OnPropertyChanged(nameof(WebView));", instance);
            // ...and the open control rebinds + re-adds the fresh control without a re-selection.
            Assert.Contains("if (e.PropertyName == nameof(BrowserInstance.WebView))", control);
            Assert.Contains("AttachRecreatedWebView();", control);
        }

        [Fact]
        public void TheNoticeElementsExistStartCollapsedAndCarryProbeAutomationIds()
        {
            var xaml = ReadClient("WebContentUserControl.xaml");

            // The browser host became a Grid so the notice can occupy the WebView's area.
            Assert.Contains("<Grid Name=\"Panel\" Background=\"DimGray\"", xaml);

            // One notice container + the labelled texts, all discovered by the live probe via
            // these AutomationIds (x:Name alone has no proven peer on bare TextBlocks).
            Assert.Contains("Name=\"profileOwnershipError\" Visibility=\"Collapsed\"", xaml);
            Assert.Contains("AutomationProperties.AutomationId=\"profileOwnershipCause\"", xaml);
            Assert.Contains("AutomationProperties.AutomationId=\"profileOwnershipHeadline\"", xaml);
            Assert.Contains("AutomationProperties.AutomationId=\"profileOwnershipText\"", xaml);
            Assert.Contains("AutomationProperties.AutomationId=\"profileOwnershipDetail\"", xaml);
        }

        [Fact]
        public void TheNoticeFollowsTheBrowserAndTheBlankWebViewStepsAside()
        {
            var control = Collapse(ReadClient("WebContentUserControl.xaml.cs"));

            // Bound per browser instance (survives recreation), unbound on close, bound LAST in
            // Init so a browser that already failed shows the notice on view selection.
            Assert.Contains("browser.PropertyChanged += Browser_ProxiedStateChanged;", control);
            Assert.Contains("UnbindInitFailure();", control);
            Assert.Contains("BindInitFailure(browser);", control);

            // While the notice stands the WebView2 control (an HWND host nothing composes over)
            // is collapsed to its notice; when the failure clears the BrowserVisibility binding
            // comes back.
            Assert.Contains("profileOwnershipText.Text = failure.Explanation;", control);
            Assert.Contains("profileOwnershipError.Visibility = Visibility.Visible;", control);
            Assert.Contains("CollapseBrowserWebView();", control);
            Assert.Contains("webView.SetValue(VisibilityProperty, Visibility.Collapsed);", control);
            Assert.Contains("RebindBrowserWebViewVisibility();", control);
        }

        [Fact]
        public void TheClassifierLandsInBothCompileLists()
        {
            var root = RepoLocator.FindRepoRoot();

            // Plugin side: SDK-style default glob - the file must simply exist in the plugin tree.
            Assert.True(File.Exists(Path.Combine(root, "src", "WebContent", "Client", "BrowserInitFailure.cs")));

            // Test side: the explicit Compile link (AGENTS rule - land it in BOTH or one side
            // silently misses the class).
            var testCsproj = File.ReadAllText(Path.Combine(root, "src", "WebContent.Tests", "WebContent.Tests.csproj"));
            Assert.Contains(
                "<Compile Include=\"..\\WebContent\\Client\\BrowserInitFailure.cs\" Link=\"BrowserInitFailure.cs\" />",
                testCsproj);
        }
    }
}
