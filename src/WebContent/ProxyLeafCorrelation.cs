using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Requester-side protocol state for the leaf-mint exchange (#56, ADR-0003). Unlike the fetch
    /// stream this exchange is exactly one response frame, so the correlator is a pending-task
    /// table with deadlines: Feed completes the waiting task with the first frame that carries a
    /// known correlation id, and an unanswered exchange completes as a Timeout failure rather
    /// than staying pending forever.
    ///
    /// Thread-safe: Feed runs on the message receiver thread, Begin on the listener's thread.
    /// </summary>
    internal sealed class ProxyLeafCorrelation
    {
        private sealed class Pending
        {
            internal string CorrelationId;
            internal CancellationTokenSource Deadline;
            internal TaskCompletionSource<ProxyLeafResponsePayload> Completion;
            internal bool Completed;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);

        /// <summary>Register a pending leaf request; the task completes on the response frame or
        /// the deadline.</summary>
        internal Task<ProxyLeafResponsePayload> Begin(string correlationId, TimeSpan timeout)
        {
            lock (_sync)
            {
                var pending = new Pending
                {
                    CorrelationId = correlationId,
                    Deadline = new CancellationTokenSource(timeout),
                    Completion = new TaskCompletionSource<ProxyLeafResponsePayload>(),
                };

                pending.Deadline.Token.Register(() =>
                    Complete(pending, new ProxyLeafResponsePayload
                    {
                        CorrelationId = correlationId,
                        Success = false,
                        ErrorCode = ProxyFetchErrorCodes.Timeout,
                        ErrorMessage = "No leaf-mint response within the response deadline.",
                    }));

                _pending[correlationId] = pending;
                return pending.Completion.Task;
            }
        }

        /// <summary>Route one response frame; true when a pending exchange owned it.</summary>
        internal bool Feed(ProxyLeafResponsePayload response)
        {
            if (response == null || string.IsNullOrEmpty(response.CorrelationId))
            {
                return false;
            }

            lock (_sync)
            {
                if (!_pending.TryGetValue(response.CorrelationId, out var pending) || pending.Completed)
                {
                    return false;
                }

                Complete(pending, response);
                return true;
            }
        }

        /// <summary>Drop a pending request locally (the listener lost interest).</summary>
        internal void Abandon(string correlationId)
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(correlationId, out var pending))
                {
                    Complete(pending, new ProxyLeafResponsePayload
                    {
                        CorrelationId = correlationId,
                        Success = false,
                        ErrorCode = ProxyFetchErrorCodes.Error,
                        ErrorMessage = "The leaf-mint request was abandoned locally.",
                    });
                }
            }
        }

        private void Complete(Pending pending, ProxyLeafResponsePayload response)
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
                pending.Completion.TrySetResult(response);
            }
            finally
            {
                pending.Deadline.Cancel();
                pending.Deadline.Dispose();
            }
        }
    }
}