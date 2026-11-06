using System;
using System.Collections.Generic;

using WebContent;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #85: the Event Server's per-workspace response-size cap store. Acceptance lives
    /// here: no workspace can obtain a cap above the 20 MB ceiling regardless of what its stored
    /// config claims (huge and malformed values clamp on every read), workspaces that never
    /// stored a cap - every pre-#85 definition - keep the 5 MB default, and the snapshot rides
    /// the same 60 s refresh / last-known-good resilience as the allowlist and trigger stores.
    /// The blobs are fed as raw WorkspaceConfig JSON, exactly the shape the configuration
    /// service hands the plugin.
    /// </summary>
    public class ProxyResponseCapStoreTests
    {
        private const string WorkspaceA = "11111111-1111-1111-1111-111111111111";
        private const string WorkspaceB = "22222222-2222-2222-2222-222222222222";

        private static readonly List<string> LogLines = new List<string>();
        private static readonly Action<string> Sink = message => { lock (LogLines) { LogLines.Add(message); } };

        private static ProxyWorkspaceCapSource Workspace(string itemId, string capJsonMember)
        {
            return new ProxyWorkspaceCapSource
            {
                ItemId = itemId,
                Name = "Workspace " + itemId,
                Properties = new Dictionary<string, string>
                {
                    ["WorkspaceConfig"] =
                        "{\"version\":1,\"layout\":\"1x1\",\"items\":[{\"type\":\"WebContent\",\"position\":0,\"settings\":" +
                        "{\"homeAddress\":\"http://x/\",\"proxyEnabled\":true" +
                        (capJsonMember == null ? string.Empty : "," + capJsonMember) + "}}]}",
                },
            };
        }

        [Fact]
        public void WorkspaceWithoutTheCapMemberKeepsTheDefaultCap()
        {
            var store = new ProxyResponseCapStore(
                () => new List<ProxyWorkspaceCapSource> { Workspace(WorkspaceA, null) },
                Sink);

            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(WorkspaceA));
        }

        [Fact]
        public void StoredCapIsServedVerbatimInsideTheRange()
        {
            var store = new ProxyResponseCapStore(
                () => new List<ProxyWorkspaceCapSource> { Workspace(WorkspaceA, "\"proxyMaxResponseBytes\":8388608") },
                Sink);

            Assert.Equal(8 * 1024 * 1024, store.ResolveEffectiveBytes(WorkspaceA));
        }

        [Theory]
        [InlineData(20971521)]   // one byte past the 20 MB ceiling
        [InlineData(int.MaxValue)]
        [InlineData(1_000_000_000)]
        public void HugeStoredCapClampsToTheCeilingServerSide(int stored)
        {
            var store = new ProxyResponseCapStore(
                () => new List<ProxyWorkspaceCapSource> { Workspace(WorkspaceA, "\"proxyMaxResponseBytes\":" + stored) },
                Sink);

            Assert.Equal(ProxyFetchLimits.MaxAllowedResponseBytes, store.ResolveEffectiveBytes(WorkspaceA));
        }

        [Fact]
        public void ClampedCapIsAuditedByWorkspaceNameOncePerRefresh()
        {
            lock (LogLines) LogLines.Clear();
            var store = new ProxyResponseCapStore(
                () => new List<ProxyWorkspaceCapSource> { Workspace(WorkspaceA, "\"proxyMaxResponseBytes\":999000000") },
                Sink);

            Assert.Equal(ProxyFetchLimits.MaxAllowedResponseBytes, store.ResolveEffectiveBytes(WorkspaceA));
            store.ResolveEffectiveBytes(WorkspaceA); // inside the 60 s window: no second resolve, no second line

            lock (LogLines)
            {
                Assert.Single(LogLines);
                Assert.Contains("ceiling", LogLines[0], StringComparison.OrdinalIgnoreCase);
                Assert.Contains(WorkspaceA, LogLines[0], StringComparison.Ordinal);
                Assert.Contains("999000000", LogLines[0], StringComparison.Ordinal);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-999999)]
        public void ZeroAndNegativeStoredCapsMeanTheDefaultNotACeilingBeatingRefusal(int stored)
        {
            var store = new ProxyResponseCapStore(
                () => new List<ProxyWorkspaceCapSource> { Workspace(WorkspaceA, "\"proxyMaxResponseBytes\":" + stored) },
                Sink);

            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(WorkspaceA));
        }

        [Fact]
        public void UnknownWorkspaceAndNullItemBothAnswerTheDefault()
        {
            var store = new ProxyResponseCapStore(
                () => new List<ProxyWorkspaceCapSource> { Workspace(WorkspaceA, "\"proxyMaxResponseBytes\":8388608") },
                Sink);

            Assert.Equal(8 * 1024 * 1024, store.ResolveEffectiveBytes(WorkspaceA));
            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(WorkspaceB));
            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(null));
            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(""));
        }

        [Fact]
        public void AWorkspaceWithoutAnyUsableBlobAnswersTheDefault()
        {
            var store = new ProxyResponseCapStore(
                () => new List<ProxyWorkspaceCapSource>
                {
                    new ProxyWorkspaceCapSource { ItemId = WorkspaceA, Name = "corrupt", Properties = new Dictionary<string, string> { ["WorkspaceConfig"] = "{{{" } },
                    new ProxyWorkspaceCapSource { ItemId = WorkspaceB, Name = "bare", Properties = new Dictionary<string, string>() },
                },
                Sink);

            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(WorkspaceA));
            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(WorkspaceB));
        }

        [Fact]
        public void NeverResolvedStoreKeepsEveryWorkspaceAtTheDefault()
        {
            var store = new ProxyResponseCapStore(
                () => throw new InvalidOperationException("Configuration service is not available."),
                Sink);

            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(WorkspaceA));
        }

        [Fact]
        public void KeepsServingTheLastKnownCapsWhenTheConfigServiceFlaps()
        {
            var resolverThrows = false;
            var store = new ProxyResponseCapStore(
                () =>
                {
                    if (resolverThrows)
                    {
                        throw new InvalidOperationException("transient");
                    }

                    return new List<ProxyWorkspaceCapSource> { Workspace(WorkspaceA, "\"proxyMaxResponseBytes\":8388608") };
                },
                Sink);

            Assert.Equal(8 * 1024 * 1024, store.ResolveEffectiveBytes(WorkspaceA));
            resolverThrows = true;

            // The flap must not knock a raised cap back to the default mid-session, and it must
            // not crack open anything either: unrelated workspaces stay at the default.
            Assert.Equal(8 * 1024 * 1024, store.ResolveEffectiveBytes(WorkspaceA));
            Assert.Equal(ProxyFetchLimits.DefaultMaxResponseBytes, store.ResolveEffectiveBytes(WorkspaceB));
        }

        [Fact]
        public void SnapshotResolvesAtMostOncePerRefreshWindow()
        {
            var resolves = 0;
            var store = new ProxyResponseCapStore(
                () =>
                {
                    resolves++;
                    return new List<ProxyWorkspaceCapSource> { Workspace(WorkspaceA, "\"proxyMaxResponseBytes\":8388608") };
                },
                Sink);

            store.ResolveEffectiveBytes(WorkspaceA);
            store.ResolveEffectiveBytes(WorkspaceA);
            store.ResolveEffectiveBytes(WorkspaceB);
            Assert.Equal(1, resolves);

            // Prime() forces the next resolve - the startup audit path.
            store.Prime();
            Assert.Equal(2, resolves);
        }

        [Theory]
        [InlineData(0, ProxyFetchLimits.DefaultMaxResponseBytes)]
        [InlineData(-5, ProxyFetchLimits.DefaultMaxResponseBytes)]
        [InlineData(1, 1)] // a hand-edited stricter cap is honoured, not floored
        [InlineData(8388608, 8388608)]
        [InlineData(20971520, 20971520)]
        [InlineData(20971521, 20971520)]
        [InlineData(int.MaxValue, 20971520)]
        public void LimitsFactoryClampsTheStoredCapIntoTheFetchBounds(int stored, int expected)
        {
            Assert.Equal(expected, ProxyFetchLimits.ForResponseCapBytes(stored).MaxResponseBytes);
        }
    }
}
