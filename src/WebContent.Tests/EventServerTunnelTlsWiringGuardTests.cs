using System.IO;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #61 live-shape guards: the Event Server must WIRE the wss tunnel's TLS validation
    /// seam to the allowlist's per-target accept-untrusted flag. The wiring itself lives in the
    /// host-loaded BackgroundPlugin (unreachable by behavioural unit tests), so these source pins
    /// hold the composition: the tunnel server is constructed with the resolver (never the null
    /// #60 slot), the resolver asks the allowlist for exactly this target's callback (the same
    /// scoped decision the fetch path makes), and nothing in the fetcher touches the process-wide
    /// ServicePointManager. The behavioural postures sit in ProxyServerAllowlistTests and the
    /// wired-resolver rows of ProxyTunnelServerTests; the live rows are SC-6 in
    /// docs/verification-matrix.md.
    /// </summary>
    public class EventServerTunnelTlsWiringGuardTests
    {
        private static string ReadFetcher()
        {
            return File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "ProxyFetchBackgroundPlugin.cs"));
        }

        [Fact]
        public void TheTunnelServerIsConstructedWithTheTlsResolverNotTheNullSlot()
        {
            var collapsed = System.Text.RegularExpressions.Regex.Replace(ReadFetcher(), @"\s+", " ");

            // The exact #61 construction: limits, log, the resolver, the traffic seam - the
            // resolver slot is never null again (that was the #60 pre-wiring posture).
            Assert.Contains(
                "new ProxyTunnelServer( ProxyTunnelLimits.Default(), message => EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), message), ResolveTunnelTlsValidation,",
                collapsed);
        }

        [Fact]
        public void TheResolverAsksTheAllowlistForThisTargetsCallbackAlone()
        {
            var fetcher = ReadFetcher();

            // One scoped lookup: the allowlist factory, keyed by THIS tunnel target's uri. A
            // hand-written always-true callback here would be the global-bypass shape the ticket
            // forbids, so the delegation is what is pinned.
            Assert.Contains("RemoteCertificateValidationCallback ResolveTunnelTlsValidation(Uri target)", fetcher);
            Assert.Contains("allowlist.TunnelTlsValidationCallback(target)", fetcher);
        }

        [Fact]
        public void NeitherTunnelNorFetchTlsDecisionTouchesTheProcessWideValidationCallback()
        {
            // #56 pinned this for the fetch leg; #61 must not break it for the tunnel leg: the
            // plugin lives inside the Event Server process and may never weaken TLS for anyone
            // else who shares it.
            var fetcher = ReadFetcher();

            Assert.DoesNotContain("ServicePointManager", fetcher);
        }
    }
}
