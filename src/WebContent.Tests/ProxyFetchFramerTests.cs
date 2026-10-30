using System;
using System.Collections.Generic;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #53: the Event Server fetcher answers a proxy-fetch request by framing the HTTP
    /// outcome as a stream of plugin-namespaced message payloads: one metadata frame carrying the
    /// status line and response headers, then base64 body chunks. The framer decides chunk
    /// boundaries, sequence order, and the chunk-count the requester needs to know it is done.
    /// </summary>
    public class ProxyFetchFramerTests
    {
        [Fact]
        public void SuccessFramesStartWithMetadataAndCoverTheWholeBodyInChunks()
        {
            var body = TestBody(1000);
            var result = new ProxyFetchResult
            {
                Success = true,
                StatusCode = 200,
                StatusText = "OK",
                Headers = new List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Content-Type", Value = "text/html" }
                },
                Body = body,
                BodyLength = 1000,
            };

            var frames = ProxyFetchFramer.Frame("corr-1", result, chunkSizeBytes: 256);

            // 1 metadata frame + 4 chunk frames (1000 bytes at 256-byte chunks).
            Assert.Equal(5, frames.Count);

            var meta = frames[0];
            Assert.Equal("corr-1", meta.CorrelationId);
            Assert.Equal(ProxyFetchFrameKinds.Response, meta.Kind);
            Assert.Equal(200, meta.StatusCode);
            Assert.Equal("OK", meta.StatusText);
            Assert.Equal(1000, meta.BodyLength);
            Assert.Equal(4, meta.ChunkCount);
            Assert.Single(meta.Headers);
            Assert.Equal("Content-Type", meta.Headers[0].Name);

            // Chunk frames decode back onto the body, in order, last one flagged.
            var rebuilt = new byte[1000];
            for (var index = 0; index < 4; index++)
            {
                var chunk = frames[index + 1];
                Assert.Equal(ProxyFetchFrameKinds.Chunk, chunk.Kind);
                Assert.Equal(index, chunk.Sequence);
                Assert.Equal(index == 3, chunk.IsLastChunk);
                var bytes = Convert.FromBase64String(chunk.ContentBase64);
                Buffer.BlockCopy(bytes, 0, rebuilt, index * 256, bytes.Length);
            }

            Assert.Equal(body, rebuilt);
        }

        [Fact]
        public void FailureOutcomeFramesAsOneTerminalFrameWithNoChunks()
        {
            var result = new ProxyFetchResult
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.Unreachable,
                ErrorMessage = "No connection refused at 10.0.0.5:8081",
            };

            var frames = ProxyFetchFramer.Frame("corr-2", result, chunkSizeBytes: 64);

            var failure = Assert.Single(frames);
            Assert.Equal(ProxyFetchFrameKinds.Failure, failure.Kind);
            Assert.Equal("corr-2", failure.CorrelationId);
            Assert.Equal(ProxyFetchErrorCodes.Unreachable, failure.ErrorCode);
            Assert.Equal("No connection refused at 10.0.0.5:8081", failure.ErrorMessage);
            Assert.Null(failure.ContentBase64);
            Assert.Equal(0, failure.ChunkCount);
        }

        private static byte[] TestBody(int length)
        {
            var body = new byte[length];
            for (var i = 0; i < length; i++)
            {
                body[i] = (byte)(i % 251);
            }

            return body;
        }
    }
}
