using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #69: the Event Server's in-memory diagnostics aggregation - per-workspace blocked
    /// rings (dedup, counts, ring eviction) and per-session live tables (lifecycle, idle sweep,
    /// traffic counters, req/min). Pure logic on caller-supplied timestamps, so every rule is
    /// exercised without MIP.
    /// </summary>
    public class ProxyDiagnosticsAggregatorTests
    {
        private static readonly Guid WorkspaceA = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid WorkspaceB = new Guid("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void SameOriginAndReasonDedupOntoOneEntryAccumulatingCount()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://one.test:80", ProxyBlockReasons.NotAllowed, 1, T0);
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://one.test:80", ProxyBlockReasons.NotAllowed, 3, T0.AddMinutes(2));

            var snapshot = aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(5));
            Assert.Single(snapshot.Blocked);
            Assert.Equal(4, snapshot.Blocked[0].Count);
            Assert.Equal(T0.AddMinutes(2), snapshot.Blocked[0].LastSeenUtc);
        }

        [Fact]
        public void SameOriginWithDifferentReasonsStaysTwoEntries()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://one.test:80", ProxyBlockReasons.NotAllowed, 1, T0);
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://one.test:80", ProxyBlockReasons.Timeout, 1, T0);

            var snapshot = aggregator.Snapshot(WorkspaceA.ToString(), T0);
            Assert.Equal(2, snapshot.Blocked.Count);
        }

        [Fact]
        public void BlockedRingEvictsOldestFirstSeenPastTheCap()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            var cap = ProxyDiagnosticsAggregator.MaxBlockedOriginsPerWorkspace;
            for (var i = 0; i < cap + 5; i++)
            {
                aggregator.RecordBlock(WorkspaceA.ToString(), $"http://host{i}.test:80", ProxyBlockReasons.NotAllowed, 1, T0.AddSeconds(i));
            }

            var snapshot = aggregator.Snapshot(WorkspaceA.ToString(), T0);
            Assert.Equal(cap, snapshot.Blocked.Count);
            Assert.Equal("http://host5.test:80", snapshot.Blocked[0].Origin);
            Assert.Equal($"http://host{cap + 4}.test:80", snapshot.Blocked[cap - 1].Origin);
        }

        [Fact]
        public void RefreshingAnEntryAccumulatesIntoTheRecencyOrder()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            var cap = ProxyDiagnosticsAggregator.MaxBlockedOriginsPerWorkspace;
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://first.test:80", ProxyBlockReasons.NotAllowed, 1, T0);
            for (var i = 0; i < cap - 1; i++)
            {
                aggregator.RecordBlock(WorkspaceA.ToString(), $"http://fill{i}.test:80", ProxyBlockReasons.NotAllowed, 1, T0);
            }

            aggregator.RecordBlock(WorkspaceA.ToString(), "http://first.test:80", ProxyBlockReasons.NotAllowed, 10, T0.AddMinutes(1));
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://newer.test:80", ProxyBlockReasons.NotAllowed, 1, T0.AddMinutes(2));

            var snapshot = aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(3));
            Assert.Equal(cap, snapshot.Blocked.Count);
            // The refreshed entry keeps its identity, accumulates its count, and moves to the
            // fresh end - the eviction that follows drops the least recently seen origin instead.
            var byId = snapshot.Blocked.ToDictionary(entry => entry.Origin, entry => entry);
            Assert.Equal(11, byId["http://first.test:80"].Count);
            Assert.False(byId.ContainsKey("http://fill0.test:80"));
            Assert.Equal("http://newer.test:80", snapshot.Blocked[cap - 1].Origin);
        }

        [Fact]
        public void BlankFieldsAndUnknownWorkspaceSnapshotsAreHarmless()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.RecordBlock(WorkspaceA.ToString(), "", ProxyBlockReasons.NotAllowed, 1, T0);
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://one.test:80", "", 1, T0);
            aggregator.RecordBlock(null, "http://one.test:80", ProxyBlockReasons.NotAllowed, 1, T0);

            Assert.Empty(aggregator.Snapshot(WorkspaceA.ToString(), T0).Blocked);

            var empty = aggregator.Snapshot(WorkspaceB.ToString(), T0);
            Assert.True(empty.Success);
            Assert.Empty(empty.Sessions);
            Assert.Empty(empty.Blocked);
        }

        [Fact]
        public void SessionLifecycleCountsAndDuplicateOpenRefreshesInPlace()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.OpenSession("s1", WorkspaceA.ToString(), "alice", "BEAST", T0);
            aggregator.OpenSession("s1", WorkspaceA.ToString(), "alice-renamed", "BEAST2", T0.AddMinutes(1));

            var snapshot = aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(2));
            Assert.Single(snapshot.Sessions);
            Assert.Equal("alice-renamed", snapshot.Sessions[0].UserName);
            Assert.Equal("BEAST2", snapshot.Sessions[0].ClientMachine);

            Assert.True(aggregator.CloseSession("s1", T0.AddMinutes(3)));
            Assert.False(aggregator.CloseSession("s1", T0.AddMinutes(4)));
            Assert.Empty(aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(5)).Sessions);
        }

        [Fact]
        public void HeartbeatAndTrafficKeepSessionsOffTheIdleSweepWhileStaleOnesGo()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.OpenSession("live", WorkspaceA.ToString(), "alice", "BEAST", T0);
            aggregator.OpenSession("stale", WorkspaceA.ToString(), "bob", "BEAST", T0);

            for (var minute = 1; minute <= 5; minute++)
            {
                Assert.True(aggregator.HeartbeatSession("live", T0.AddMinutes(minute)));
                aggregator.RecordFetchTraffic("live", 10, 20, T0.AddMinutes(minute));
            }

            var reclaimed = aggregator.SweepIdleSessions(TimeSpan.FromMinutes(3), T0.AddMinutes(5));
            Assert.Equal(new[] { "stale" }, reclaimed);
            Assert.True(aggregator.HasSessions(WorkspaceA.ToString()));

            Assert.False(aggregator.HeartbeatSession("gone", T0.AddMinutes(6)));
            Assert.False(aggregator.RecordFetchTraffic("gone", 1, 1, T0.AddMinutes(6)));
            Assert.False(aggregator.RecordTunnelTraffic("gone", 1, 1, T0.AddMinutes(6)));
        }

        [Fact]
        public void SweepReclaimsSessionCountersWithTheSession()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.OpenSession("doomed", WorkspaceA.ToString(), null, null, T0);
            aggregator.RecordFetchTraffic("doomed", 5, 7, T0);

            Assert.Single(aggregator.SweepIdleSessions(TimeSpan.FromMinutes(3), T0.AddMinutes(4)));
            var snapshot = aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(5));
            Assert.Empty(snapshot.Sessions);
            // Blocked rings never die with the sweep.
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://keep.test:80", ProxyBlockReasons.NotAllowed, 1, T0.AddMinutes(5));
            Assert.Single(aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(6)).Blocked);
        }

        [Fact]
        public void FetchTrafficCountsRequestAndBothByteDirectionsTowardOneSession()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.OpenSession("s1", WorkspaceA.ToString(), "alice", "BEAST", T0);
            Assert.True(aggregator.RecordFetchTraffic("s1", 100, 5000, T0.AddMinutes(1)));
            Assert.True(aggregator.RecordFetchTraffic("s1", 50, 2500, T0.AddMinutes(2)));

            var entry = aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(4)).Sessions[0];
            Assert.Equal(2, entry.Requests);
            Assert.Equal(150, entry.BytesSent);
            Assert.Equal(7500, entry.BytesReceived);
            Assert.Equal(T0.AddMinutes(2), entry.LastActivityUtc);
            Assert.Equal(T0, entry.OpenedUtc);
            Assert.Equal(0.5, entry.RequestsPerMinute, 9);
        }

        [Fact]
        public void JustOpenedSessionNeverShowsAnInflatedRate()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.OpenSession("fresh", WorkspaceA.ToString(), "alice", "BEAST", T0.AddMinutes(29));
            aggregator.RecordFetchTraffic("fresh", 0, 0, T0.AddMinutes(29.5));

            // 30 seconds of life, one request: the window floor of one minute keeps the rate at 1,
            // not the misleading 2-per-minute a raw divide would show.
            var entry = aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(29.75)).Sessions[0];
            Assert.Equal(1.0, entry.RequestsPerMinute, 9);
        }

        [Fact]
        public void TunnelTrafficAddsBytesWithoutCountingAnotherRequest()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.OpenSession("s1", WorkspaceA.ToString(), "alice", "BEAST", T0);
            aggregator.RecordFetchTraffic("s1", 0, 0, T0);
            Assert.True(aggregator.RecordTunnelTraffic("s1", 400, 900, T0.AddMinutes(1)));
            Assert.True(aggregator.RecordTunnelTraffic("s1", 0, 25, T0.AddMinutes(2)));

            var entry = aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(3)).Sessions[0];
            Assert.Equal(1, entry.Requests);
            Assert.Equal(400, entry.BytesSent);
            Assert.Equal(925, entry.BytesReceived);
        }

        [Fact]
        public void BlocksFiledForSessionsLandUnderTheSessionsWorkspace()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.OpenSession("s1", WorkspaceA.ToString(), "alice", "BEAST", T0);

            Assert.True(aggregator.RecordBlockForSession("s1", "http://gate.test:80", ProxyBlockReasons.NotAllowed, T0.AddMinutes(1)));
            Assert.False(aggregator.RecordBlockForSession("nope", "http://gate.test:80", ProxyBlockReasons.NotAllowed, T0.AddMinutes(1)));

            Assert.Single(aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(2)).Blocked);
            Assert.Empty(aggregator.Snapshot(WorkspaceB.ToString(), T0.AddMinutes(2)).Blocked);
        }

        [Fact]
        public void WorkspacesKeepSeparateRingsAndSessionTables()
        {
            var aggregator = new ProxyDiagnosticsAggregator();
            aggregator.RecordBlock(WorkspaceA.ToString(), "http://a.test:80", ProxyBlockReasons.NotAllowed, 1, T0);
            aggregator.RecordBlock(WorkspaceB.ToString(), "http://b.test:80", ProxyBlockReasons.Timeout, 1, T0);
            aggregator.OpenSession("sa", WorkspaceA.ToString(), "alice", "BEAST", T0);
            aggregator.OpenSession("sb", WorkspaceB.ToString(), "bob", "WORKSTATION", T0);

            var a = aggregator.Snapshot(WorkspaceA.ToString(), T0.AddMinutes(1));
            var b = aggregator.Snapshot(WorkspaceB.ToString(), T0.AddMinutes(1));
            Assert.Equal(new[] { "http://a.test:80" }, new[] { a.Blocked[0].Origin });
            Assert.Equal(new[] { "http://b.test:80" }, new[] { b.Blocked[0].Origin });
            Assert.Equal("alice", a.Sessions[0].UserName);
            Assert.Equal("bob", b.Sessions[0].UserName);
        }

        [Fact]
        public void TryMapErrorCodeFunnelsWireCodesIntoBlockCategories()
        {
            string reason;
            Assert.True(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.NotAllowed, out reason));
            Assert.Equal(ProxyBlockReasons.NotAllowed, reason);
            Assert.True(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.TlsValidation, out reason));
            Assert.Equal(ProxyBlockReasons.CertUntrusted, reason);
            Assert.True(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.NoAuthority, out reason));
            Assert.Equal(ProxyBlockReasons.CertUntrusted, reason);
            Assert.True(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.Unreachable, out reason));
            Assert.Equal(ProxyBlockReasons.Unreachable, reason);
            Assert.True(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.Timeout, out reason));
            Assert.Equal(ProxyBlockReasons.Timeout, reason);
            Assert.True(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.TooLarge, out reason));
            Assert.Equal(ProxyBlockReasons.TooLarge, reason);
            Assert.True(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.HandshakeRefused, out reason));
            Assert.Equal(ProxyBlockReasons.TunnelRefused, reason);

            // Component faults and protocol noise are never per-origin blocks.
            Assert.False(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.ServerUnavailable, out reason));
            Assert.False(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.BadRequest, out reason));
            Assert.False(ProxyBlockReasons.TryMapErrorCode(ProxyFetchErrorCodes.Error, out reason));
            Assert.False(ProxyBlockReasons.TryMapErrorCode(null, out reason));
            Assert.Null(reason);
        }
    }
}
