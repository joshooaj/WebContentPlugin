using System;
using System.Collections.Generic;

using WebContent;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #86: the Event Server's per-workspace header-policy store. Acceptance lives here:
    /// the runtime guardrail — a stored row naming a forbidden header (hand-edited blob, an old
    /// definition, anything) is REFUSED at snapshot time with an audit line naming the workspace
    /// and the raw row, and never reaches the applied policy; valid rows keep configuration order;
    /// and the snapshot rides the same 60 s refresh / last-known-good resilience as the cap,
    /// allowlist and trigger stores, with a fault never changing the served posture beyond "no
    /// rules". Blobs are fed as raw WorkspaceConfig JSON, exactly the shape the configuration
    /// service hands the plugin.
    /// </summary>
    public class ProxyHeaderPolicyStoreTests
    {
        private const string WorkspaceA = "11111111-1111-1111-1111-111111111111";
        private const string WorkspaceB = "22222222-2222-2222-2222-222222222222";

        private static ProxyWorkspaceHeaderSource Workspace(string itemId, string headerJsonMembers)
        {
            return new ProxyWorkspaceHeaderSource
            {
                ItemId = itemId,
                Name = "Workspace " + itemId,
                Properties = new Dictionary<string, string>
                {
                    ["WorkspaceConfig"] =
                        "{\"version\":1,\"layout\":\"1x1\",\"items\":[{\"type\":\"WebContent\",\"position\":0,\"settings\":" +
                        "{\"homeAddress\":\"http://x/\",\"proxyEnabled\":true" +
                        (headerJsonMembers == null ? string.Empty : "," + headerJsonMembers) + "}}]}",
                },
            };
        }

        [Fact]
        public void WorkspaceWithoutHeaderSectionsGetsTheEmptyPolicy()
        {
            var store = new ProxyHeaderPolicyStore(
                () => new List<ProxyWorkspaceHeaderSource> { Workspace(WorkspaceA, null) },
                _ => { });

            var policy = store.Resolve(WorkspaceA);
            Assert.Empty(policy.RequestRules);
            Assert.Empty(policy.ResponseRules);
        }

        [Fact]
        public void ValidRowsAreAppliedInConfigurationOrderOnTheirOwnLeg()
        {
            const string members =
                "\"proxyRequestHeaders\":[{\"name\":\"X-First\",\"value\":\"1\"},{\"name\":\"X-First\",\"value\":\"2\"}]," +
                "\"proxyResponseHeaders\":[{\"name\":\"Set-Cookie\",\"remove\":true}]";
            var store = new ProxyHeaderPolicyStore(
                () => new List<ProxyWorkspaceHeaderSource> { Workspace(WorkspaceA, members) },
                _ => { });

            var policy = store.Resolve(WorkspaceA);
            Assert.Equal(2, policy.RequestRules.Count);
            Assert.Equal("2", policy.RequestRules[1].Value);
            Assert.Single(policy.ResponseRules);
            Assert.True(policy.ResponseRules[0].Remove);
            Assert.Equal("Set-Cookie", policy.ResponseRules[0].Name);
            Assert.Empty(store.Resolve(WorkspaceB).RequestRules); // other workspaces unaffected
        }

        [Fact]
        public void ForbiddenStoredRowsAreRefusedWithAnAuditLineAndNeverReachTheWire()
        {
            // The acceptance guardrail end to end: a blob that claims Host:, Content-Length: and a
            // Proxy-* header on the REQUEST leg and Transfer-Encoding on the RESPONSE leg yields
            // zero applied rules and one audit line per refused row, naming the workspace.
            const string members =
                "\"proxyRequestHeaders\":[{\"name\":\"Host\",\"value\":\"evil.example\"}," +
                "{\"name\":\"Content-Length\",\"value\":\"0\"},{\"name\":\"Proxy-Cookie\",\"value\":\"x\"}]," +
                "\"proxyResponseHeaders\":[{\"name\":\"Transfer-Encoding\",\"remove\":true}]";
            var log = new List<string>();
            var store = new ProxyHeaderPolicyStore(
                () => new List<ProxyWorkspaceHeaderSource> { Workspace(WorkspaceA, members) },
                message => log.Add(message));

            var policy = store.Resolve(WorkspaceA);
            Assert.Empty(policy.RequestRules);
            Assert.Empty(policy.ResponseRules);
            Assert.Equal(4, log.Count);
            Assert.All(log, line =>
            {
                Assert.Contains("refused", line, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(WorkspaceA, line, StringComparison.Ordinal);
            });
            Assert.Contains("Host", log[0], StringComparison.Ordinal);
            Assert.Contains("reserved", log[0], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ForbiddenRefusalsAreAuditedOncePerRefreshWindow()
        {
            const string members = "\"proxyRequestHeaders\":[{\"name\":\"Connection\",\"value\":\"close\"}]";
            var log = new List<string>();
            var store = new ProxyHeaderPolicyStore(
                () => new List<ProxyWorkspaceHeaderSource> { Workspace(WorkspaceA, members) },
                message => log.Add(message));

            store.Resolve(WorkspaceA);
            store.Resolve(WorkspaceA); // inside the 60 s window: no second resolve, no second line
            var refusals = log.FindAll(line => line.Contains("refused", StringComparison.OrdinalIgnoreCase));
            Assert.Single(refusals);
            store.Prime();
            refusals = log.FindAll(line => line.Contains("refused", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, refusals.Count);
        }

        [Fact]
        public void MalformedRowsAreRefusedLikeForbiddenOnes()
        {
            const string members =
                "\"proxyRequestHeaders\":[{\"name\":\"Bad Name\",\"value\":\"x\"}," +
                "{\"name\":\"X-NoValue\"},{\"name\":\"X-CRLF\",\"value\":\"a\\r\\nX-Evil: yes\"}]," +
                "\"proxyResponseHeaders\":[{\"name\":\"X-\",\"value\":\"\"}]";
            var log = new List<string>();
            var store = new ProxyHeaderPolicyStore(
                () => new List<ProxyWorkspaceHeaderSource> { Workspace(WorkspaceA, members) },
                message => log.Add(message));

            var policy = store.Resolve(WorkspaceA);
            Assert.Empty(policy.RequestRules);
            Assert.Empty(policy.ResponseRules);
            Assert.Equal(4, log.Count); // bad token, no value, CR/LF value, and an add row with an empty value
        }

        [Fact]
        public void StripRowsMayCarryNoValue()
        {
            const string members = "\"proxyRequestHeaders\":[{\"name\":\"X-Internal\",\"remove\":true}]";
            var store = new ProxyHeaderPolicyStore(
                () => new List<ProxyWorkspaceHeaderSource> { Workspace(WorkspaceA, members) },
                _ => { });

            var rule = Assert.Single(store.Resolve(WorkspaceA).RequestRules);
            Assert.True(rule.Remove);
            Assert.Null(rule.Value);
        }

        [Fact]
        public void NeverResolvedStoreAndCorruptBlobsAnswerTheEmptyPolicy()
        {
            var store = new ProxyHeaderPolicyStore(
                () => throw new InvalidOperationException("Configuration service is not available."),
                _ => { });
            Assert.Empty(store.Resolve(WorkspaceA).RequestRules);
            Assert.Empty(store.Resolve(null).ResponseRules);

            var corrupt = new ProxyHeaderPolicyStore(
                () => new List<ProxyWorkspaceHeaderSource>
                {
                    new ProxyWorkspaceHeaderSource { ItemId = WorkspaceA, Name = "corrupt", Properties = new Dictionary<string, string> { ["WorkspaceConfig"] = "{{{" } },
                    new ProxyWorkspaceHeaderSource { ItemId = WorkspaceB, Name = "bare", Properties = new Dictionary<string, string>() },
                },
                _ => { });
            Assert.Empty(corrupt.Resolve(WorkspaceA).RequestRules);
            Assert.Empty(corrupt.Resolve(WorkspaceB).ResponseRules);
        }

        [Fact]
        public void KeepsServingTheLastKnownPoliciesWhenTheConfigServiceFlaps()
        {
            var resolverThrows = false;
            const string members = "\"proxyRequestHeaders\":[{\"name\":\"X-Trace\",\"value\":\"on\"}]";
            var store = new ProxyHeaderPolicyStore(
                () =>
                {
                    if (resolverThrows)
                    {
                        throw new InvalidOperationException("transient");
                    }

                    return new List<ProxyWorkspaceHeaderSource> { Workspace(WorkspaceA, members) };
                },
                _ => { });

            Assert.Single(store.Resolve(WorkspaceA).RequestRules);
            resolverThrows = true;
            Assert.Single(store.Resolve(WorkspaceA).RequestRules); // last-known-good survives the flap
            Assert.Empty(store.Resolve(WorkspaceB).RequestRules);
        }

        [Fact]
        public void SnapshotResolvesAtMostOncePerRefreshWindow()
        {
            var resolves = 0;
            var store = new ProxyHeaderPolicyStore(
                () =>
                {
                    resolves++;
                    return new List<ProxyWorkspaceHeaderSource>();
                },
                _ => { });

            store.Resolve(WorkspaceA);
            store.Resolve(WorkspaceA);
            store.Resolve(WorkspaceB);
            Assert.Equal(1, resolves);

            store.Prime();
            Assert.Equal(2, resolves);
        }
    }
}
