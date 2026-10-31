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
