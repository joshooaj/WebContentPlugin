using System;
using System.Collections.Generic;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #65: behavior tests for the per-workspace trigger row store — the resilience
    /// contract (closed before the first resolve, last-known-good after, tolerant absence,
    /// per-row refusal isolation) and the one-event-per-fetch rule.
    /// </summary>
    public class ProxyTriggerMatcherTests
    {
        private const string WorkspaceA = "11111111-1111-1111-1111-111111111111";
        private const string WorkspaceB = "22222222-2222-2222-2222-222222222222";

        [Fact]
        public void BeforeTheFirstSuccessfulResolve_EverythingIsNoMatch()
        {
            var logs = new List<string>();
            var matcher = new ProxyTriggerMatcher(
                () => throw new InvalidOperationException("configuration service down"),
                message => logs.Add(message));

            ProxyTriggerMatcher.TriggerMatch match;
            Assert.False(matcher.TryMatch(WorkspaceA, new Uri("https://camera.local/x"), "GET", 200, out match));
            Assert.Null(match);
            Assert.Contains(logs, line => line.Contains("Proxy trigger row refresh failed"));
        }

        [Fact]
        public void RowsMatchTheirOwnWorkspaceOnly()
        {
            var matcher = MatcherFor(new Dictionary<string, List<ProxyTriggerConfiguration>>
            {
                [WorkspaceA] = new List<ProxyTriggerConfiguration>
                {
                    Row("https://camera.local/*", null, "4xx"),
                },
            });

            ProxyTriggerMatcher.TriggerMatch match;

            // Same url, right workspace: match, and the class filter decides.
            Assert.True(matcher.TryMatch(WorkspaceA, new Uri("https://camera.local/snapshot"), "GET", 404, out match));
            Assert.Equal(WorkspaceA, match.WorkspaceItemId);
            Assert.False(matcher.TryMatch(WorkspaceA, new Uri("https://camera.local/snapshot"), "GET", 200, out match));

            // Same url, different workspace: no rows there, no match.
            Assert.False(matcher.TryMatch(WorkspaceB, new Uri("https://camera.local/snapshot"), "GET", 404, out match));
        }

        [Fact]
        public void TheFirstMatchingRowWins_OneFetchRaisesAtMostOneEvent()
        {
            var matcher = MatcherFor(new Dictionary<string, List<ProxyTriggerConfiguration>>
            {
                [WorkspaceA] = new List<ProxyTriggerConfiguration>
                {
                    Row("https://camera.local/a*", null, null),
                    Row("https://camera.local/*", null, null),
                },
            });

            ProxyTriggerMatcher.TriggerMatch match;
            Assert.True(matcher.TryMatch(WorkspaceA, new Uri("https://camera.local/abc"), "GET", 200, out match));
            Assert.Contains("url=https://camera.local/a*", match.RowDescription);
        }

        [Fact]
        public void AbsentSectionsAreToleratedSilently()
        {
            var logs = new List<string>();

            // No WorkspaceConfig blob at all, and a blob whose WebContent settings carry no
            // proxyTriggers section (every definition saved before #65): both load as "no rows"
            // without an audit line.
            var sources = new List<ProxyWorkspaceTriggerSource>
            {
                new ProxyWorkspaceTriggerSource { ItemId = WorkspaceA, Name = "No blob", Properties = new Dictionary<string, string>() },
                new ProxyWorkspaceTriggerSource { ItemId = WorkspaceB, Name = "No section", Properties = Blob(new List<ProxyTriggerConfiguration>()) },
            };
            var matcher = new ProxyTriggerMatcher(() => sources, message => logs.Add(message));

            Assert.Empty(matcher.WorkspaceIdsWithRows());
            Assert.DoesNotContain(logs, line => line.Contains("refused"));

            ProxyTriggerMatcher.TriggerMatch match;
            Assert.False(matcher.TryMatch(WorkspaceB, new Uri("https://camera.local/x"), "GET", 200, out match));
        }

        [Fact]
        public void ARefusedRowNeverTakesDownTheWorkspacesOtherRows()
        {
            var logs = new List<string>();
            var settings = new List<ProxyTriggerConfiguration>
            {
                Row("https://*", "GET", "nope"),     // outside the status grammar
                Row("https://*/ok", null, null),     // good row after a bad one
            };
            var sources = new List<ProxyWorkspaceTriggerSource>
            {
                new ProxyWorkspaceTriggerSource { ItemId = WorkspaceA, Name = "Mixed rows", Properties = Blob(settings) },
            };
            var matcher = new ProxyTriggerMatcher(() => sources, message => logs.Add(message));

            ProxyTriggerMatcher.TriggerMatch match;
            Assert.True(matcher.TryMatch(WorkspaceA, new Uri("https://anything/ok"), "GET", 200, out match));
            Assert.Contains(logs, line => line.Contains("refused") && line.Contains("Mixed rows") && line.Contains("status='nope'"));
        }

        [Fact]
        public void ResolverFailureAfterAGoodResolveKeepsServingLastKnownGoodRows()
        {
            var calls = 0;
            var logs = new List<string>();
            var good = new List<ProxyWorkspaceTriggerSource>
            {
                new ProxyWorkspaceTriggerSource { ItemId = WorkspaceA, Name = "Good", Properties = Blob(new List<ProxyTriggerConfiguration> { Row("https://*/x", null, null) }) },
            };

            // Force exactly one successful resolve, then a failing one, by flipping the seam
            // between Prime() calls (each Prime resets the refresh window).
            var matcher = new ProxyTriggerMatcher(
                () =>
                {
                    calls++;
                    if (calls == 1)
                    {
                        return good;
                    }

                    throw new InvalidOperationException("configuration service blipped");
                },
                message => logs.Add(message));

            matcher.Prime();
            matcher.Prime();

            Assert.Contains(logs, line => line.Contains("Proxy trigger row refresh failed"));
            ProxyTriggerMatcher.TriggerMatch match;
            Assert.True(matcher.TryMatch(WorkspaceA, new Uri("https://anything/x"), "GET", 200, out match));
            Assert.Contains(WorkspaceA, string.Join(",", matcher.WorkspaceIdsWithRows()));
        }

        [Fact]
        public void PrimeLogsWhatTheConfigurationActuallyLoaded()
        {
            var logs = new List<string>();
            var sources = new List<ProxyWorkspaceTriggerSource>
            {
                new ProxyWorkspaceTriggerSource { ItemId = WorkspaceA, Name = "A", Properties = Blob(new List<ProxyTriggerConfiguration> { Row("https://*/a", null, null), Row("https://*/b", null, null) }) },
                new ProxyWorkspaceTriggerSource { ItemId = WorkspaceB, Name = "B", Properties = Blob(new List<ProxyTriggerConfiguration>()) },
            };
            var matcher = new ProxyTriggerMatcher(() => sources, message => logs.Add(message));

            matcher.Prime();

            Assert.Contains(logs, line => line.Contains("Workspace rows refreshed: 2 row(s) across 1 workspace(s)"));
        }

        // ---- helpers ----

        private static ProxyTriggerMatcher MatcherFor(Dictionary<string, List<ProxyTriggerConfiguration>> byWorkspace)
        {
            var sources = new List<ProxyWorkspaceTriggerSource>();
            foreach (var pair in byWorkspace)
            {
                sources.Add(new ProxyWorkspaceTriggerSource
                {
                    ItemId = pair.Key,
                    Name = "workspace-" + pair.Key.Substring(0, 4),
                    Properties = Blob(pair.Value),
                });
            }

            return new ProxyTriggerMatcher(() => sources, _ => { });
        }

        private static Dictionary<string, string> Blob(List<ProxyTriggerConfiguration> triggers)
        {
            var config = WorkspaceConfiguration.CreateDefault("https://start.page");
            config.Items[0].Settings.ProxyTriggers = triggers;
            return new Dictionary<string, string> { { WorkspaceConfiguration.WorkspaceConfigKey, config.Serialize() } };
        }

        private static ProxyTriggerConfiguration Row(string url, string method, string status)
        {
            return new ProxyTriggerConfiguration { Url = url, Method = method, Status = status };
        }
    }
}
