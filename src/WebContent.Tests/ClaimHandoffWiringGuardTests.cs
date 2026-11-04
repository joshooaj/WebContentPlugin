using System.IO;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #96 wiring guards: when a boundary's listener owner dies mid-session, the surviving
    /// joined view heals WITHOUT user action. The heartbeat, the pure decision call, the UI-thread
    /// dispatch, and the healing routes all live in the WPF/socket-coupled classes the unit harness
    /// cannot host, so these source pins hold the composition the SC-15 live probes drive: every
    /// proxied entry point arms the watchdog, the watch consults through the SAME claim protocol
    /// (never a release side-door), handoff re-creation rides ONLY the #87 RecreateWebView route,
    /// and the retry budget/grace bound the cycle. The decisions themselves sit in
    /// ClaimWatchCoreTests; the live rows sit in docs/verification-matrix.md SC-15.
    /// </summary>
    public class ClaimHandoffWiringGuardTests
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
        public void EveryProxiedEntryPointArmsTheWatchAndTeardownStopsIt()
        {
            var collapsed = Collapse(ReadClient("BrowserManager.cs"));

            // Both proxied entry points (creation and the mid-session proxy re-enable) arm the
            // idempotent heartbeat, and exactly ONE stop site exists: the plugin teardown.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(collapsed, @"ProxyListenerClaimWatchdog\.EnsureStarted\(\);").Count);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(collapsed, @"ProxyListenerClaimWatchdog\.Stop\(\);").Count);
            Assert.Contains("public static void StopAllProxyListeners()", collapsed);

            // The watchdog's tick is guarded end to end - a throwing watch never kills the timer.
            var watchdog = Collapse(ReadClient("ProxyListenerClaimWatchdog.cs"));
            Assert.Contains("BrowserManager.WatchBoundaryClaims();", watchdog);
        }

        [Fact]
        public void TheWatchDecidesThroughThePureCoreWithTheProductionLivenessProbes()
        {
            var collapsed = Collapse(ReadClient("BrowserManager.cs"));

            // One Decide call, fed the #93 store's OWN production probes - the watch's staleness
            // verdict can never disagree with the claim protocol's own liveness rule.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(collapsed, @"ClaimWatchCore\.Decide\(").Count);
            Assert.Contains("ClaimWatchCore.Decide(file, facts, ListenerClaimStore.ProcessExists, ListenerClaimStore.LoopbackPortConnectable)", collapsed);

            // Only CLAIMED browsers are watched (epoch 0 = unclaimed ephemeral fallback: nothing
            // to hold), and the healing branches all exist in the dispatch switch.
            Assert.Contains("browser.ProxyClaimEpoch < 1", collapsed);
            Assert.Contains("case ClaimWatchAction.Republish:", collapsed);
            Assert.Contains("case ClaimWatchAction.Rewire:", collapsed);
            Assert.Contains("case ClaimWatchAction.Retry:", collapsed);

            // WebView work is dispatched to the UI thread - the timer thread never touches views.
            Assert.Contains("app.Dispatcher.BeginInvoke(new Action(() => PerformHandoffRewire(browser, claimPath)))", collapsed);

            // (measured live, #96) the view-liveness read the watch performs FROM THE TIMER THREAD
            // hops through the control's dispatcher - a raw CoreWebView2 read off the owning
            // thread throws VerifyAccess and silently kills every watch tick.
            var instance = Collapse(ReadClient("BrowserInstance.cs"));
            Assert.Contains("web.Dispatcher.CheckAccess()", instance);
            Assert.Contains("web.Dispatcher.Invoke(() => IsInitialized && web.CoreWebView2 != null && InitFailure == null)", instance);
        }

        [Fact]
        public void TheHandoffReclaimsThroughTheSeamAndRecreatesThroughTheOnlyRoute()
        {
            var collapsed = Collapse(ReadClient("BrowserManager.cs"));

            // The owner-side slot repair pins the port ACTUALLY served (joiners' baked arguments
            // stay truthful) and keeps the generation instead of resetting it.
            Assert.Contains("ProxyBrowserArguments.PortIndependentFingerprint(), browser.ProxyClaimEpoch, served)", collapsed);

            // The joined-side handoff re-claims at a bumped floor and pins its own live port when
            // it is the owner repairing the slot.
            Assert.Contains("Math.Max(1, browser.ProxyClaimEpoch + 1)", collapsed);
            Assert.Contains("browser.ProxyClaimPath != null ? (int?)(browser.ProxyListener?.Port ?? browser.ProxyClaimPort) : null", collapsed);

            // A settle failure changes NOTHING (a claim never outlives a lie, neither does a rewire).
            Assert.Contains("if (lease.Role == ProxyListenerLeaseRole.UnclaimedFallback)", collapsed);

            // ONE re-creation call site in the whole manager - the #87 route - and it is the
            // handoff's. No second recreation path exists.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(collapsed, @"RecreateWebView\(").Count);
            Assert.Contains("browser.RecreateWebView(", collapsed);
            Assert.Contains("browser.ProxyHandoffAttempts = argumentsChanged ? 1 : browser.ProxyHandoffAttempts + 1;", collapsed);
        }

        [Fact]
        public void TheConsumedPostureCarriesTheFreshLeasesEpochAndOwner()
        {
            var collapsed = Collapse(ReadClient("BrowserManager.cs"));

            // Both posture branches stamp what the browser now actually rides; the joined branch
            // records the FOREIGN owner pid (the watch's liveness rule rides it next tick).
            Assert.Contains("browser.ProxyClaimEpoch = lease.Epoch", collapsed);
            Assert.Contains("browser.ProxyClaimOwnerPid = lease.OwnerPid", collapsed);
        }

        [Fact]
        public void TheCoreIsPureAndItsBudgetOutlastsTheMeasuredRespawnWindow()
        {
            var core = Collapse(ReadClient("ClaimWatchCore.cs"));

            // #90 measured ~13 s for a destroyed collection to respawn with fresh browser
            // arguments; the grace must stay above that, and the budget is finite per generation.
            Assert.Contains("ReCreationGraceMs = 15000", core);
            Assert.Contains("MaxHandoffRetries = 5", core);

            // Pure means unit-testable: no timer, no WPF, no filesystem, no sockets in the core.
            Assert.DoesNotContain("Timer", core);
            Assert.DoesNotContain("Dispatcher", core);
            Assert.DoesNotContain("System.Windows", core);
            Assert.DoesNotContain("File.", core);
            Assert.DoesNotContain("TcpClient", core);
        }

        [Fact]
        public void TheDecisionCoreLandsInBothCompileListsButTheTimerStaysPluginOnly()
        {
            var root = RepoLocator.FindRepoRoot();

            // Plugin side: SDK-style default glob - both files must simply exist in the plugin tree.
            Assert.True(File.Exists(Path.Combine(root, "src", "WebContent", "Client", "ClaimWatchCore.cs")));
            Assert.True(File.Exists(Path.Combine(root, "src", "WebContent", "Client", "ProxyListenerClaimWatchdog.cs")));

            // Test side: the CORE is linked (AGENTS rule - land it in BOTH or one side silently
            // misses the class); the WPF-adjacent timer is deliberately NOT (nothing to test there
            // beyond the pins above).
            var testCsproj = File.ReadAllText(Path.Combine(root, "src", "WebContent.Tests", "WebContent.Tests.csproj"));
            Assert.Contains(
                "<Compile Include=\"..\\WebContent\\Client\\ClaimWatchCore.cs\" Link=\"ClaimWatchCore.cs\" />",
                testCsproj);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(testCsproj, "Compile Include=\\\"[^\\\"]*ClaimWatchCore\\.cs").Count);
            Assert.DoesNotContain("ProxyListenerClaimWatchdog.cs", testCsproj);
        }
    }
}
