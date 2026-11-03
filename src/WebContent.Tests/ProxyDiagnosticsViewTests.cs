using System;
using System.Collections.Generic;
using System.Linq;

using WebContent.Admin;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #84: the Management Client Diagnostics tab's snapshot → view transform pinned as
    /// pure logic — the failure postures shared with the Status tab (a lost query is never an
    /// empty list), newest-first row order, the shared cause labels and recency strings, the
    /// already-allowlisted annotation driven through THE shared matcher (wildcard cover AND the
    /// SSRF floor), and the acceptance-critical rule that CertUntrusted rows never offer an
    /// "Add to allowlist" promotion.
    /// </summary>
    public class ProxyDiagnosticsViewTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        private static ProxyBlockEntry Blocked(
            string origin = "http://cdn.example.com",
            string reason = ProxyBlockReasons.NotAllowed,
            long count = 1,
            DateTime? seenUtc = null)
        {
            return new ProxyBlockEntry
            {
                Origin = origin,
                Reason = reason,
                Count = count,
                LastSeenUtc = seenUtc ?? T0.AddSeconds(-47),
            };
        }

        private static ProxyStatusResponsePayload Answer(params ProxyBlockEntry[] blocked)
        {
            return new ProxyStatusResponsePayload
            {
                Success = true,
                WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001",
                Sessions = new List<ProxySessionStatusEntry>(),
                Blocked = new List<ProxyBlockEntry>(blocked),
            };
        }

        private static ProxyDiagnosticsView Build(
            ProxyStatusResponsePayload payload,
            IEnumerable<string> entries = null,
            IEnumerable<string> floorHosts = null)
        {
            return ProxyDiagnosticsViewBuilder.Build(payload, entries, floorHosts, T0);
        }

        // ---- failure postures: identical discipline to the Status tab -------------------------

        [Fact]
        public void NoAnswerIsAFailureNamedServerUnavailable()
        {
            var view = Build(null);
            Assert.False(view.QuerySucceeded);
            Assert.Equal(ProxyFetchErrorCodes.ServerUnavailable, view.FailureCode);
            Assert.Empty(view.Rows);
        }

        [Fact]
        public void FailedAnswerKeepsItsOwnCodeAndAnEmptyCodeFallsBack()
        {
            var gone = Build(new ProxyStatusResponsePayload { Success = false, ErrorCode = ProxyFetchErrorCodes.BadRequest });
            Assert.False(gone.QuerySucceeded);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, gone.FailureCode);

            var blank = Build(new ProxyStatusResponsePayload { Success = false, ErrorCode = "  " });
            Assert.False(blank.QuerySucceeded);
            Assert.Equal(ProxyFetchErrorCodes.ServerUnavailable, blank.FailureCode);
        }

        [Fact]
        public void EmptyRingIsAnHonestSuccessWithNoRows()
        {
            var view = Build(Answer());
            Assert.True(view.QuerySucceeded);
            Assert.Equal(0, view.BlockedCount);
            Assert.Empty(view.Rows);

            var nullList = Build(new ProxyStatusResponsePayload { Success = true, Blocked = null });
            Assert.True(nullList.QuerySucceeded);
            Assert.Equal(0, nullList.BlockedCount);
        }

        // ---- row rendering ---------------------------------------------------------------------

        [Fact]
        public void RowsSortNewestRefusalFirst()
        {
            // The ring answers oldest-first (its eviction order); the admin reads newest-first.
            var view = Build(Answer(
                Blocked("https://oldest.example", seenUtc: T0.AddMinutes(-30)),
                Blocked("https://middle.example", seenUtc: T0.AddMinutes(-5)),
                Blocked("https://newest.example", seenUtc: T0.AddSeconds(-12))));

            Assert.Equal(3, view.BlockedCount);
            Assert.Equal(
                new[] { "https://newest.example", "https://middle.example", "https://oldest.example" },
                view.Rows.Select(r => r.Origin).ToArray());
        }

        [Fact]
        public void EqualTimestampsOrderByOriginOrdinalSoRefreshesAreStable()
        {
            var seen = T0.AddMinutes(-2);
            var view = Build(Answer(
                Blocked("https://b.example", seenUtc: seen),
                Blocked("https://a.example", seenUtc: seen)));

            Assert.Equal(new[] { "https://a.example", "https://b.example" }, view.Rows.Select(r => r.Origin).ToArray());
        }

        [Fact]
        public void BlankAndNullRingEntriesNeverRender()
        {
            var view = Build(Answer(
                null,
                new ProxyBlockEntry { Origin = "   ", Reason = ProxyBlockReasons.Timeout, Count = 4, LastSeenUtc = T0 },
                Blocked("https://real.example")));

            Assert.Single(view.Rows);
            Assert.Equal("https://real.example", view.Rows[0].Origin);
        }

        [Fact]
        public void EveryCauseCategoryGetsItsSharedLabel()
        {
            // Same vocabulary as the Smart Client's blocked list (#83) and the error pages (#66).
            var view = Build(Answer(
                Blocked("https://a.example", ProxyBlockReasons.NotAllowed),
                Blocked("https://b.example", ProxyBlockReasons.TooLarge),
                Blocked("https://c.example", ProxyBlockReasons.CertUntrusted),
                Blocked("https://d.example", ProxyBlockReasons.Unreachable),
                Blocked("https://e.example", ProxyBlockReasons.Timeout),
                Blocked("https://f.example", ProxyBlockReasons.TunnelRefused),
                Blocked("https://g.example", "SomethingFuture")));

            Assert.Equal("not allowed by administrator", view.Rows.Single(r => r.Origin == "https://a.example").ReasonLabel);
            Assert.Equal("response too large", view.Rows.Single(r => r.Origin == "https://b.example").ReasonLabel);
            Assert.Equal("certificate not trusted", view.Rows.Single(r => r.Origin == "https://c.example").ReasonLabel);
            Assert.Equal("target unreachable", view.Rows.Single(r => r.Origin == "https://d.example").ReasonLabel);
            Assert.Equal("target timed out", view.Rows.Single(r => r.Origin == "https://e.example").ReasonLabel);
            Assert.Equal("proxy tunnel refused", view.Rows.Single(r => r.Origin == "https://f.example").ReasonLabel);
            Assert.Equal("(unknown)", view.Rows.Single(r => r.Origin == "https://g.example").ReasonLabel);
        }

        [Fact]
        public void HitsAndLastSeenArePreFormattedDisplayStrings()
        {
            var view = Build(Answer(
                new ProxyBlockEntry { Origin = "https://a.example", Reason = ProxyBlockReasons.Timeout, Count = 0, LastSeenUtc = T0.AddSeconds(-3) },
                new ProxyBlockEntry { Origin = "https://b.example", Reason = ProxyBlockReasons.Timeout, Count = 4711, LastSeenUtc = T0.AddSeconds(-42) },
                new ProxyBlockEntry { Origin = "https://c.example", Reason = ProxyBlockReasons.Timeout, Count = 9, LastSeenUtc = T0.AddMinutes(-4).AddSeconds(-7) },
                new ProxyBlockEntry { Origin = "https://d.example", Reason = ProxyBlockReasons.Timeout, Count = 1, LastSeenUtc = T0.AddHours(-2).AddMinutes(-5) }));

            Assert.Equal("1", view.Rows.Single(r => r.Origin == "https://a.example").Hits);   // count floor is one sighting
            Assert.Equal("just now", view.Rows.Single(r => r.Origin == "https://a.example").LastSeen);
            Assert.Equal("4711", view.Rows.Single(r => r.Origin == "https://b.example").Hits);
            Assert.Equal("42s ago", view.Rows.Single(r => r.Origin == "https://b.example").LastSeen);
            Assert.Equal("4m 07s ago", view.Rows.Single(r => r.Origin == "https://c.example").LastSeen);
            Assert.Equal("2h 05m ago", view.Rows.Single(r => r.Origin == "https://d.example").LastSeen);
        }

        [Fact]
        public void FutureTimestampsReadJustNowNeverNegative()
        {
            var view = Build(Answer(Blocked("https://skew.example", seenUtc: T0.AddMinutes(5))));
            Assert.Equal("just now", view.Rows[0].LastSeen);
        }

        // ---- the promotion decision ---------------------------------------------------------------

        [Fact]
        public void CertUntrustedRowsNeverOfferAnAllowlistPromotion()
        {
            // #84 AC, pinned in the pure layer: the fix there is the CA trust path. The row still
            // renders (with its label and an annotated Status cell), it simply has no button.
            var view = Build(Answer(Blocked("https://corp.example", ProxyBlockReasons.CertUntrusted)));

            var row = view.Rows[0];
            Assert.Equal(ProxyBlockedRowStatus.CertTrust, row.StatusCode);
            Assert.False(row.CanPromote);
        }

        [Fact]
        public void CertTrustedPosturesAllOfferPromotion()
        {
            var view = Build(Answer(
                Blocked("https://a.example", ProxyBlockReasons.NotAllowed),
                Blocked("https://b.example", ProxyBlockReasons.TooLarge),
                Blocked("https://c.example", ProxyBlockReasons.Unreachable),
                Blocked("https://d.example", ProxyBlockReasons.Timeout),
                Blocked("https://e.example", ProxyBlockReasons.TunnelRefused)));

            Assert.All(view.Rows, row =>
            {
                Assert.Equal(ProxyBlockedRowStatus.None, row.StatusCode);
                Assert.True(row.CanPromote);
            });
        }

        [Fact]
        public void ExactEntryCoveringTheOriginAnnotatesAndRemovesTheButton()
        {
            var view = Build(
                Answer(Blocked("https://10.0.0.5:8443")),
                new[] { "https://10.0.0.5:8443" });

            var row = view.Rows[0];
            Assert.Equal(ProxyBlockedRowStatus.Allowlisted, row.StatusCode);
            Assert.False(row.CanPromote);
        }

        [Fact]
        public void WildcardEntryCoveringTheOriginAnnotatesTooThroughTheSameMatcher()
        {
            var view = Build(
                Answer(Blocked("https://cdn.example.com")),
                new[] { "https://*.example.com" });

            Assert.Equal(ProxyBlockedRowStatus.Allowlisted, view.Rows[0].StatusCode);
            Assert.False(view.Rows[0].CanPromote);
        }

        [Fact]
        public void WildcardFloorKeepsTheButtonOnOriginsTheEntryCouldNeverServe()
        {
            // The annotation runs through the gates' matcher INCLUDING the wildcard SSRF floor, so
            // "https://*" does not annotate a localhost block as allowed - and the promotion button
            // stays, because an exact entry is precisely what a floored origin needs.
            var view = Build(
                Answer(
                    Blocked("https://localhost:8443"),
                    Blocked("https://10.0.0.9")),
                new[] { "https://*" });

            Assert.All(view.Rows, row =>
            {
                Assert.Equal(ProxyBlockedRowStatus.None, row.StatusCode);
                Assert.True(row.CanPromote);
            });

            // ... while the same origins WOULD annotate under explicit exact entries.
            var explicitView = Build(
                Answer(Blocked("https://localhost:8443")),
                new[] { "https://localhost:8443" });
            Assert.Equal(ProxyBlockedRowStatus.Allowlisted, explicitView.Rows[0].StatusCode);
        }

        [Fact]
        public void VmsFloorHostsFeedTheAnnotationLikeEveryGate()
        {
            var view = Build(
                Answer(Blocked("https://vms.example.com")),
                new[] { "https://*" },
                new[] { "vms.example.com" });

            Assert.Equal(ProxyBlockedRowStatus.None, view.Rows[0].StatusCode);
            Assert.True(view.Rows[0].CanPromote);
        }

        [Fact]
        public void UnparsableOriginRendersButIsNotPromotable()
        {
            var view = Build(Answer(Blocked("gibberish-origin")));
            var row = view.Rows[0];
            Assert.Equal("gibberish-origin", row.Origin);   // the admin still sees what was blocked
            Assert.Equal(ProxyBlockedRowStatus.None, row.StatusCode);
            Assert.False(row.CanPromote);
        }

        [Fact]
        public void NoEntriesAtAllStillOffersPromotionOnPlainBlocks()
        {
            var view = Build(Answer(Blocked("https://cdn.example.com")), null);
            Assert.True(view.Rows[0].CanPromote);
        }
    }
}
