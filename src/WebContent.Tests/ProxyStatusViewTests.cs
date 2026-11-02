using System;
using System.Collections.Generic;
using System.Linq;
using WebContent.Admin;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #76: the Management Client Status tab's snapshot → view transform pinned as pure
    /// logic — the failure postures (a lost query must never render as an empty table), the
    /// per-session rate differencing over the Event Server's cumulative counters, and the exact
    /// culture-invariant strings the admin reads.
    /// </summary>
    public class ProxyStatusViewTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        private static ProxySessionStatusEntry Entry(
            string id = "session-1",
            string user = "jhadmin",
            string machine = "BEAST",
            long requests = 10,
            double perMinute = 20.0,
            long sent = 1024,
            long received = 2048)
        {
            return new ProxySessionStatusEntry
            {
                SessionId = id,
                UserName = user,
                ClientMachine = machine,
                OpenedUtc = T0.AddMinutes(-4).AddSeconds(-7),
                LastActivityUtc = T0.AddSeconds(-47),
                Requests = requests,
                RequestsPerMinute = perMinute,
                BytesSent = sent,
                BytesReceived = received,
            };
        }

        private static ProxyStatusResponsePayload Answer(params ProxySessionStatusEntry[] sessions)
        {
            return new ProxyStatusResponsePayload
            {
                Success = true,
                WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001",
                Sessions = new List<ProxySessionStatusEntry>(sessions),
            };
        }

        [Fact]
        public void NoAnswerIsAFailureNamedServerUnavailable()
        {
            // The page must show the failure, never an empty "no sessions" table, when the
            // Event Server never replied at all.
            var view = ProxyStatusViewBuilder.Build(null, new ProxyStatusRateTracker(), T0);
            Assert.False(view.QuerySucceeded);
            Assert.Equal(ProxyFetchErrorCodes.ServerUnavailable, view.FailureCode);
            Assert.Empty(view.Sessions);
        }

        [Fact]
        public void FailedAnswerKeepsItsOwnCodeAndAnEmptyCodeFallsBack()
        {
            // "Workspace gone" (BadRequest) reads differently from "no live sessions" - the code
            // the Event Server sent must survive the transform untouched.
            var gone = ProxyStatusViewBuilder.Build(
                new ProxyStatusResponsePayload { Success = false, ErrorCode = ProxyFetchErrorCodes.BadRequest },
                new ProxyStatusRateTracker(),
                T0);
            Assert.False(gone.QuerySucceeded);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, gone.FailureCode);

            // A failure frame without a code (or with a blank one) is indistinguishable from
            // silence for the page - the fallback names that.
            var blank = ProxyStatusViewBuilder.Build(
                new ProxyStatusResponsePayload { Success = false, ErrorCode = "  " },
                new ProxyStatusRateTracker(),
                T0);
            Assert.False(blank.QuerySucceeded);
            Assert.Equal(ProxyFetchErrorCodes.ServerUnavailable, blank.FailureCode);
        }

        [Fact]
        public void ZeroSessionsIsAnHonestSuccessWithNoRows()
        {
            // "The Event Server answered and nothing is browsing" is the success path the tab
            // renders as its quiet message - not a failure banner.
            var view = ProxyStatusViewBuilder.Build(Answer(), new ProxyStatusRateTracker(), T0);
            Assert.True(view.QuerySucceeded);
            Assert.Equal(0, view.SessionCount);
            Assert.Empty(view.Sessions);
            Assert.Equal(0d, view.TotalRequestsPerMinute);

            var nullList = ProxyStatusViewBuilder.Build(
                new ProxyStatusResponsePayload { Success = true },
                new ProxyStatusRateTracker(),
                T0);
            Assert.True(nullList.QuerySucceeded);
            Assert.Equal(0, nullList.SessionCount);
        }

        [Fact]
        public void RowCarriesDisplayStringsPlaceholdersAndZeroFirstSightingRates()
        {
            // Every cell arrives pre-formatted so the grid binds straight through; a first
            // sighting has no baseline, so the rate columns are honestly zero, never the whole
            // session's life divided by nothing.
            var view = ProxyStatusViewBuilder.Build(
                Answer(Entry(user: "", machine: null, requests: 1234, perMinute: 7.5)),
                new ProxyStatusRateTracker(),
                T0);

            var row = Assert.Single(view.Sessions);
            Assert.Equal(ProxyStatusViewBuilder.UnknownText, row.UserName);
            Assert.Equal(ProxyStatusViewBuilder.UnknownText, row.ClientMachine);
            Assert.Equal("4m 07s", row.Duration);
            Assert.Equal("47s", row.IdleFor);
            Assert.Equal("1234", row.Requests);
            Assert.Equal("7.5", row.RequestsPerMinute);
            Assert.Equal("1.0 KB", row.BytesSent);
            Assert.Equal("2.0 KB", row.BytesReceived);
            Assert.Equal("0 B/s", row.SendRate);
            Assert.Equal("0 B/s", row.ReceiveRate);
        }

        [Fact]
        public void AggregateSumsAcrossSessionsAndSkipsNullEntries()
        {
            var payload = Answer(Entry(id: "a", perMinute: 20.0), null, Entry(id: "b", perMinute: 0.5));
            var view = ProxyStatusViewBuilder.Build(payload, new ProxyStatusRateTracker(), T0);

            Assert.Equal(2, view.SessionCount);
            Assert.Equal(20.5, view.TotalRequestsPerMinute, 3);
        }

        [Fact]
        public void RatesDifferenceConsecutiveSnapshotsOfTheCumulativeCounters()
        {
            var tracker = new ProxyStatusRateTracker();

            // First sighting: no baseline, zero rate, but the row still renders its counters.
            var first = ProxyStatusViewBuilder.Build(Answer(Entry(sent: 1024, received: 2048)), tracker, T0);
            Assert.Equal("0 B/s", Assert.Single(first.Sessions).SendRate);

            // +4096 sent / +8192 received across 4 seconds → exactly 1024 B/s and 2048 B/s.
            var second = ProxyStatusViewBuilder.Build(
                Answer(Entry(sent: 5120, received: 10240)),
                tracker,
                T0.AddSeconds(4));
            var row = Assert.Single(second.Sessions);
            Assert.Equal("1.0 KB/s", row.SendRate);
            Assert.Equal("2.0 KB/s", row.ReceiveRate);
            Assert.Equal(1024d, second.SendBytesPerSecond, 3);
            Assert.Equal(2048d, second.ReceiveBytesPerSecond, 3);
        }

        [Fact]
        public void RateTrackerRefusesToInventRatesFromImpossibleDeltas()
        {
            var tracker = new ProxyStatusRateTracker();
            tracker.For(Entry(sent: 100, received: 100), T0);

            // A snapshot no later than the stored one can never divide by zero seconds.
            var same = tracker.For(Entry(sent: 900, received: 900), T0);
            Assert.Equal(0d, same.SendBytesPerSecond);
            Assert.Equal(0d, same.ReceiveBytesPerSecond);

            // Counters going backwards (a session id reused after an Event Server restart) read
            // as zero, never as a negative rate.
            var backwards = tracker.For(Entry(sent: 10, received: 10), T0.AddSeconds(5));
            Assert.Equal(0d, backwards.SendBytesPerSecond);
            Assert.Equal(0d, backwards.ReceiveBytesPerSecond);

            // Unknown ids and entry-less frames are zero, not exceptions.
            var unknown = tracker.For(Entry(id: "never-seen"), T0.AddSeconds(10));
            Assert.Equal(0d, unknown.SendBytesPerSecond);
            Assert.Equal(ProxyStatusRates.Zero, tracker.For(null, T0.AddSeconds(10)));
        }

        [Fact]
        public void ResetDropsTheBaselineSoTheNextSightingIsAFreshZero()
        {
            // A reopen of the tab or a switch to another workspace item must not splice rates
            // across contexts - the counters are per Event Server process.
            var tracker = new ProxyStatusRateTracker();
            tracker.For(Entry(sent: 100, received: 100), T0);
            tracker.Reset();

            var after = tracker.For(Entry(sent: 5000, received: 5000), T0.AddSeconds(3));
            Assert.Equal(0d, after.SendBytesPerSecond);
            Assert.Equal(0d, after.ReceiveBytesPerSecond);
        }

        [Fact]
        public void ByteFormattingIsBinaryInvariantAndClampsGarbage()
        {
            Assert.Equal("0 B", ProxyStatusFormat.Bytes(0));
            Assert.Equal("1023 B", ProxyStatusFormat.Bytes(1023));
            Assert.Equal("1.0 KB", ProxyStatusFormat.Bytes(1024));
            Assert.Equal("1.5 KB", ProxyStatusFormat.Bytes(1536));
            Assert.Equal("1.0 MB", ProxyStatusFormat.Bytes(1024 * 1024));
            Assert.Equal("1.0 GB", ProxyStatusFormat.Bytes(1024L * 1024L * 1024L));
            Assert.Equal("2.0 TB", ProxyStatusFormat.Bytes(2L * 1024L * 1024L * 1024L * 1024L));
            Assert.Equal("0 B", ProxyStatusFormat.Bytes(-42));
        }

        [Fact]
        public void RateFormattingRoundsAndNeverPrintsNaNOrInfinity()
        {
            Assert.Equal("0 B/s", ProxyStatusFormat.Rate(0));
            Assert.Equal("0 B/s", ProxyStatusFormat.Rate(-5));
            Assert.Equal("0 B/s", ProxyStatusFormat.Rate(double.NaN));
            Assert.Equal("0 B/s", ProxyStatusFormat.Rate(double.PositiveInfinity));
            Assert.Equal("512 B/s", ProxyStatusFormat.Rate(512));
            Assert.Equal("1.5 KB/s", ProxyStatusFormat.Rate(1536));
        }

        [Fact]
        public void PerMinuteFormattingIsInvariantAndClampsGarbage()
        {
            Assert.Equal("0.0", ProxyStatusFormat.PerMinute(0));
            Assert.Equal("20.0", ProxyStatusFormat.PerMinute(20));
            Assert.Equal("0.0", ProxyStatusFormat.PerMinute(-1));
            Assert.Equal("0.0", ProxyStatusFormat.PerMinute(double.NaN));
            Assert.Equal("7.5", ProxyStatusFormat.PerMinute(7.5));
        }

        [Fact]
        public void DurationIsSecondsMinutesHoursAndNeverNegative()
        {
            Assert.Equal("0s", ProxyStatusFormat.Duration(TimeSpan.FromMinutes(-30)));
            Assert.Equal("59s", ProxyStatusFormat.Duration(TimeSpan.FromSeconds(59.9)));
            Assert.Equal("47s", ProxyStatusFormat.Duration(TimeSpan.FromSeconds(47)));
            Assert.Equal("4m 07s", ProxyStatusFormat.Duration(TimeSpan.FromSeconds(247)));
            Assert.Equal("2h 05m", ProxyStatusFormat.Duration(TimeSpan.FromMinutes(125)));
        }
    }
}
