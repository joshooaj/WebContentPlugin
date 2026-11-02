using System;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// #54 live-host fix: proxy-fetch payloads travel as JSON strings, never as plugin-namespace
    /// objects. The platform binds custom Data types with Type.GetType(DataClass) and hands the
    /// receiver a null Data on hosts whose type resolution cannot see WebContent.dll (Smart
    /// Client's black hole). The codec keeps every wire payload a plain string.
    /// </summary>
    public class ProxyFetchWireCodecTests
    {
        [Fact]
        public void RequestRoundTripsThroughAString()
        {
            var wire = ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload
            {
                CorrelationId = "corr-1",
                Url = "http://httpforever.com/",
            });

            Assert.IsType<string>(wire);
            Assert.Contains("corr-1", wire);

            var back = ProxyFetchWireCodec.RequestFromWire(wire);
            Assert.NotNull(back);
            Assert.Equal("corr-1", back.CorrelationId);
            Assert.Equal("http://httpforever.com/", back.Url);
        }

        [Fact]
        public void RequestCarriesMethodHeadersAndBase64BodyOnTheWire()
        {
            // #55: login POSTs cross the wire with method, browser request headers, and body —
            // still as ONE JSON string, never as plugin types (the #54 Smart Client black hole).
            var body = System.Text.Encoding.UTF8.GetBytes("user=admin&pass=s3cret&submit=Login");
            var wire = ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload
            {
                CorrelationId = "corr-post",
                Url = "http://10.0.0.5:8080/login",
                Method = "POST",
                Headers = new System.Collections.Generic.List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Content-Type", Value = "application/x-www-form-urlencoded" },
                    new ProxyFetchHeader { Name = "Cookie", Value = "sid=abc123" },
                    new ProxyFetchHeader { Name = "X-Vendor-Auth", Value = "token-9" },
                },
                BodyBase64 = Convert.ToBase64String(body),
            });

            Assert.IsType<string>(wire);

            var back = ProxyFetchWireCodec.RequestFromWire(wire);
            Assert.NotNull(back);
            Assert.Equal("corr-post", back.CorrelationId);
            Assert.Equal("POST", back.Method);
            Assert.Equal("Cookie", back.Headers[1].Name);
            Assert.Equal("sid=abc123", back.Headers[1].Value);
            Assert.Equal("X-Vendor-Auth", back.Headers[2].Name);
            Assert.Equal(body, Convert.FromBase64String(back.BodyBase64));
        }

        [Fact]
        public void RequestCarriesTheWorkspaceItemIdAndOldShapesStillDecode()
        {
            // #65: the workspace attribution that rule-trigger rows are evaluated against rides
            // the existing request wire; a sender from before #65 (no member in the JSON at all)
            // decodes to null and the request is still a fully valid fetch.
            var wire = ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload
            {
                CorrelationId = "corr-ws",
                Url = "https://camera.local/snapshot",
                SessionId = "sess-1",
                WorkspaceItemId = "e8955f17-d107-424f-a81f-e076b4f332e9",
            });

            var back = ProxyFetchWireCodec.RequestFromWire(wire);
            Assert.NotNull(back);
            Assert.Equal("sess-1", back.SessionId);
            Assert.Equal("e8955f17-d107-424f-a81f-e076b4f332e9", back.WorkspaceItemId);

            var preTriggerSender = "{\"CorrelationId\":\"corr-old\",\"Url\":\"https://camera.local/x\",\"SessionId\":\"sess-old\"}";
            var oldBack = ProxyFetchWireCodec.RequestFromWire(preTriggerSender);
            Assert.NotNull(oldBack);
            Assert.Equal("sess-old", oldBack.SessionId);
            Assert.Null(oldBack.WorkspaceItemId);
        }

        [Fact]
        public void PlainGetRequestsStillDecodeWithNullMethodHeadersAndBody()
        {
            // Field additions must not disturb the GET path: absent fields decode to null, and
            // the fetcher treats a null method as GET.
            var wire = ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload
            {
                CorrelationId = "corr-get",
                Url = "http://httpforever.com/",
            });

            var back = ProxyFetchWireCodec.RequestFromWire(wire);
            Assert.NotNull(back);
            Assert.Null(back.Method);
            Assert.Null(back.Headers);
            Assert.Null(back.BodyBase64);
        }

        [Fact]
        public void ResponseFrameRoundTripsEveryFieldTheProtocolUses()
        {
            var frame = new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-2",
                Kind = ProxyFetchFrameKinds.Response,
                Sequence = -1,
                StatusCode = 200,
                StatusText = "OK",
                Headers = new System.Collections.Generic.List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Content-Type", Value = "text/html" },
                },
                BodyLength = 11513,
                ChunkCount = 1,
            };

            var back = ProxyFetchWireCodec.FrameFromWire(ProxyFetchWireCodec.ToWire(frame));
            Assert.NotNull(back);
            Assert.Equal("corr-2", back.CorrelationId);
            Assert.Equal(ProxyFetchFrameKinds.Response, back.Kind);
            Assert.Equal(200, back.StatusCode);
            Assert.Equal(11513, back.BodyLength);
            Assert.Equal(1, back.ChunkCount);
            Assert.Single(back.Headers);
            Assert.Equal("Content-Type", back.Headers[0].Name);
        }

        [Fact]
        public void ChunkFramesRoundTripBase64Bodies()
        {
            var body = new byte[9000];
            new Random(7).NextBytes(body);
            var frame = new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-3",
                Kind = ProxyFetchFrameKinds.Chunk,
                Sequence = 0,
                IsLastChunk = true,
                ContentBase64 = Convert.ToBase64String(body),
            };

            var back = ProxyFetchWireCodec.FrameFromWire(ProxyFetchWireCodec.ToWire(frame));
            Assert.NotNull(back);
            Assert.Equal(Convert.ToBase64String(body), back.ContentBase64);
            Assert.True(back.IsLastChunk);
        }

        [Fact]
        public void NonStringForeignAndGarbageWireContentDecodeToNull()
        {
            // Pre-codec senders (typed payload objects) and hostile content both decode to the
            // "unusable frame" signal instead of throwing on the message-receiver thread.
            Assert.Null(ProxyFetchWireCodec.RequestFromWire(null));
            Assert.Null(ProxyFetchWireCodec.RequestFromWire(42));
            Assert.Null(ProxyFetchWireCodec.RequestFromWire("{not json"));
            Assert.Null(ProxyFetchWireCodec.RequestFromWire("{\"Bogus\":1}"));

            Assert.Null(ProxyFetchWireCodec.FrameFromWire(null));
            Assert.Null(ProxyFetchWireCodec.FrameFromWire(new ProxyFetchResponsePayload { CorrelationId = "typed-legacy" }));
            Assert.Null(ProxyFetchWireCodec.FrameFromWire("garbage"));
        }
    }
}
