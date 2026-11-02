using System;

namespace WebContent
{
    /// <summary>
    /// Ticket #69: the seam the loopback forwarding flow calls where it refuses a browser
    /// request. Optional on <see cref="ProxyForwardConnection"/>: unreported refusals (no
    /// reporter wired, tests) keep working exactly as before.
    /// </summary>
    internal interface IProxyBlockReporter
    {
        /// <summary>File one refusal of <paramref name="origin"/> under a ProxyBlockReasons category.</summary>
        void Report(Uri origin, string reason);
    }

    /// <summary>
    /// Ticket #69: the per-listener block reporter. Bound to one proxied view's session context,
    /// it turns each client-side refusal into a fire-and-forget block report through the
    /// supplied send seam (production: MIP messaging via ProxyFetchClient). Sending must never
    /// throw into the browser flow — every fault is swallowed here; the diagnostics channel
    /// dying says nothing about the proxy traffic and must not disturb it.
    /// </summary>
    internal sealed class ProxyBlockReporter : IProxyBlockReporter
    {
        private readonly ProxyViewSessionContext _context;
        private readonly Action<ProxyBlockReportPayload> _send;
        private readonly Action<string> _log;

        internal ProxyBlockReporter(ProxyViewSessionContext context, Action<ProxyBlockReportPayload> send, Action<string> log)
        {
            _context = context;
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _log = log ?? (message => { });
        }

        public void Report(Uri origin, string reason)
        {
            try
            {
                if (_context == null || string.IsNullOrEmpty(reason) || origin == null)
                {
                    return;
                }

                if (!_context.HasWorkspaceIdentity)
                {
                    // A view item that knows no workspace item has nowhere to file the block;
                    // the Event Server would drop it as an unknown workspace anyway.
                    return;
                }

                _send(new ProxyBlockReportPayload
                {
                    WorkspaceItemId = _context.WorkspaceItemId,
                    WorkspaceFingerprint = _context.WorkspaceFingerprint,
                    Origin = ProxyPolicy.CanonicalOrigin(origin),
                    Reason = reason,
                    Count = 1,
                    ClientMachine = _context.ClientMachine,
                });
            }
            catch (Exception exception)
            {
                _log($"Block report for '{reason}' not sent: {exception.Message}");
            }
        }
    }
}