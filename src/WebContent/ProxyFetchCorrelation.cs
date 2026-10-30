using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Requester-side protocol state for the proxy-fetch response stream (#53). Response frames
    /// arrive over the shared MIP message channel; only the correlation id tells the requester
    /// which request they belong to. The correlator accumulates pending exchanges and completes
    /// one exactly when the protocol says the stream ended — every counted chunk arrived, or a
    /// single Failure frame arrived — and enforces the requester's response deadline so stalled
    /// exchanges are bounded.
    ///
    /// Thread-safe: Feed is called from the message receiver thread, Begin from the plugin thread.
    /// </summary>
    internal sealed class ProxyFetchCorrelation
    {
        internal sealed class Pending
        {
            internal string CorrelationId;
            internal CancellationTokenSource Deadline;
            internal TaskCompletionSource<ProxyFetchResult> Completion;
            internal readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
            internal ProxyFetchResponsePayload Meta;
            internal bool Completed;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);

        /// <summary>
        /// Register a pending proxy-fetch request and return the task its reassembled outcome
        /// completes on. If no terminal frame arrives before the deadline, the task completes with
        /// a Timeout error result rather than staying pending forever.
        /// </summary>
        internal Task<ProxyFetchResult> Begin(string correlationId, TimeSpan timeout)
        {
            lock (_sync)
            {
                var pending = new Pending
                {
                    CorrelationId = correlationId,
                    Deadline = new CancellationTokenSource(timeout),
                    Completion = new TaskCompletionSource<ProxyFetchResult>(),
                };

                var id = correlationId;
                pending.Deadline.Token.Register(() =>
                {
                    Complete(pending, new ProxyFetchResult
                    {
                        Success = false,
                        ErrorCode = ProxyFetchErrorCodes.Timeout,
                        ErrorMessage = $"No proxy-fetch response for {id} within the response deadline.",
                    });
                });

                _pending[correlationId] = pending;
                return pending.Completion.Task;
            }
        }

        /// <summary>
        /// Route one response frame. Frames for correlation ids no longer pending (timed out or
        /// already completed) or with malformed content are ignored, not thrown.
        /// </summary>
        internal void Feed(ProxyFetchResponsePayload frame)
        {
            if (frame == null || string.IsNullOrEmpty(frame.CorrelationId))
            {
                return;
            }

            lock (_sync)
            {
                if (!_pending.TryGetValue(frame.CorrelationId, out var pending) || pending.Completed)
                {
                    return;
                }

                if (frame.Kind == ProxyFetchFrameKinds.Failure)
                {
                    Complete(pending, new ProxyFetchResult
                    {
                        Success = false,
                        StatusCode = frame.StatusCode,
                        ErrorCode = string.IsNullOrEmpty(frame.ErrorCode) ? ProxyFetchErrorCodes.Error : frame.ErrorCode,
                        ErrorMessage = frame.ErrorMessage,
                    });
                    return;
                }

                if (frame.Kind == ProxyFetchFrameKinds.Response)
                {
                    pending.Meta = frame;
                }
                else if (frame.Kind == ProxyFetchFrameKinds.Chunk)
                {
                    try
                    {
                        pending.Chunks[frame.Sequence] = string.IsNullOrEmpty(frame.ContentBase64)
                            ? new byte[0]
                            : Convert.FromBase64String(frame.ContentBase64);
                    }
                    catch (FormatException)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }

                TryFinishByCount(pending);
            }
        }

        /// <summary>
        /// Drop a pending request (the requester lost interest), completing its task cancelled so
        /// nobody waits forever on it.
        /// </summary>
        internal void Abandon(string correlationId)
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(correlationId, out var pending))
                {
                    Complete(pending, new ProxyFetchResult
                    {
                        Success = false,
                        ErrorCode = ProxyFetchErrorCodes.Error,
                        ErrorMessage = "The proxy-fetch request was abandoned locally.",
                    });
                }
            }
        }

        private void TryFinishByCount(Pending pending)
        {
            if (pending.Meta == null)
            {
                return;
            }

            // A meta frame with a negative or oversize declaration is hostile/garbled input;
            // allocating from it could throw on the message-receiver thread. The server-side
            // fetcher never declares more than its own response cap.
            if (pending.Meta.ChunkCount < 0 ||
                pending.Meta.BodyLength < 0 ||
                pending.Meta.BodyLength > ProxyFetchLimits.Default().MaxResponseBytes)
            {
                Complete(pending, new ProxyFetchResult
                {
                    Success = false,
                    StatusCode = pending.Meta.StatusCode,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = "The proxy-fetch metadata frame declared an unusable body size.",
                });
                return;
            }

            if (pending.Chunks.Count < pending.Meta.ChunkCount)
            {
                return;
            }

            var body = new byte[pending.Meta.BodyLength];
            var offset = 0;
            for (var sequence = 0; sequence < pending.Meta.ChunkCount; sequence++)
            {
                if (!pending.Chunks.TryGetValue(sequence, out var chunk))
                {
                    return;
                }

                // Garbled or hostile frames must never throw on the message-receiver thread:
                // chunk bytes that overrun the declared body length complete the exchange with
                // an error outcome instead.
                if (offset + chunk.Length > body.Length)
                {
                    Complete(pending, new ProxyFetchResult
                    {
                        Success = false,
                        StatusCode = pending.Meta.StatusCode,
                        ErrorCode = ProxyFetchErrorCodes.Error,
                        ErrorMessage = "The proxy-fetch chunk stream overran the declared body length.",
                    });
                    return;
                }

                Buffer.BlockCopy(chunk, 0, body, offset, chunk.Length);
                offset += chunk.Length;
            }

            if (offset < body.Length)
            {
                Complete(pending, new ProxyFetchResult
                {
                    Success = false,
                    StatusCode = pending.Meta.StatusCode,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = "The proxy-fetch chunk stream carried less data than declared.",
                });
                return;
            }

            Complete(pending, new ProxyFetchResult
            {
                Success = true,
                StatusCode = pending.Meta.StatusCode,
                StatusText = pending.Meta.StatusText,
                Headers = pending.Meta.Headers ?? new List<ProxyFetchHeader>(),
                BodyLength = pending.Meta.BodyLength,
                Body = body,
            });
        }

        private void Complete(Pending pending, ProxyFetchResult result)
        {
            lock (_sync)
            {
                if (pending.Completed)
                {
                    return;
                }

                pending.Completed = true;
                _pending.Remove(pending.CorrelationId);
            }

            try
            {
                pending.Completion.TrySetResult(result);
            }
            finally
            {
                pending.Deadline.Cancel();
                pending.Deadline.Dispose();
            }
        }
    }
}
