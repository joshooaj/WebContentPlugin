using System.Collections.Generic;
using System.Text;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #57: the pure websocket-handshake seam — recognize the browser's ws:// upgrade,
    /// rebuild the origin-form handshake the Event Server replays at the target (the browser's
    /// Sec-WebSocket-Key must ride byte-identical so the target's Sec-WebSocket-Accept matches
    /// what the browser verifies), and decide the target's handshake answer.
    /// </summary>
    public class ProxyWebSocketHandshakeTests
    {
        private static ProxyForwardedRequest Parse(string head)
        {
            ProxyForwardedRequest request;
            string reason;
            Assert.True(ProxyForwardCodec.TryParseRequestHead(head, out request, out reason), reason);
            return request;
        }

        private const string BrowserUpgradeHead =
            "GET http://10.0.0.5:8080/ws/live?stream=1 HTTP/1.1\r\n" +
            "Host: 10.0.0.5:8080\r\n" +
            "Connection: keep-alive, Upgrade\r\n" +
            "Pragma: no-cache\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Upgrade: websocket\r\n" +
            "Origin: http://10.0.0.5:8080\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Cookie: sid=abc\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "\r\n";

        [Fact]
        public void RecognizesTheBrowserWebsocketUpgradeShape()
        {
            var request = Parse(BrowserUpgradeHead);

            Assert.True(ProxyWebSocketHandshake.IsWebSocketUpgrade(request));
            Assert.True(ProxyWebSocketHandshake.HasValidClientHandshake(request, out _));
        }

        [Fact]
        public void DoesNotTreatOtherUpgradeProtocolsAsWebsockets()
        {
            var request = Parse(
                "GET http://10.0.0.5:8080/ HTTP/1.1\r\n" +
                "Connection: Upgrade\r\nUpgrade: h2c\r\n\r\n");

            Assert.False(ProxyWebSocketHandshake.IsWebSocketUpgrade(request));
        }

        [Fact]
        public void RebuildsTheOriginFormHandshakeForTheTargetConnection()
        {
            var request = Parse(BrowserUpgradeHead);
            var wire = ProxyWebSocketHandshake.ToTunnelWireHeaders(request);

            var bytes = ProxyWebSocketHandshake.BuildTargetHandshake(request.Uri, wire);
            var handshake = Encoding.UTF8.GetString(bytes);

            // Origin-form against the target path+query, Host derived from the uri.
            Assert.StartsWith("GET /ws/live?stream=1 HTTP/1.1\r\n", handshake);
            Assert.Contains("Host: 10.0.0.5:8080\r\n", handshake);

            // The upgrade pair is restated explicitly; the browser's Sec-WebSocket-Key and
            // session cookie ride untouched.
            Assert.Contains("Connection: Upgrade\r\n", handshake);
            Assert.Contains("Upgrade: websocket\r\n", handshake);
            Assert.Contains("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n", handshake);
            Assert.Contains("Cookie: sid=abc\r\n", handshake);

            // Per-hop proxy decoration never travels to the target, and the head terminates.
            Assert.DoesNotContain("Proxy-Connection", handshake);
            Assert.EndsWith("\r\n\r\n", handshake);
        }

        [Fact]
        public void RebuiltHandshakeCarriesExactlyOneConnectionUpgradePair()
        {
            // The browser spells its upgrade "Connection: keep-alive, Upgrade"; the rebuilt
            // handshake restates the pair canonically, so relaying the browser's copy too would
            // send two Connection lines (and a duplicated Upgrade) and a target may reject the
            // malformed upgrade. Exactly one of each must reach the wire.
            var request = Parse(BrowserUpgradeHead);
            var wire = ProxyWebSocketHandshake.ToTunnelWireHeaders(request);

            var handshake = Encoding.UTF8.GetString(ProxyWebSocketHandshake.BuildTargetHandshake(request.Uri, wire));

            Assert.DoesNotContain("keep-alive, Upgrade", handshake);
            Assert.Equal(2, handshake.Split(new[] { "\r\nConnection: " }, System.StringSplitOptions.None).Length);
            Assert.Equal(2, handshake.Split(new[] { "\r\nUpgrade: " }, System.StringSplitOptions.None).Length);
        }

        [Fact]
        public void DropsTheClientsHostHeaderSoTheTargetSeesTheDerivedOne()
        {
            var request = Parse(
                "GET http://10.0.0.5:8080/ws HTTP/1.1\r\n" +
                "Host: spoofed.internal\r\n" +
                "Connection: Upgrade\r\nUpgrade: websocket\r\n\r\n");
            var wire = ProxyWebSocketHandshake.ToTunnelWireHeaders(request);

            var handshake = Encoding.UTF8.GetString(ProxyWebSocketHandshake.BuildTargetHandshake(request.Uri, wire));

            // Exactly one Host line - the derived one - never the client-supplied one.
            Assert.DoesNotContain("Host: spoofed.internal", handshake);
            Assert.Equal(2, handshake.Split(new[] { "\r\nHost: " }, System.StringSplitOptions.None).Length);
            Assert.Contains("\r\nHost: 10.0.0.5:8080\r\n", handshake);
        }

        [Fact]
        public void RefusesWireSuppliedHeaderValuesThatWouldInjectExtraHeaders()
        {
            var target = new System.Uri("http://10.0.0.5:8080/ws");
            var wire = new List<ProxyFetchHeader>
            {
                new ProxyFetchHeader { Name = "Sec-WebSocket-Key", Value = "ok== key" },
                new ProxyFetchHeader { Name = "X-Evil", Value = "x\r\nInjected: yes" },
            };

            var handshake = Encoding.UTF8.GetString(ProxyWebSocketHandshake.BuildTargetHandshake(target, wire));

            Assert.Contains("Sec-WebSocket-Key: ok== key\r\n", handshake);
            Assert.DoesNotContain("Injected", handshake);
        }

        [Fact]
        public void TargetHandshakeEndIsFoundAtTheHeadTerminatorWithBufferedFrameBytesAfterIt()
        {
            var bytes = Encoding.UTF8.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: xyz==\r\n\r\n\x81\x05hello");

            int headLength;
            Assert.True(ProxyWebSocketHandshake.TryFindHandshakeEnd(bytes, bytes.Length, out headLength));
            Assert.True(ProxyWebSocketHandshake.IsUpgradeAccepted(bytes, headLength));

            // The head cut leaves the already-buffered frame bytes for the pump, not the head.
            Assert.True(headLength < bytes.Length);
            Assert.Equal('\r', (char)bytes[headLength - 2]);
            Assert.Equal('\n', (char)bytes[headLength - 1]);
        }

        [Fact]
        public void HandshakeHeadThatNeverTerminatesIsIncomplete()
        {
            var bytes = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n");

            int headLength;
            Assert.False(ProxyWebSocketHandshake.TryFindHandshakeEnd(bytes, bytes.Length, out headLength));
        }

        [Theory]
        [InlineData("HTTP/1.1 101 Switching Protocols\r\n\r\n", true)]
        [InlineData("HTTP/1.0 101 Switching Protocols\r\n\r\n", true)]
        [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", false)]
        [InlineData("HTTP/1.1 401 Unauthorized\r\n\r\n", false)]
        [InlineData("HTPX/1.1 101 Switching Protocols\r\n\r\n", false)]
        public void OnlyTheUpgradeStatusAdmitsTheTunnel(string head, bool expected)
        {
            var bytes = Encoding.UTF8.GetBytes(head);
            int headLength;
            Assert.True(ProxyWebSocketHandshake.TryFindHandshakeEnd(bytes, bytes.Length, out headLength));
            Assert.Equal(expected, ProxyWebSocketHandshake.IsUpgradeAccepted(bytes, headLength));
        }
    }
}
