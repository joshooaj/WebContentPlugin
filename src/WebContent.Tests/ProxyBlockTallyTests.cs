using System;
using System.Collections.Generic;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #83: the per-view blocked-resource tally. It must count every client-side refusal
    /// locally (regardless of the ES forwarder's health), dedup per canonical origin + reason,
    /// stay bounded, reset on demand, and never throw or bury the log during a refusal storm.
    /// </summary>
    public class ProxyBlockTallyTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

        private static ProxyBlockTally Tally(List<string> logs, Func<DateTime> clock = null)
        {
            return new ProxyBlockTally(message => logs.Add(message), clock ?? (() => T0));
        }

        [Fact]
        public void RefusalsCountLocallyAndDedupPerCanonicalOriginAndReason()
        {
            var logs = new List<string>();
            var tally = Tally(logs);

            tally.Report(new Uri("http://cdn.test/img/one.png"), ProxyBlockReasons.NotAllowed);
            tally.Report(new Uri("http://cdn.test/img/two.png"), ProxyBlockReasons.NotAllowed);
            tally.Report(new Uri("http://cdn.test/"), ProxyBlockReasons.Timeout);

            Assert.Equal(3, tally.TotalBlocks);
            var rows = tally.Snapshot();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal("http://cdn.test", row.Origin));
            var notAllowed = rows.Find(row => row.Reason == ProxyBlockReasons.NotAllowed);
            Assert.Equal(2, notAllowed.Count);
            Assert.Equal(1, rows.Find(row => row.Reason == ProxyBlockReasons.Timeout).Count);

            // One marker per NEW origin+reason - the repeat of the first key stayed quiet.
            Assert.Equal(2, logs.Count);
            Assert.Contains("origin='http://cdn.test' reason=NotAllowed", string.Join("\n", logs));
        }

        [Fact]
        public void SnapshotIsMostRecentlySeenFirst()
        {
            var ticks = 0;
            var tally = Tally(new List<string>(), () => T0.AddSeconds(++ticks));

            tally.Report(new Uri("http://first.test/"), ProxyBlockReasons.NotAllowed);
            tally.Report(new Uri("http://second.test/"), ProxyBlockReasons.NotAllowed);
            // A fresh sighting of the older row promotes it to the top.
            tally.Report(new Uri("http://first.test/"), ProxyBlockReasons.NotAllowed);

            var rows = tally.Snapshot();
            Assert.Equal("http://first.test", rows[0].Origin);
            Assert.Equal("http://second.test", rows[1].Origin);
            Assert.Equal(2, rows[0].Count);
        }

        [Fact]
        public void ResetForgetsThePageLoadAndRaisesTheChange()
        {
            var tally = Tally(new List<string>());
            var changes = 0;
            tally.Changed += () => changes++;

            tally.Report(new Uri("http://cdn.test/x.png"), ProxyBlockReasons.NotAllowed);
            tally.Report(new Uri("http://cdn.test/x.png"), ProxyBlockReasons.NotAllowed);
            Assert.Equal(2, changes);

            tally.Reset();

            Assert.Equal(0, tally.TotalBlocks);
            Assert.Empty(tally.Snapshot());
            Assert.Equal(3, changes);

            // Reset of an already-empty tally is a quiet no-op (no spurious indicator churn).
            tally.Reset();
            Assert.Equal(3, changes);
        }

        [Fact]
        public void NullAndEmptyRefusalsCountNothing()
        {
            var tally = Tally(new List<string>());
            tally.Report(null, ProxyBlockReasons.NotAllowed);
            tally.Report(new Uri("http://cdn.test/"), null);
            tally.Report(new Uri("http://cdn.test/"), "");

            Assert.Equal(0, tally.TotalBlocks);
            Assert.Empty(tally.Snapshot());
        }

        [Fact]
        public void EntriesAreBoundedAndEvictTheLeastRecentlySeen()
        {
            var ticks = 0;
            var tally = Tally(new List<string>(), () => T0.AddSeconds(++ticks));

            for (var i = 0; i < ProxyBlockTally.MaxEntries; i++)
            {
                tally.Report(new Uri($"http://host{i:D3}.test/"), ProxyBlockReasons.NotAllowed);
            }

            Assert.Equal(ProxyBlockTally.MaxEntries, tally.Snapshot().Count);
            var total = tally.TotalBlocks;

            // One more distinct origin: the row evicted is the least recently SEEN (host000).
            tally.Report(new Uri("http://newcomer.test/"), ProxyBlockReasons.NotAllowed);

            Assert.Equal(ProxyBlockTally.MaxEntries, tally.Snapshot().Count);
            Assert.Equal(total + 1, tally.TotalBlocks);
            var rows = tally.Snapshot();
            Assert.NotNull(rows.Find(row => row.Origin == "http://newcomer.test"));
            Assert.Null(rows.Find(row => row.Origin == "http://host000.test"));
            Assert.NotNull(rows.Find(row => row.Origin == "http://host001.test"));
        }

        [Fact]
        public void RefusalsForwardToTheEsReporterAndLocalCountsSurviveADeadForwarder()
        {
            var forwarded = new List<string>();
            var logs = new List<string>();
            var tally = Tally(logs);
            tally.SetForwarder(new RecordingReporter(forwarded));

            tally.Report(new Uri("https://Vendor.Test:8443/deep/path"), ProxyBlockReasons.TunnelRefused);
            Assert.Equal(new[] { "https://vendor.test:8443|" + ProxyBlockReasons.TunnelRefused }, forwarded);
            Assert.Equal(1, tally.TotalBlocks);

            // A throwing forwarder (dead messaging channel) must never reach the browser flow,
            // and the local count - what the indicator reads - stays intact.
            tally.SetForwarder(new ThrowingReporter());
            tally.Report(new Uri("http://cdn.test/x.png"), ProxyBlockReasons.NotAllowed);

            Assert.Equal(2, tally.TotalBlocks);
            Assert.NotNull(tally.Snapshot().Find(row => row.Reason == ProxyBlockReasons.NotAllowed));
            Assert.Contains("forward", string.Join("\n", logs).ToLowerInvariant());
        }

        [Fact]
        public void TallyComposedOverTheEsReporterSendsBothHalves()
        {
            // The production composition: the tally's forwarder IS the #69 ProxyBlockReporter.
            // One refusal must produce one ES payload AND one local row.
            ProxyBlockReportPayload sent = null;
            var esReporter = new ProxyBlockReporter(
                new ProxyViewSessionContext
                {
                    SessionId = "s-1",
                    WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001",
                    WorkspaceFingerprint = "fp-1",
                    ClientMachine = "BEAST",
                },
                payload => sent = payload,
                null);
            var tally = Tally(new List<string>());
            tally.SetForwarder(esReporter);

            tally.Report(new Uri("http://cdn.test/x.png"), ProxyBlockReasons.NotAllowed);

            Assert.NotNull(sent);
            Assert.Equal("http://cdn.test", sent.Origin);
            Assert.Equal(ProxyBlockReasons.NotAllowed, sent.Reason);
            Assert.Equal(1, tally.TotalBlocks);
        }

        [Fact]
        public void ForwarderSwapDetachesTheStoppedListenerReporter()
        {
            var forwarded = new List<string>();
            var tally = Tally(new List<string>());
            tally.SetForwarder(new RecordingReporter(forwarded));
            tally.SetForwarder(null);

            tally.Report(new Uri("http://cdn.test/x.png"), ProxyBlockReasons.NotAllowed);

            Assert.Empty(forwarded);
            Assert.Equal(1, tally.TotalBlocks);
        }

        [Fact]
        public void SubscriberFaultsNeverBreakTheCountOrThrowIntoTheFlow()
        {
            var logs = new List<string>();
            var tally = Tally(logs);
            tally.Changed += () => throw new InvalidOperationException("indicator went boom");

            tally.Report(new Uri("http://cdn.test/x.png"), ProxyBlockReasons.NotAllowed);

            Assert.Equal(1, tally.TotalBlocks);
            Assert.Contains("indicator went boom", string.Join("\n", logs));
        }

        [Fact]
        public void ReasonLabelsMatchTheSharedCauseCategories()
        {
            Assert.Equal("not allowed by administrator", ProxyBlockDisplay.ReasonLabel(ProxyBlockReasons.NotAllowed));
            Assert.Equal("response too large", ProxyBlockDisplay.ReasonLabel(ProxyBlockReasons.TooLarge));
            Assert.Equal("certificate not trusted", ProxyBlockDisplay.ReasonLabel(ProxyBlockReasons.CertUntrusted));
            Assert.Equal("target unreachable", ProxyBlockDisplay.ReasonLabel(ProxyBlockReasons.Unreachable));
            Assert.Equal("target timed out", ProxyBlockDisplay.ReasonLabel(ProxyBlockReasons.Timeout));
            Assert.Equal("proxy tunnel refused", ProxyBlockDisplay.ReasonLabel(ProxyBlockReasons.TunnelRefused));
            Assert.Equal("(unknown)", ProxyBlockDisplay.ReasonLabel("SomethingElse"));
        }

        private sealed class RecordingReporter : IProxyBlockReporter
        {
            private readonly List<string> _seen;

            internal RecordingReporter(List<string> seen) => _seen = seen;

            public void Report(Uri origin, string reason) => _seen.Add(ProxyPolicy.CanonicalOrigin(origin) + "|" + reason);
        }

        private sealed class ThrowingReporter : IProxyBlockReporter
        {
            public void Report(Uri origin, string reason) => throw new InvalidOperationException("messaging channel is dead");
        }
    }
}
