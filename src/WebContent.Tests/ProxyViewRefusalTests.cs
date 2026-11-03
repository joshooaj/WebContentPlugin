using System;
using System.Collections.Generic;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #88: the view-layer refusal for off-allowlist https requests. The decision is the
    /// ONE shared ProxyPolicy matcher over the same canonical entries + VMS floor the loopback
    /// listener gates on (the four-gate agreement pin lives in ProxyAllowlistGateAgreementTests);
    /// these rows pin WHAT the refusal carries: the #66 friendly 403 page - NotAllowed cause
    /// chip, the verbatim "not in the workspace proxy allowlist" sentence, and the canonical
    /// https entry that would admit the requested origin - while http requests and allowlisted
    /// origins fall through untouched (the proxy's absolute-form path is unchanged).
    /// </summary>
    public class ProxyViewRefusalTests
    {
        private static readonly string[] Allowlist =
        {
            "http://httpforever.com",
            "https://cam1.test:8443",
            "https://*.portal.test",
        };

        private static readonly string[] VmsFloor = { "vms.joshooaj.com" };

        [Fact]
        public void RefusesOffAllowlistHttpsOriginWithTheFriendlyForbiddenPage()
        {
            var refused = ProxyViewRefusal.TryRefuseHttpsRequest(
                Allowlist, VmsFloor, new Uri("https://neverssl.com/some/page"), "GET", out var reason, out var html);

            Assert.True(refused);
            Assert.Contains("'https://neverssl.com' is not in the workspace proxy allowlist", reason);

            // The #66 page shape: cause chip + headline up front...
            Assert.Contains("<title>Web Content proxy: 403</title>", html);
            Assert.Contains(ProxyErrorPage.CauseLabel(ProxyErrorPage.CategoryNotAllowed), html);
            Assert.Contains(ProxyErrorPage.Headline(ProxyErrorPage.CategoryNotAllowed), html);

            // ...the shared refusal sentence verbatim, the side attribution, and the canonical
            // HTTPS entry (the scheme the user requested, not the http twin).
            Assert.Contains("not in the workspace proxy allowlist", html);
            Assert.Contains(ProxyErrorPage.DecidedByLocalListener, html);
            Assert.Contains("Allowlist entry that would admit this origin: https://neverssl.com", html);
        }

        [Fact]
        public void TheHttpTwinOfAnAllowlistedHttpEntryDoesNotUnlockItsHttpsSibling()
        {
            // #57's posture in one line: an http entry admits the origin's ws CONNECT tunnel,
            // never https browsing to it - the entry's scheme IS the posture, and the view gate
            // reads it the same way the forward gate does.
            var refused = ProxyViewRefusal.TryRefuseHttpsRequest(
                Allowlist, VmsFloor, new Uri("https://httpforever.com/"), "GET", out var reason, out var html);

            Assert.True(refused);
            Assert.Contains("Allowlist entry that would admit this origin: https://httpforever.com", html);
        }

        [Fact]
        public void AllowlistedHttpsOriginsAndSubdomainsFallThroughUntouched()
        {
            foreach (var uri in new[]
            {
                "https://cam1.test:8443/live/index.html",
                "https://a.portal.test/dashboard",
                "https://deep.sub.portal.test/",
            })
            {
                var refused = ProxyViewRefusal.TryRefuseHttpsRequest(Allowlist, VmsFloor, new Uri(uri), "GET", out var reason, out var html);

                Assert.False(refused, $"{uri} must reach the network (the CONNECT/terminate path), not the view refusal");
                Assert.Null(reason);
                Assert.Null(html);
            }
        }

        [Fact]
        public void PlainHttpRequestsNeverEnterTheViewGate()
        {
            // Absolute-form http already rides the proxy's own 403 page (#58/#66 live rows);
            // the view gate must not double-answer it.
            foreach (var uri in new[] { "http://neverssl.com/", "http://elsewhere.test/x" })
            {
                var refused = ProxyViewRefusal.TryRefuseHttpsRequest(new string[0], VmsFloor, new Uri(uri), "GET", out _, out _);
                Assert.False(refused, $"{uri} stays on the forward path");
            }
        }

        [Fact]
        public void TheWildcardSsrfFloorRefusesSameAtTheViewAsAtTheListener()
        {
            var hatch = new[] { "https://*" };

            Assert.True(ProxyViewRefusal.TryRefuseHttpsRequest(hatch, VmsFloor, new Uri("https://vms.joshooaj.com/"), "GET", out _, out _));
            Assert.True(ProxyViewRefusal.TryRefuseHttpsRequest(hatch, VmsFloor, new Uri("https://10.20.30.40/"), "GET", out _, out _));
            Assert.True(ProxyViewRefusal.TryRefuseHttpsRequest(hatch, VmsFloor, new Uri("https://localhost/"), "GET", out _, out _));

            Assert.False(ProxyViewRefusal.TryRefuseHttpsRequest(hatch, VmsFloor, new Uri("https://anything.example.test/"), "GET", out _, out _));

            // Exact entries may still name a floored host - the floor only binds patterns.
            var exact = new[] { "https://vms.joshooaj.com" };
            Assert.False(ProxyViewRefusal.TryRefuseHttpsRequest(exact, VmsFloor, new Uri("https://vms.joshooaj.com/"), "GET", out _, out _));
        }

        [Fact]
        public void TheApexOfAWildcardTailStaysRefused()
        {
            Assert.True(ProxyViewRefusal.TryRefuseHttpsRequest(Allowlist, VmsFloor, new Uri("https://portal.test/"), "GET", out _, out var html));
            Assert.Contains("Allowlist entry that would admit this origin: https://portal.test", html);
        }

        [Fact]
        public void DefensiveInputsAnswerHonestly()
        {
            // A null uri is nothing to refuse; a null entry set is the FAIL-CLOSED reading
            // (nothing is allowlisted, so an https request is refused), identical to what the
            // listener's own gate answers for an empty target list.
            Assert.False(ProxyViewRefusal.TryRefuseHttpsRequest(Allowlist, VmsFloor, null, "GET", out _, out _));

            Assert.True(ProxyViewRefusal.TryRefuseHttpsRequest(null, VmsFloor, new Uri("https://x.test/"), "GET", out _, out _));
            Assert.True(ProxyViewRefusal.TryRefuseHttpsRequest(new string[0], VmsFloor, new Uri("https://x.test/"), "GET", out _, out _));
        }

        [Fact]
        public void TheViewGateAndTheForwardGateRefuseTheSameHttpsOrigins()
        {
            // The #88 divergence guard: for every posture, the view's https gate and the
            // listener's CONNECT decision must answer the same way - whatever the view refuses
            // is what the listener would have refused (and the view never widens anything).
            var probes = new[]
            {
                "https://cam1.test:8443/live",      // allowlisted https entry
                "https://a.portal.test/x",          // wildcard subdomain
                "https://portal.test/",             // wildcard apex - refused by both
                "https://httpforever.com/",         // http twin only - refused for https by both
                "https://neverssl.com/",            // nowhere - refused by both
            };

            foreach (var probe in probes)
            {
                var uri = new Uri(probe);
                var viewRefused = ProxyViewRefusal.TryRefuseHttpsRequest(Allowlist, VmsFloor, uri, "GET", out _, out _);

                var connectHead = $"CONNECT {uri.Host}:{uri.Port} HTTP/1.1\r\n\r\n";
                var forward = ProxyForwardPolicy.Decide(Allowlist, connectHead, null, VmsFloor, out _, out _);
                var forwardRefused = forward == ProxyForwardDisposition.OriginNotAllowed;

                Assert.Equal(forwardRefused, viewRefused);
            }
        }
    }
}
