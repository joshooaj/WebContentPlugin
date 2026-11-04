using System.IO;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #95 wiring guards: the claim consult, the join/own branch, and the release side all
    /// live in the WPF/socket-coupled classes the unit harness cannot host, so these source pins
    /// hold the composition the SC-14 live probe drives - the proxied browser consults the claim
    /// BEFORE binding anything, a JOINED browser binds no listener and carries the owner's port,
    /// an OWNER binds exactly the claimed port, every claim write is paired with a release path,
    /// and the #88 view gate survives a listener-less joined browser. The consult/claim/join
    /// decisions themselves sit in ProxyListenerLeaseTests; the live rows sit in
    /// docs/verification-matrix.md SC-14.
    /// </summary>
    public class ListenerJoinWiringGuardTests
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
        public void EveryProxiedCreationAndReenableConsultsTheClaimBeforeAnythingBinds()
        {
            var collapsed = Collapse(ReadClient("BrowserManager.cs"));

            // One lease provider, consulted from BOTH proxied entry points (creation and the
            // mid-session proxy re-enable), each keyed to the boundary claim path.
            Assert.Contains("private static readonly ProxyListenerLeaseProvider _leases = new ProxyListenerLeaseProvider();", collapsed);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(collapsed, @"BrowserProfileIdentity\.GetListenerClaimPath\(").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(collapsed, @"_leases\.Acquire\(").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(collapsed, @"ProxyBrowserArguments\.PortIndependentFingerprint\(\)").Count);

            // Every claim audit the store handed back is journalled verbatim, both entry points.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(collapsed, @"foreach \(var audit in lease\.AuditLines\)").Count);
        }

        [Fact]
        public void AJoinedBrowserBindsNoListenerAndCarriesTheOwnersPortVerbatim()
        {
            var collapsed = Collapse(ReadClient("BrowserManager.cs"));

            // Both entry points branch on the lease role, and the JOIN branch composes the
            // identical argument from the owner's port with NO listener construction and no claim
            // path recorded (the joiner owns nothing).
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(collapsed, @"if \(lease\.Role == ProxyListenerLeaseRole\.Joined\)").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(collapsed, @"ProxyBrowserArguments\.For\(lease\.Port\)").Count);
            Assert.Contains("joined the boundary owner's loopback listener", collapsed);

            // The owner route always runs through the lease-aware starter, never a bare Start().
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(collapsed, @"StartListenerOnLeasedPort\(listener, lease, claimPath, ").Count);
        }

        [Fact]
        public void TheOwnerBindsExactlyTheClaimedPortAndReleaseIsPairedWithEveryClaimWrite()
        {
            var manager = Collapse(ReadClient("BrowserManager.cs"));
            var listener = Collapse(ReadClient("LoopbackProxyListener.cs"));

            // Owner: bind the exact published port; a refused bind releases and falls back -
            // a claim never outlives a port nobody serves.
            Assert.Contains("listener.Start(lease.Port);", manager);
            Assert.Contains("_leases.Release(claimPath);", manager);
            Assert.Contains("listener.Start();", manager); // the pre-#95 ephemeral fallback

            // The listener honors the port it is handed (the ONLY change to it).
            Assert.Contains("internal void Start(int port = 0)", listener);
            Assert.Contains("_listener = new TcpListener(IPAddress.Loopback, port);", listener);

            // Release paths: browser creation failure, proxy disable, plugin teardown, and browser
            // Dispose all reach a release; the shared rule only deletes OWN-pid slots.
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(manager, @"_leases\.Release\(").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(manager, @"ReleaseOwnClaim\(browser\);").Count);
            var instance = Collapse(ReadClient("BrowserInstance.cs"));
            Assert.Contains("ProxyListenerLeaseProvider.ReleaseOwnClaimFile(claimPath);", instance);
        }

        [Fact]
        public void TheJoinedBrowserKeepsTheViewGateAndItsTargetsTrackConfigEdits()
        {
            var instance = Collapse(ReadClient("BrowserInstance.cs"));
            var manager = Collapse(ReadClient("BrowserManager.cs"));

            // The #88 gate registers for a listener-less joined browser too (ProxyViewTargets
            // stand in), and the handler resolves the live listener set when one exists.
            Assert.Contains("if (ProxyListener != null || ProxyViewTargets != null)", instance);
            Assert.Contains("listener != null ? listener.CanonicalTargets : ProxyViewTargets", instance);
            Assert.Contains("listener != null ? listener.VmsFloorHosts : ProxyViewFloorHosts", instance);

            // Target edits keep the joined browser's gate copies current (already-proxied branch).
            Assert.Contains("browser.ProxyViewTargets = proxyProfile.CanonicalTargets;", manager);
            Assert.Contains("browser.ProxyViewFloorHosts = proxyProfile.VmsFloorHosts;", manager);
        }

        [Fact]
        public void TheClaimPathIsDeriveOnlyFromTheSameBoundaryLegsAsTheProfile()
        {
            var identity = Collapse(ReadClient("BrowserProfileIdentity.cs"));

            // One leg-resolution call each, identical inputs to GetProxiedUserDataFolder - a
            // claim can never key a different boundary than the profile it sits beside.
            Assert.Contains("BrowserProfileKey.ComposeListenerClaimPath(root, GetCurrentUserKey(), GetCurrentServerKey(), browserId)", identity);
        }

        [Fact]
        public void TheLeaseClassLandsInBothCompileLists()
        {
            var root = RepoLocator.FindRepoRoot();

            // Plugin side: SDK-style default glob - the file must simply exist in the plugin tree.
            Assert.True(File.Exists(Path.Combine(root, "src", "WebContent", "Client", "ProxyListenerLease.cs")));

            // Test side: the explicit Compile link (AGENTS rule - land it in BOTH or one side
            // silently misses the class).
            var testCsproj = File.ReadAllText(Path.Combine(root, "src", "WebContent.Tests", "WebContent.Tests.csproj"));
            Assert.Contains(
                "<Compile Include=\"..\\WebContent\\Client\\ProxyListenerLease.cs\" Link=\"ProxyListenerLease.cs\" />",
                testCsproj);
        }
    }
}
