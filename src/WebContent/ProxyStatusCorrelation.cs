using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Ticket #69: requester-side protocol state for the workspace status query. Exactly one
    /// response frame per request (unicasted by the Event Server), so this is the leaf-mint
    /// pattern again: a pending-task table with deadlines. An unanswered query completes as a
    /// ServerUnavailable failure — silence from this id means the VMS-side plugin never answered,
    /// which the MC page renders distinctly from an empty snapshot.
    ///
    /// Thread-safe: Feed runs on the message receiver thread, Begin on the UI thread.
    /// </summary>
    internal sealed class ProxyStatusCorrelation
    {
        private sealed class Pending
        {
            internal string CorrelationId;
            internal CancellationTokenSource Deadline;
            internal TaskCompletionSource<ProxyStatusResponsePayload> Completion;
            internal bool Completed;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.Ordinal);

        internal Task<ProxyStatusResponsePayload> Begin(string correlationId, TimeSpan timeout)
        {
            lock (_sync)
            {
                var pending = new Pending
                {
                    CorrelationId = correlationId,
                    Deadline = new CancellationTokenSource(timeout),
                    Completion = new TaskCompletionSource<ProxyStatusResponsePayload>(),
                };

                pending.Deadline.Token.Register(() =>
                    Complete(pending, new ProxyStatusResponsePayload
                    {
                        CorrelationId = correlationId,
                        Success = false,
                        ErrorCode = ProxyFetchErrorCodes.ServerUnavailable,
                        ErrorMessage = "The VMS Event Server did not answer the proxy status query within the deadline (the plugin's diagnostics channel is absent or the messaging session is down).",
                    }));

                _pending[correlationId] = pending;
                return pending.Completion.Task;
            }
        }

        /// <summary>Route one response frame; true when a pending exchange owned it.</summary>
        internal bool Feed(ProxyStatusResponsePayload response)
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

        private void Complete(Pending pending, ProxyStatusResponsePayload response)
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
