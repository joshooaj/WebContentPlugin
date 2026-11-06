using System;
using System.Collections.Generic;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #57: the tunnel message contract. A websocket tunnel crosses the client↔Event
    /// Server channel as its OWN directional message pair — never the #53 Response/Chunk
    /// request/response framing (no double-encoding of already-framed websocket bytes). Payloads
    /// still travel as single JSON strings (#54 rule).
    /// </summary>
    public class ProxyTunnelContractTests
    {
        [Fact]
        public void TunnelMessagesFormDirectionalPairsAndNoneEndWithTheIgnoredSuffix()
        {
            // The platform's default receiver security ignores ids ending in "Command"; and each
            // direction gets its own id so a client's frames can never be swallowed by another
            // client's filter the way a shared id would.
            var ids = new[]
            {
                ProxyMessageIds.TunnelOpenRequestId,
                ProxyMessageIds.TunnelOpenResponseId,
                ProxyMessageIds.TunnelDataToTargetId,
                ProxyMessageIds.TunnelDataFromTargetId,
                ProxyMessageIds.TunnelCloseToTargetId,
                ProxyMessageIds.TunnelCloseFromTargetId,
            };

            foreach (var id in ids)
            {
                Assert.StartsWith("WebContent.Proxy.", id, StringComparison.Ordinal);
                Assert.False(id.EndsWith("Command", StringComparison.Ordinal), id);
            }

            Assert.Equal(ids.Length, new HashSet<string>(ids).Count);
        }

        [Fact]
        public void TunnelOpenRequestRoundTripsUrlAndHandshakeHeadersAsOneString()
        {
            var wire = ProxyFetchWireCodec.ToWire(new ProxyTunnelPayload
            {
                CorrelationId = "tun-1",
                Url = "http://10.0.0.5:8080/ws/live",
                Headers = new List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Upgrade", Value = "websocket" },
                    new ProxyFetchHeader { Name = "Connection", Value = "keep-alive, Upgrade" },
                    new ProxyFetchHeader { Name = "Sec-WebSocket-Key", Value = "dGhlIHNhbXBsZSBub25jZQ==" },
                    new ProxyFetchHeader { Name = "Cookie", Value = "sid=abc" },
                },
            });

            Assert.IsType<string>(wire);

            var back = ProxyFetchWireCodec.TunnelFromWire(wire);
            Assert.NotNull(back);
            Assert.Equal("tun-1", back.CorrelationId);
            Assert.Equal("http://10.0.0.5:8080/ws/live", back.Url);
            Assert.Equal("dGhlIHNhbXBsZSBub25jZQ==", back.Headers[2].Value);
            Assert.Equal("sid=abc", back.Headers[3].Value);
        }

        [Fact]
        public void TunnelDataFrameRoundTripsRawBytesByteIdentical()
        {
            // Websocket frame bytes are binary (opcodes, masks, payloads): the tunnel frame must
            // carry them byte-identical, base64-encoded at the JSON edge only.
            var raw = new byte[4093];
            new Random(11).NextBytes(raw);
            raw[0] = 0x82; // server->client binary frame header

            var back = ProxyFetchWireCodec.TunnelFromWire(ProxyFetchWireCodec.ToWire(new ProxyTunnelPayload
            {
                CorrelationId = "tun-2",
                DataBase64 = Convert.ToBase64String(raw),
            }));

            Assert.NotNull(back);
            Assert.Equal(raw, Convert.FromBase64String(back.DataBase64));
        }

        [Fact]
        public void TunnelFailureAndCloseFieldsRoundTrip()
        {
            var back = ProxyFetchWireCodec.TunnelFromWire(ProxyFetchWireCodec.ToWire(new ProxyTunnelPayload
            {
                CorrelationId = "tun-3",
                Success = false,
                StatusCode = 401,
                ErrorCode = ProxyFetchErrorCodes.HandshakeRefused,
                ErrorMessage = "the target answered 401 instead of 101",
            }));

            Assert.NotNull(back);
            Assert.False(back.Success);
            Assert.Equal(401, back.StatusCode);
            Assert.Equal(ProxyFetchErrorCodes.HandshakeRefused, back.ErrorCode);
            Assert.Contains("401", back.ErrorMessage);
        }

        [Fact]
        public void ForeignAndGarbageTunnelWireContentDecodeToNull()
        {
            Assert.Null(ProxyFetchWireCodec.TunnelFromWire(null));
            Assert.Null(ProxyFetchWireCodec.TunnelFromWire(42));
            Assert.Null(ProxyFetchWireCodec.TunnelFromWire("not json at all"));
            Assert.Null(ProxyFetchWireCodec.TunnelFromWire("{\"Bogus\":1}"));
        }
    }
}
