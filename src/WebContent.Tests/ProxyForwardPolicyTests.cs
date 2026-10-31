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

        [Fact]
        public void RefusesMethodsWithBodyInsteadOfForwardingThem()
        {
            // The Event Server fetcher (#53) is GET-only; a POST must be refused locally with an
            // explicit 405, never quietly forwarded and never answered from the client machine.
            var head = "POST http://10.0.0.5:8080/login HTTP/1.1\r\nContent-Length: 12\r\n\r\nuser=x&pwd=y";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.MethodNotAllowed, decision);
            Assert.Contains("POST", rejectReason);
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
