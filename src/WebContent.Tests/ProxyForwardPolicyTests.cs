using System;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #54 AC4: the proxied decision is taken by the pure policy class from the config
    /// ticket (#52) — allowlisted origins forward, everything else is refused with an explicit
    /// HTTP status the browser can show. Address-bar navigation follows the same table as
    /// page subresources because both arrive as proxied requests.
    /// </summary>
    public class ProxyForwardPolicyTests
    {
        private static readonly string[] Allowlist = { "http://10.0.0.5:8080" };

        [Fact]
        public void ForwardsGetRequestsToAllowlistedOrigins()
        {
            var head = "GET http://10.0.0.5:8080/monitor/live HTTP/1.1\r\nHost: 10.0.0.5:8080\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.Forward, decision);
            Assert.Equal("http://10.0.0.5:8080/monitor/live", request.Uri.AbsoluteUri);
            Assert.Null(rejectReason);
        }

        [Fact]
        public void RefusesNonAllowlistedOriginsEvenForAddressBarNavigation()
        {
            var head = "GET http://somewhere.else.test/dashboard HTTP/1.1\r\nHost: somewhere.else.test\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.OriginNotAllowed, decision);
            Assert.Contains("somewhere.else.test", rejectReason);
        }

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        [InlineData("OPTIONS")]
        [InlineData("PATCH")]
        [InlineData("HEAD")]
        public void ForwardsWriteMethodsToAllowlistedOrigins(string method)
        {
            // #55: vendor login flows are POSTs with form bodies; background XHR uses PUT/DELETE/
            // OPTIONS/PATCH. The ES fetcher replays them, so the forwarder no longer refuses them.
            var head = $"{method} http://10.0.0.5:8080/login HTTP/1.1\r\nContent-Length: 15\r\nContent-Type: application/json\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.Forward, decision);
            Assert.Equal(method, request.Method);
            Assert.Equal("15", request.Headers["Content-Length"]);
            Assert.Null(rejectReason);
        }

        [Fact]
        public void RefusesUnknownMethodsWith405InsteadOfForwardingThem()
        {
            // The method allowlist is explicit: TRACE and junk verbs stay refused, never relayed.
            var head = "TRACE http://10.0.0.5:8080/debug HTTP/1.1\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.MethodNotAllowed, decision);
            Assert.Contains("TRACE", rejectReason);
        }

        [Fact]
        public void RefusesWriteMethodsToNonAllowlistedOriginsWith403()
        {
            // The origin gate runs for POST exactly as for GET — a POST to an origin no proxied
            // workspace allowlists never reaches the fetcher.
            var head = "POST http://elsewhere.test/login HTTP/1.1\r\nContent-Length: 5\r\n\r\nx=1";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.OriginNotAllowed, decision);
            Assert.Contains("elsewhere.test", rejectReason);
        }

        [Fact]
        public void RefusesConnectTunnelsWithAnExplanation()
        {
            // https would reach the listener as CONNECT; a CONNECT-less forwarder cannot relay it,
            // and the browser must never be allowed to go direct instead.
            var head = "CONNECT portal.example.com:443 HTTP/1.1\r\nProxy-Connection: keep-alive\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(new[] { "https://portal.example.com" }, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.ConnectRefused, decision);
            Assert.Contains("portal.example.com:443", rejectReason);
        }

        [Fact]
        public void RejectsMalformedProxyRequestLines()
        {
            var decision = ProxyForwardPolicy.Decide(Allowlist, "GARBAGE", out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.BadRequest, decision);
            Assert.Null(request);
        }
    }
}
