using System;
using System.Collections.Generic;
using System.Net.Security;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #54 server-side hardening: the Event Server fetcher refuses urls outside the union
    /// of every proxied workspace's allowlist, so a crafted fetch request cannot drive the server
    /// network at arbitrary targets. The snapshot comes from workspace item properties in
    /// production; these tests feed property dictionaries directly.
    /// </summary>
    public class ProxyServerAllowlistTests
    {
        private static readonly Action<string> Sink = _ => { };

        private static Dictionary<string, string> WorkspaceProperties(bool enabled, params string[] targetJsonObjects)
        {
            var targetsJson = "[" + string.Join(",", targetJsonObjects) + "]";
            return new Dictionary<string, string>
            {
                ["WorkspaceConfig"] =
                    "{\"version\":1,\"layout\":\"1x1\",\"items\":[{\"type\":\"WebContent\",\"position\":0,\"settings\":" +
                    "{\"homeAddress\":\"http://x/\",\"proxyEnabled\":" + (enabled ? "true" : "false") +
                    ",\"proxyTargets\":" + targetsJson + "}}]}",
            };
        }

        private static string Target(string scheme, string host, int port) =>
            $"{{\"scheme\":\"{scheme}\",\"host\":\"{host}\",\"port\":{port}}}";

        private static string Target(string scheme, string host, int port, bool acceptUntrusted) =>
            $"{{\"scheme\":\"{scheme}\",\"host\":\"{host}\",\"port\":{port},\"acceptUntrusted\":{(acceptUntrusted ? "true" : "false")}}}";

        // #73: the new stored shape - the structured pattern member, legacy host omitted.
        private static string PatternTarget(string scheme, string pattern, int port, bool acceptUntrusted = false) =>
            $"{{\"scheme\":\"{scheme}\",\"pattern\":\"{pattern}\",\"port\":{port},\"acceptUntrusted\":{(acceptUntrusted ? "true" : "false")}}}";

        [Fact]
        public void ForwardsOriginsAllowlistedByAnyProxiedWorkspace()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true, Target("http", "10.0.0.5", 8080)),
                    WorkspaceProperties(true, Target("https", "portal.test", 443)),
                },
                Sink);

            Assert.True(allowlist.IsAllowed(new Uri("http://10.0.0.5:8080/monitor/live"), out _));
            Assert.True(allowlist.IsAllowed(new Uri("https://portal.test/dashboard"), out _));
        }

        [Fact]
        public void RefusesOriginsNoProxiedWorkspaceAllowlists()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true, Target("http", "10.0.0.5", 8080)),
                },
                Sink);

            Assert.False(allowlist.IsAllowed(new Uri("http://metadata.internal.latest/"), out var reason));
            Assert.Contains("allowlist", reason);
        }

        [Fact]
        public void TargetsOfWorkspacesWithTheProxyOffDoNotCount()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(false, Target("http", "10.0.0.5", 8080)),
                },
                Sink);

            Assert.False(allowlist.IsAllowed(new Uri("http://10.0.0.5:8080/"), out _));
        }

        [Fact]
        public void FailsClosedWhenTheConfigurationHasNeverResolved()
        {
            var allowlist = new ProxyServerAllowlist(
                () => throw new InvalidOperationException("Configuration service is not available."),
                Sink);

            Assert.False(allowlist.IsAllowed(new Uri("http://10.0.0.5:8080/"), out var reason));
            Assert.Contains("Configuration", reason);
        }

        [Fact]
        public void UntrustedCertificateOverrideIsFalseForEveryTargetWithoutTheFlag()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true,
                        Target("https", "cam1.test", 8443),
                        Target("https", "cam2.test", 8443, false)),
                },
                Sink);

            Assert.False(allowlist.AcceptsUntrustedCertificate(new Uri("https://cam1.test:8443/live")));
            Assert.False(allowlist.AcceptsUntrustedCertificate(new Uri("https://cam2.test:8443/live")));
        }

        [Fact]
        public void UntrustedCertificateOverrideIsTrueOnlyForTheOriginWhoseEntryCarriesIt()
        {
            // The AC3 shape: one flagged entry, one unflagged sibling on the SAME host pair —
            // the override must never leak from a target to its neighbours.
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true,
                        Target("https", "cam1.test", 8443, true),
                        Target("https", "cam1.test", 9443, false)),
                    WorkspaceProperties(true, Target("https", "portal.test", 443)),
                },
                Sink);

            Assert.True(allowlist.AcceptsUntrustedCertificate(new Uri("https://cam1.test:8443/live/index.html")));
            Assert.False(allowlist.AcceptsUntrustedCertificate(new Uri("https://cam1.test:9443/live/index.html")));
            Assert.False(allowlist.AcceptsUntrustedCertificate(new Uri("https://portal.test/dashboard")));
        }

        [Fact]
        public void UntrustedCertificateOverrideFollowsTheAllowlistGate()
        {
            // Proxy off: the entry counts for neither the allowlist nor the override. Config
            // never resolved: the gate is closed and the override set stays empty (fail closed).
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(false, Target("https", "cam1.test", 8443, true)),
                },
                Sink);

            Assert.False(allowlist.AcceptsUntrustedCertificate(new Uri("https://cam1.test:8443/")));

            var closed = new ProxyServerAllowlist(
                () => throw new InvalidOperationException("Configuration service is not available."),
                Sink);

            Assert.False(closed.AcceptsUntrustedCertificate(new Uri("https://cam1.test:8443/")));
        }

        [Fact]
        public void KeepsServingTheLastKnownAllowlistWhenTheConfigServiceFlaps()
        {
            var resolverThrows = false;
            var allowlist = new ProxyServerAllowlist(
                () =>
                {
                    if (resolverThrows)
                    {
                        throw new InvalidOperationException("transient");
                    }

                    return new List<Dictionary<string, string>>
                    {
                        WorkspaceProperties(true, Target("http", "10.0.0.5", 8080)),
                    };
                },
                Sink);

            Assert.True(allowlist.IsAllowed(new Uri("http://10.0.0.5:8080/"), out _));
            resolverThrows = true;

            // A transient config-service failure must not break pages that were fine a moment ago,
            // but the stale snapshot is still the gate: unrelated origins stay refused.
            Assert.True(allowlist.IsAllowed(new Uri("http://10.0.0.5:8080/page"), out _));
            Assert.False(allowlist.IsAllowed(new Uri("http://elsewhere.test/"), out _));
        }

        // #73: the ES gate consumes the shared matcher - legacy host records and the new pattern
        // shape gate identically, wildcards honour the apex/subdomain rule, and the VMS floor
        // travels through a resolver seam.

        [Fact]
        public void PatternAndLegacyShapeEntriesGateIdentically()
        {
            var legacy = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true, Target("https", "portal.test", 443)),
                },
                Sink);
            var patterned = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true, PatternTarget("https", "portal.test", 443)),
                },
                Sink);

            foreach (var probe in new[] { "https://portal.test/", "https://portal.test:443/dashboard", "https://portal.test:8443/" })
            {
                Assert.Equal(legacy.IsAllowed(new Uri(probe), out _), patterned.IsAllowed(new Uri(probe), out _));
            }

            Assert.True(patterned.IsAllowed(new Uri("https://portal.test/dashboard"), out _));
        }

        [Fact]
        public void WildcardEntriesAdmitSubdomainsAndNeverTheApexAtTheServerGate()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true,
                        PatternTarget("https", "*.portal.test", 0),
                        PatternTarget("http", "*.cams.local", 8080)),
                },
                Sink);

            Assert.True(allowlist.IsAllowed(new Uri("https://a.portal.test/dashboard"), out _));
            Assert.True(allowlist.IsAllowed(new Uri("https://a.b.portal.test/"), out _));
            Assert.False(allowlist.IsAllowed(new Uri("https://portal.test/"), out _));
            Assert.True(allowlist.IsAllowed(new Uri("http://cam1.cams.local:8080/live"), out _));
            Assert.False(allowlist.IsAllowed(new Uri("http://cam1.cams.local:9090/live"), out _));
        }

        [Fact]
        public void FullHostWildcardRespectsTheVmsFloorAtTheServerGate()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true, PatternTarget("https", "*", 0)),
                },
                Sink,
                () => new[] { "vms.joshooaj.com" });

            Assert.True(allowlist.IsAllowed(new Uri("https://any.example.test/"), out _));
            Assert.False(allowlist.IsAllowed(new Uri("https://vms.joshooaj.com/"), out _));
            Assert.False(allowlist.IsAllowed(new Uri("https://10.20.30.40/"), out _));
            Assert.False(allowlist.IsAllowed(new Uri("https://localhost:22323/"), out _));
        }

        [Fact]
        public void AcceptUntrustedFlagRidesWildcardEntriesUnderTheSameFloor()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true, PatternTarget("https", "*.cams.test", 0, true)),
                    WorkspaceProperties(true, PatternTarget("https", "cam1.cams.test", 0)),
                },
                Sink);

            Assert.True(allowlist.AcceptsUntrustedCertificate(new Uri("https://a.cams.test/live")));
            Assert.False(allowlist.AcceptsUntrustedCertificate(new Uri("https://a.cams.test:9443/live")));
            Assert.False(allowlist.AcceptsUntrustedCertificate(new Uri("https://cams.test/")));
        }

        // #61: the wss tunnel leg's validation posture is decided by the SAME scoped flag lookup
        // the fetch path uses - the callback factory just hands it to one SslStream. Null means
        // SslStream's default full-chain validation (the #60 posture), so every unflagged posture
        // must resolve to null, never to a rejecting-or-accepting hand-written callback.

        [Fact]
        public void TunnelTlsValidationCallbackIsNullForEveryUnflaggedPosture()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true,
                        Target("https", "cam1.test", 8443),
                        Target("https", "cam2.test", 8443, false),
                        Target("http", "cam3.test", 80, true)),
                },
                Sink);

            // No flag, an explicit false, and a flag on the http TWIN of a different origin:
            // the tunnel leg keeps full chain validation for all of them.
            Assert.Null(allowlist.TunnelTlsValidationCallback(new Uri("https://cam1.test:8443/live")));
            Assert.Null(allowlist.TunnelTlsValidationCallback(new Uri("https://cam2.test:8443/live")));
            Assert.Null(allowlist.TunnelTlsValidationCallback(new Uri("https://cam3.test:80/live")));

            // Closed gate (config never resolved): fail closed = default validation.
            var closed = new ProxyServerAllowlist(
                () => throw new InvalidOperationException("Configuration service is not available."),
                Sink);
            Assert.Null(closed.TunnelTlsValidationCallback(new Uri("https://cam1.test:8443/live")));
        }

        [Fact]
        public void TunnelTlsValidationCallbackAcceptsChainErrorsOnlyForTheFlaggedOrigin()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    WorkspaceProperties(true,
                        Target("https", "cam1.test", 8443, true),
                        Target("https", "cam1.test", 9443, false),
                        PatternTarget("https", "*.cams.test", 0, true)),
                },
                Sink);

            var callback = allowlist.TunnelTlsValidationCallback(new Uri("https://cam1.test:8443/live"));
            Assert.NotNull(callback);

            // The #56 AcceptUntrusted posture, pinned at the callback level: an untrusted chain
            // passes on THIS leg only - the unflagged neighbour on the same host pair gets null,
            // so its SslStream keeps default full-chain validation.
            Assert.True(callback(null, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.Null(allowlist.TunnelTlsValidationCallback(new Uri("https://cam1.test:9443/live")));
            Assert.Null(allowlist.TunnelTlsValidationCallback(new Uri("https://other.test:8443/live")));

            // A flagged wildcard entry carries the flag to its subdomains under the same
            // scheme/port exactness (#73) - and the apex, which no wildcard admits, stays default.
            var wildcarded = allowlist.TunnelTlsValidationCallback(new Uri("https://a.cams.test/live"));
            Assert.NotNull(wildcarded);
            Assert.True(wildcarded(null, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.Null(allowlist.TunnelTlsValidationCallback(new Uri("https://cams.test/live")));
            Assert.Null(allowlist.TunnelTlsValidationCallback(new Uri("https://a.cams.test:9443/live")));
        }
    }
}
