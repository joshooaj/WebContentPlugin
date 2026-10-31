using System;
using System.Collections.Generic;
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
    }
}
