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
        public void TerminatesConnectTunnelsToAllowlistedHttpsOrigins()
        {
            // #56: an allowlisted https origin gets its CONNECT accepted and terminated at the
            // loopback listener (ADR-0002) - the decrypted requests then ride the fetch pipeline.
            var head = "CONNECT cam1.test:8443 HTTP/1.1\r\nProxy-Connection: keep-alive\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(new[] { "https://cam1.test:8443" }, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.ConnectTerminate, decision);
            Assert.Equal("https://cam1.test:8443", ProxyPolicy.CanonicalOrigin(request.Uri));
            Assert.Null(rejectReason);
        }

        [Fact]
        public void TerminatesConnectToTheSchemeDefaultPortOfAnAllowlistedOrigin()
        {
            // The canonical allowlist drops the default port; the browser CONNECTs to :443 -
            // the same origin must match either spelling.
            var head = "CONNECT portal.example.com:443 HTTP/1.1\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(new[] { "https://portal.example.com" }, head, out var request, out _);

            Assert.Equal(ProxyForwardDisposition.ConnectTerminate, decision);
            Assert.Equal("https://portal.example.com", ProxyPolicy.CanonicalOrigin(request.Uri));
        }

        [Fact]
        public void RefusesConnectTunnelsToOriginsNoWorkspaceAllowlists()
        {
            // A CONNECT to an origin no proxied workspace allowlists is refused with the same
            // 403 posture as any other request, and the browser is never let go direct.
            var head = "CONNECT somewhere.else.test:443 HTTP/1.1\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.OriginNotAllowed, decision);
            Assert.Contains("somewhere.else.test", rejectReason);
        }

        [Fact]
        public void ConnectToHttpTwinOriginIsAcceptedForAWebsocketHandshakeOnly()
        {
            // #57 live repro: Chromium proxies plain ws:// as CONNECT + a plaintext websocket
            // handshake inside the tunnel, so an http allowlist entry DOES open a CONNECT tunnel -
            // but one the connection flow validates before any target socket opens (a non-ws
            // first-tunnel-bytes test lives in ProxyForwardConnectionTests).
            var head = "CONNECT 10.0.0.5:8080 HTTP/1.1\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.ConnectWebSocketTunnel, decision);
            Assert.Equal("https://10.0.0.5:8080", ProxyPolicy.CanonicalOrigin(request.Uri));
            Assert.Null(rejectReason);
        }

        [Fact]
        public void AnHttpsEntryWinsTheConnectOverItsHttpTwin()
        {
            // The allowlist entry's scheme is the posture: an origin listed as https gets the
            // #56 TLS-terminating tunnel, never the ws:// byte-tunnel posture, even though the
            // http twin would technically also match.
            var head = "CONNECT 10.0.0.5:8080 HTTP/1.1\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(
                new[] { "http://10.0.0.5:8080", "https://10.0.0.5:8080" }, head, out _, out _);

            Assert.Equal(ProxyForwardDisposition.ConnectTerminate, decision);
        }

        [Theory]
        [InlineData("CONNECT not-a-host:port HTTP/1.1\r\n\r\n")]
        [InlineData("CONNECT HTTP/1.1\r\n\r\n")]
        public void RejectsMalformedConnectTargets(string head)
        {
            var decision = ProxyForwardPolicy.Decide(new[] { "https://cam1.test:8443" }, head, out var request, out _);

            Assert.Equal(ProxyForwardDisposition.BadRequest, decision);
        }

        [Fact]
        public void RejectsMalformedProxyRequestLines()
        {
            var decision = ProxyForwardPolicy.Decide(Allowlist, "GARBAGE", out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.BadRequest, decision);
            Assert.Null(request);
        }
        [Fact]
        public void InnerTunnelRequestForwardsAgainstTheTunnelOrigin()
        {
            // The decrypted inner request of a terminated tunnel (#56): origin-form path, and the
            // origin gate runs on the tunnel origin, not on any header the client types.
            var head = "GET /live/index.html HTTP/1.1\r\nHost: cam1.test:8443\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(
                new[] { "https://cam1.test:8443" }, head, new Uri("https://cam1.test:8443"), out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.Forward, decision);
            Assert.Equal("https://cam1.test:8443/live/index.html", request.Uri.AbsoluteUri);
            Assert.Null(rejectReason);
        }

        [Fact]
        public void NestedConnectInsideATerminatedTunnelIsRefused()
        {
            var head = "CONNECT evil.test:443 HTTP/1.1\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(
                new[] { "https://cam1.test:8443" }, head, new Uri("https://cam1.test:8443"), out _, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.MethodNotAllowed, decision);
            Assert.Contains("CONNECT", rejectReason);
        }

        // #57: a plain ws:// upgrade reaches an HTTP proxy as an absolute-form GET carrying
        // Connection/Upgrade headers. The decision table routes it to the CONNECT-style tunnel,
        // never to the HTTP forward path; the origin gate is the ws origin spelled as http.

        [Fact]
        public void AllowlistedWebSocketUpgradeTunnelsInsteadOfForwarding()
        {
            var head =
                "GET ws://10.0.0.5:8080/ws/live HTTP/1.1\r\n" +
                "Host: 10.0.0.5:8080\r\n" +
                "Connection: keep-alive, Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Origin: http://10.0.0.5:8080\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.WebSocketTunnel, decision);
            Assert.Equal("http://10.0.0.5:8080/ws/live", request.Uri.AbsoluteUri);
            Assert.Null(rejectReason);
        }

        [Fact]
        public void TunnelsAbsoluteWssUpgradeToAnAllowlistedHttpsOrigin()
        {
            // #60: a wss upgrade rebased (by the codec) onto an allowlisted https origin rides the
            // same byte tunnel; the Event Server dials the TLS leg to the target.
            var head =
                "GET wss://cam1.test:8443/ws/live HTTP/1.1\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(new[] { "https://cam1.test:8443" }, head, out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.WebSocketTunnel, decision);
            Assert.Equal("https://cam1.test:8443/ws/live", request.Uri.AbsoluteUri);
            Assert.Null(rejectReason);
        }

        [Fact]
        public void RefusesWssUpgradeWhenOnlyItsHttpTwinIsAllowlisted()
        {
            // The entry's scheme stays the posture: an http entry admits the plain ws:// tunnel,
            // never a wss one - the wss origin is simply not on the list.
            var head =
                "GET wss://10.0.0.5:8080/ws/live HTTP/1.1\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out _, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.OriginNotAllowed, decision);
            Assert.Contains("10.0.0.5", rejectReason);
        }

        [Fact]
        public void RefusesWebSocketUpgradeToOriginsNoWorkspaceAllowlists()
        {
            var head =
                "GET http://elsewhere.test/ws HTTP/1.1\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out _, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.OriginNotAllowed, decision);
            Assert.Contains("elsewhere.test", rejectReason);
        }

        [Fact]
        public void RefusesWebSocketUpgradeWithoutTheHandshakeKey()
        {
            // A crafted "upgrade" without the mandatory client handshake fields is malformed, not
            // a tunnel candidate.
            var head =
                "GET http://10.0.0.5:8080/ws HTTP/1.1\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out _, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.BadRequest, decision);
            Assert.Contains("Sec-WebSocket", rejectReason);
        }

        [Fact]
        public void RefusesWebSocketUpgradeWithAnUnsupportedVersion()
        {
            var head =
                "GET http://10.0.0.5:8080/ws HTTP/1.1\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 8\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out _, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.BadRequest, decision);
            Assert.Contains("Sec-WebSocket-Version", rejectReason);
        }

        [Fact]
        public void TunnelsWebSocketUpgradeInsideATerminatedTunnel()
        {
            // #60: wss:// reaches this proxy as an upgrade inside a CONNECT tunnel the listener
            // terminated (#56). The upgrade gets its own disposition: the Event Server replays the
            // rebuilt handshake over a fresh TLS leg and admits the tunnel only on 101 - the same
            // full-validation posture as an https fetch.
            var head =
                "GET /ws/live HTTP/1.1\r\n" +
                "Host: cam1.test:8443\r\n" +
                "Connection: keep-alive, Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(
                new[] { "https://cam1.test:8443" }, head, new Uri("https://cam1.test:8443"), out var request, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.WebSocketTunnel, decision);
            Assert.Equal("https://cam1.test:8443/ws/live", request.Uri.AbsoluteUri);
            Assert.Null(rejectReason);
        }

        [Fact]
        public void RefusesInTunnelWebSocketUpgradeWithoutTheHandshakeKey()
        {
            // Inside a terminated tunnel the same RFC 6455 handshake gate runs: a crafted upgrade
            // with no key is malformed (400), never a half-tunnelled connection.
            var head =
                "GET /ws/live HTTP/1.1\r\n" +
                "Host: cam1.test:8443\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(
                new[] { "https://cam1.test:8443" }, head, new Uri("https://cam1.test:8443"), out _, out var rejectReason);

            Assert.Equal(ProxyForwardDisposition.BadRequest, decision);
            Assert.Contains("Sec-WebSocket", rejectReason);
        }

        [Fact]
        public void UpgradesToOtherProtocolsStayOrdinaryForwardedRequests()
        {
            // Only the websocket protocol tunnels. A non-websocket upgrade header (h2c chasing)
            // rides the normal forward path; the fetcher strips hop-by-hop upgrade headers anyway.
            var head =
                "GET http://10.0.0.5:8080/ HTTP/1.1\r\n" +
                "Connection: Upgrade, HTTP2-Settings\r\n" +
                "Upgrade: h2c\r\n" +
                "HTTP2-Settings: AABAAABIAAAACw\r\n" +
                "\r\n";

            var decision = ProxyForwardPolicy.Decide(Allowlist, head, out var request, out _);

            Assert.Equal(ProxyForwardDisposition.Forward, decision);
            Assert.Equal("http://10.0.0.5:8080/", request.Uri.AbsoluteUri);
        }

        // #73: the client gate consumes the same shared matcher as the Event Server gate -
        // wildcard entries carry the same apex/subdomain, port-exact, and VMS-floor semantics
        // through every posture (forward, CONNECT-terminate, ws-tunnel twin, wss upgrade).

        private static readonly string[] WildcardAllowlist =
        {
            "https://*.portal.test",
            "http://*.cams.local:8080",
            "https://*",
        };

        [Theory]
        [InlineData("https://a.portal.test/dashboard", "Forward")]
        [InlineData("https://a.b.portal.test/", "Forward")]
        [InlineData("https://portal.test/", "Forward")] // apex misses *.portal.test but the https://* hatch covers it
        [InlineData("http://cam1.cams.local:8080/live", "Forward")]
        [InlineData("http://cam1.cams.local:9090/live", "OriginNotAllowed")]
        [InlineData("https://anything.example.test/", "Forward")]
        [InlineData("https://vms.joshooaj.com/", "OriginNotAllowed")]
        [InlineData("https://10.20.30.40/", "OriginNotAllowed")]
        [InlineData("https://localhost:9443/", "OriginNotAllowed")]
        public void ClientGateWildcardPosturesMatchTheSharedMatcher(string url, string expected)
        {
            var head = $"GET {url} HTTP/1.1\r\nHost: host\r\n\r\n";

            var decision = ProxyForwardPolicy.Decide(
                WildcardAllowlist, head, null, new[] { "vms.joshooaj.com" }, out _, out _);

            Assert.Equal(expected, decision.ToString());
        }
    }
}
