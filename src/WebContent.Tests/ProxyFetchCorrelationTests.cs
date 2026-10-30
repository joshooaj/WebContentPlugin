using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #53: the requester side of the proxy-fetch exchange. Response frames arrive over a
    /// shared message channel tagged only by correlation id; the correlator tracks pending
    /// requests and decides when the stream is complete (all counted chunks arrived, or a Failure
    /// frame arrived) — never before, never after foreign ids.
    /// </summary>
    public class ProxyFetchCorrelationTests
    {
        [Fact]
        public async Task FramedSuccessStreamCompletesWithTheSameResultItStartedAs()
        {
            var body = TestBody(1000);
            var original = new ProxyFetchResult
            {
                Success = true,
                StatusCode = 200,
                StatusText = "OK",
                Headers = new List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Content-Type", Value = "text/html" }
                },
                Body = body,
                BodyLength = body.Length,
            };

            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-1", TimeSpan.FromSeconds(30));

            foreach (var frame in ProxyFetchFramer.Frame("corr-1", original, chunkSizeBytes: 256))
            {
                correlation.Feed(frame);
            }

            var completed = await completion;
            Assert.True(completed.Success);
            Assert.Equal(200, completed.StatusCode);
            Assert.Equal("OK", completed.StatusText);
            Assert.Single(completed.Headers);
            Assert.Equal("Content-Type", completed.Headers[0].Name);
            Assert.Equal(body, completed.Body);
        }

        [Fact]
        public async Task FailureFrameCompletesOnItsOwnWithoutAnyChunks()
        {
            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-2", TimeSpan.FromSeconds(30));

            correlation.Feed(new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-2",
                Kind = ProxyFetchFrameKinds.Failure,
                Sequence = -1,
                ErrorCode = ProxyFetchErrorCodes.Unreachable,
                ErrorMessage = "connection refused",
            });

            var completed = await completion;
            Assert.False(completed.Success);
            Assert.Equal(ProxyFetchErrorCodes.Unreachable, completed.ErrorCode);
            Assert.Equal("connection refused", completed.ErrorMessage);
        }

        [Fact]
        public async Task EmptyBodyCompletesOnMetadataFrameAlone()
        {
            var result = new ProxyFetchResult
            {
                Success = true,
                StatusCode = 204,
                Body = new byte[0],
                BodyLength = 0,
            };

            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-empty", TimeSpan.FromSeconds(30));

            correlation.Feed(ProxyFetchFramer.Frame("corr-empty", result, chunkSizeBytes: 256)[0]);

            var completed = await completion;
            Assert.True(completed.Success);
            Assert.Equal(204, completed.StatusCode);
            Assert.Equal(new byte[0], completed.Body);
        }

        [Fact]
        public async Task PartialStreamNeverCompletesAndKeepsTheRequestPending()
        {
            var body = TestBody(600);
            var result = new ProxyFetchResult { Success = true, StatusCode = 200, Body = body, BodyLength = body.Length };

            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-3", TimeSpan.FromMinutes(5));

            var frames = ProxyFetchFramer.Frame("corr-3", result, chunkSizeBytes: 256);
            correlation.Feed(frames[0]);
            correlation.Feed(frames[1]);

            Assert.False(completion.IsCompleted);

            correlation.Feed(frames[2]);
            correlation.Feed(frames[3]);

            var assembled = await completion;
            Assert.Equal(body, assembled.Body);
        }

        [Fact]
        public void FramesForForeignCorrelationIdsAreIgnoredAndStayIncomplete()
        {
            var result = new ProxyFetchResult { Success = true, StatusCode = 200, Body = TestBody(10), BodyLength = 10 };

            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-mine", TimeSpan.FromMinutes(5));

            foreach (var frame in ProxyFetchFramer.Frame("corr-other", result, chunkSizeBytes: 256))
            {
                correlation.Feed(frame);
            }

            Assert.False(completion.IsCompleted);
        }

        [Fact]
        public async Task ExpiredDeadlineCompletesTheTaskWithALocalTimeoutResult()
        {
            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-4", TimeSpan.FromMilliseconds(50));

            await Task.Delay(400);

            Assert.True(completion.IsCompleted);
            Assert.False(completion.Result.Success);
            Assert.Equal(ProxyFetchErrorCodes.Timeout, completion.Result.ErrorCode);
        }

        [Fact]
        public async Task ChunkBytesOverrunningTheDeclaredBodyCompleteWithErrorNotThrow()
        {
            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-over", TimeSpan.FromSeconds(30));

            // Meta declares 10 bytes; the chunk frame carries 20. The receiver thread must not
            // take an exception from BlockCopy — the exchange completes with an error outcome.
            correlation.Feed(new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-over",
                Kind = ProxyFetchFrameKinds.Response,
                Sequence = -1,
                StatusCode = 200,
                BodyLength = 10,
                ChunkCount = 1,
            });
            correlation.Feed(new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-over",
                Kind = ProxyFetchFrameKinds.Chunk,
                Sequence = 0,
                IsLastChunk = true,
                ContentBase64 = Convert.ToBase64String(TestBody(20)),
            });

            var completed = await completion;
            Assert.False(completed.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, completed.ErrorCode);
        }

        [Fact]
        public async Task ChunkBytesUnderDeclaringTheBodyCompleteWithErrorNotTruncatedSuccess()
        {
            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-under", TimeSpan.FromSeconds(30));

            correlation.Feed(new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-under",
                Kind = ProxyFetchFrameKinds.Response,
                Sequence = -1,
                StatusCode = 200,
                BodyLength = 100,
                ChunkCount = 1,
            });
            correlation.Feed(new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-under",
                Kind = ProxyFetchFrameKinds.Chunk,
                Sequence = 0,
                IsLastChunk = true,
                ContentBase64 = Convert.ToBase64String(TestBody(10)),
            });

            var completed = await completion;
            Assert.False(completed.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, completed.ErrorCode);
        }

        [Fact]
        public async Task UnusableBodyDeclarationInMetaFrameCompletesWithErrorWithoutThrowing()
        {
            var correlation = new ProxyFetchCorrelation();
            var completion = correlation.Begin("corr-hostile", TimeSpan.FromSeconds(30));

            // Negative and over-cap declarations would blow up the reassembly allocation; the
            // guard turns them into a normal error outcome.
            correlation.Feed(new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-hostile",
                Kind = ProxyFetchFrameKinds.Response,
                Sequence = -1,
                StatusCode = 200,
                BodyLength = -5,
                ChunkCount = 1,
            });

            var completed = await completion;
            Assert.False(completed.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, completed.ErrorCode);

            var second = correlation.Begin("corr-hostile-2", TimeSpan.FromSeconds(30));
            correlation.Feed(new ProxyFetchResponsePayload
            {
                CorrelationId = "corr-hostile-2",
                Kind = ProxyFetchFrameKinds.Response,
                Sequence = -1,
                StatusCode = 200,
                BodyLength = ProxyFetchLimits.DefaultMaxResponseBytes + 1L,
                ChunkCount = 0,
            });

            var completed2 = await second;
            Assert.False(completed2.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, completed2.ErrorCode);
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
