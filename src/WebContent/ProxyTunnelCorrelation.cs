using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Ticket #57: the client-side registry of live websocket tunnels. The messaging receive
    /// thread feeds it (open acks, target data, target closes); the browser connection pumps
    /// drain it (ordered data, close sentinels). It owns no transport - ProxyFetchClient turns
    /// these events into MIP messages - so the whole lifetime model (half-open deadlines, idle
    /// reclaim, ordered pumping, orphan-ack cleanup) is unit-testable on its own.
    /// </summary>
    internal sealed class ProxyTunnelCorrelation
    {
        private sealed class Session
        {
            internal readonly string Id;
            internal DateTime LastActivityUtc;
            internal TaskCompletionSource<ProxyTunnelOpenOutcome> OpenWaiter;
            internal readonly Queue<byte[]> Data = new Queue<byte[]>();
            internal TaskCompletionSource<byte[]> DataWaiter;
            internal bool Closed;

            internal Session(string id)
            {
                Id = id;
                LastActivityUtc = DateTime.UtcNow;
                OpenWaiter = new TaskCompletionSource<ProxyTunnelOpenOutcome>();
            }
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);

        internal int Count
        {
            get
            {
                lock (_sync)
                {
                    return _sessions.Count;
                }
            }
        }

        /// <summary>Reserve a tunnel id; false when it is already live (a client-side duplicate).</summary>
        internal bool TryRegister(string id)
        {
            lock (_sync)
            {
                if (_sessions.ContainsKey(id))
                {
                    return false;
                }

                _sessions[id] = new Session(id);
                return true;
            }
        }

        /// <summary>
        /// Await the Event Server's open answer for a registered id; null when the id is not (or
        /// no longer) live. Completion arrives via <see cref="CompleteOpen"/>, a failure path, or
        /// any close (which completes it as an abandoned-open failure so waiters never hang).
        /// </summary>
        internal Task<ProxyTunnelOpenOutcome> AwaitOpenAsync(string id)
        {
            lock (_sync)
            {
                Session session;
                if (!_sessions.TryGetValue(id, out session) || session.Closed || session.OpenWaiter == null)
                {
                    return Task.FromResult<ProxyTunnelOpenOutcome>(null);
                }

                return session.OpenWaiter.Task;
            }
        }

        /// <summary>
        /// Land the open response. A success keeps the session live for pumping; a failure closes
        /// and removes it. An ack for an unknown id is an orphan: the client already abandoned
        /// this open (deadline/sweep) while the Event Server was still connecting, so the handle
        /// it came with is closed immediately rather than left orphaned on the target.
        /// </summary>
        internal void CompleteOpen(string id, ProxyTunnelOpenOutcome outcome)
        {
            if (outcome == null)
            {
                return;
            }

            lock (_sync)
            {
                Session session;
                if (!_sessions.TryGetValue(id, out session) || session.Closed)
                {
                    if (outcome.Handle != null)
                    {
                        outcome.Handle.Close("the Smart Client stopped waiting for this tunnel before it opened");
                    }

                    return;
                }

                session.LastActivityUtc = DateTime.UtcNow;
                var waiter = session.OpenWaiter;
                session.OpenWaiter = null;

                if (!outcome.Success)
                {
                    session.Closed = true;
                    _sessions.Remove(id);
                    var dataWaiter = session.DataWaiter;
                    session.DataWaiter = null;
                    dataWaiter?.TrySetResult(null);
                }

                waiter?.TrySetResult(outcome);
            }
        }

        /// <summary>Relay target bytes toward the browser, in arrival order. Unknown/closed ids drop.</summary>
        internal void OnTargetData(string id, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            lock (_sync)
            {
                Session session;
                if (!_sessions.TryGetValue(id, out session) || session.Closed)
                {
                    return;
                }

                session.LastActivityUtc = DateTime.UtcNow;
                var waiter = session.DataWaiter;
                session.DataWaiter = null;
                if (waiter != null)
                {
                    waiter.TrySetResult(bytes);
                }
                else
                {
                    session.Data.Enqueue(bytes);
                }
            }
        }

        /// <summary>
        /// The browser pump's read: the next target bytes, or null once the tunnel is closed
        /// (either side). A closed/unknown id answers null immediately, so a pump racing with a
        /// reclaim unwinds instead of parking.
        /// </summary>
        internal Task<byte[]> NextDataAsync(string id)
        {
            lock (_sync)
            {
                Session session;
                if (!_sessions.TryGetValue(id, out session) || session.Closed)
                {
                    return Task.FromResult<byte[]>(null);
                }

                if (session.Data.Count > 0)
                {
                    return Task.FromResult(session.Data.Dequeue());
                }

                // One pump per tunnel by construction; a replaced waiter would only mean a
                // second concurrent reader, which the flow never creates.
                session.DataWaiter = new TaskCompletionSource<byte[]>();
                return session.DataWaiter.Task;
            }
        }

        /// <summary>The Event Server ended the tunnel (target closed or reclaimed): end the pump.</summary>
        internal void OnTargetClose(string id, string reason)
        {
            lock (_sync)
            {
                CloseLocked(id, reason ?? "the Event Server closed the tunnel");
            }
        }

        /// <summary>
        /// The browser side ended the tunnel. True when a live session was removed (the caller
        /// owes the Event Server a close notice); false for unknown/already-closed ids.
        /// </summary>
        internal bool EndLocal(string id)
        {
            lock (_sync)
            {
                return _sessions.Remove(id);
            }
        }

        /// <summary>Browser-to-target traffic keeps the tunnel off the idle deadline.</summary>
        internal void Touch(string id)
        {
            lock (_sync)
            {
                Session session;
                if (_sessions.TryGetValue(id, out session) && !session.Closed)
                {
                    session.LastActivityUtc = DateTime.UtcNow;
                }
            }
        }

        internal bool IsClosed(string id)
        {
            lock (_sync)
            {
                Session session;
                return !_sessions.TryGetValue(id, out session) || session.Closed;
            }
        }

        /// <summary>
        /// Reclaim tunnels with no traffic in either direction for <paramref name="idle"/> —
        /// including half-open ones that never got their open ack. Returns the reclaimed ids so
        /// the caller can notify the Event Server to release its side.
        /// </summary>
        internal IReadOnlyList<string> ReclaimIdle(TimeSpan idle)
        {
            var cutoff = DateTime.UtcNow - idle;
            List<string> reclaimed = null;
            lock (_sync)
            {
                List<string> stale = null;
                foreach (var pair in _sessions)
                {
                    if (pair.Value.LastActivityUtc < cutoff)
                    {
                        if (stale == null)
                        {
                            stale = new List<string>();
                        }

                        stale.Add(pair.Key);
                    }
                }

                if (stale != null)
                {
                    reclaimed = new List<string>();
                    foreach (var id in stale)
                    {
                        CloseLocked(id, "the Smart Client reclaimed an idle websocket tunnel");
                        reclaimed.Add(id);
                    }
                }
            }

            return reclaimed ?? (IReadOnlyList<string>)new string[0];
        }

        /// <summary>Shut every tunnel down (client teardown): waiters unwind, registry empties.</summary>
        internal IReadOnlyList<string> CloseAll(string reason)
        {
            lock (_sync)
            {
                List<string> ids = null;
                foreach (var pair in _sessions)
                {
                    if (ids == null)
                    {
                        ids = new List<string>();
                    }

                    ids.Add(pair.Key);
                }

                if (ids == null)
                {
                    return new string[0];
                }

                foreach (var id in ids)
                {
                    CloseLocked(id, reason);
                }

                return ids;
            }
        }

        private void CloseLocked(string id, string reason)
        {
            Session session;
            if (!_sessions.TryGetValue(id, out session))
            {
                return;
            }

            session.Closed = true;
            _sessions.Remove(id);

            var openWaiter = session.OpenWaiter;
            session.OpenWaiter = null;
            openWaiter?.TrySetResult(new ProxyTunnelOpenOutcome
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.Timeout,
                ErrorMessage = "the websocket tunnel closed before it opened: " + reason,
            });

            var dataWaiter = session.DataWaiter;
            session.DataWaiter = null;
            dataWaiter?.TrySetResult(null);
        }

        internal void DebugSetLastActivity(string id, DateTime when)
        {
            lock (_sync)
            {
                Session session;
                if (_sessions.TryGetValue(id, out session))
                {
                    session.LastActivityUtc = when;
                }
            }
        }
    }
}