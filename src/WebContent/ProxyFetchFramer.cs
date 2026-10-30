using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// Pure seam between the fetcher's outcome and the message stream: turns a ProxyFetchResult
    /// into the ordered frames the Event Server transmits back to the requester — one metadata
    /// frame (status line + response headers + body length + chunk count), then base64 body
    /// chunks. Ticket #53 acceptance: the body comes back chunked with status code and content
    /// headers.
    /// </summary>
    internal static class ProxyFetchFramer
    {
        /// <summary>
        /// Frame a successful fetch outcome. Chunk frames carry a 0-based sequence and the final
        /// chunk is flagged, so a consumer can detect end of stream without waiting for the count.
        /// </summary>
        internal static List<ProxyFetchResponsePayload> Frame(string correlationId, ProxyFetchResult result, int chunkSizeBytes)
        {
            var frames = new List<ProxyFetchResponsePayload>();

            if (!result.Success)
            {
                frames.Add(new ProxyFetchResponsePayload
                {
                    CorrelationId = correlationId,
                    Kind = ProxyFetchFrameKinds.Failure,
                    Sequence = -1,
                    StatusCode = result.StatusCode,
                    ErrorCode = result.ErrorCode,
                    ErrorMessage = result.ErrorMessage,
                });

                return frames;
            }

            var body = result.Body ?? new byte[0];
            if (chunkSizeBytes <= 0)
            {
                chunkSizeBytes = ProxyFetchLimits.DefaultChunkSizeBytes;
            }

            var chunkCount = (int)((body.Length + chunkSizeBytes - 1) / chunkSizeBytes);

            frames.Add(new ProxyFetchResponsePayload
                {
                    CorrelationId = correlationId,
                    Kind = ProxyFetchFrameKinds.Response,
                    Sequence = -1,
                    StatusCode = result.StatusCode,
                    StatusText = result.StatusText,
                    Headers = result.Headers ?? new List<ProxyFetchHeader>(),
                    BodyLength = result.BodyLength,
                    ChunkCount = chunkCount,
                });

            for (var index = 0; index < chunkCount; index++)
            {
                var offset = index * chunkSizeBytes;
                var length = Math.Min(chunkSizeBytes, body.Length - offset);
                var chunk = new byte[length];
                Array.Copy(body, offset, chunk, 0, length);

                frames.Add(new ProxyFetchResponsePayload
                {
                    CorrelationId = correlationId,
                    Kind = ProxyFetchFrameKinds.Chunk,
                    Sequence = index,
                    IsLastChunk = index == chunkCount - 1,
                    ContentBase64 = Convert.ToBase64String(chunk),
                });
            }

            return frames;
        }
    }
}
