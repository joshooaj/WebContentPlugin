using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// One proxied browser-view instance's diagnostics identity (#69): the client-generated
    /// session id carried on every fetch/tunnel request and lifecycle event, plus the workspace
    /// item and definition fingerprint block reports are filed under, and a creation stamp for
    /// the heartbeat bookkeeping. Created by <c>BrowserManager</c> when a proxied view's
    /// loopback listener starts and retired when that listener stops.
    /// </summary>
    internal sealed class ProxyViewSessionContext
    {
        internal string SessionId;
        internal string WorkspaceItemId;
        internal string WorkspaceFingerprint;
        internal string ClientMachine;

        internal bool HasWorkspaceIdentity
        {
            get
            {
                Guid parsed;
                return WorkspaceItemId != null && Guid.TryParse(WorkspaceItemId, out parsed) && parsed != Guid.Empty;
            }
        }
    }

    /// <summary>
    /// Ticket #69: the Smart Client's registry of live proxied-view sessions. Pure bookkeeping:
    /// it hands the caller the lifecycle payloads owed (heartbeat fan-out) and answers session
    /// lookups; the actual transmission rides the seam the caller supplies. <see cref="Active"/>
    /// is the process-wide instance the browser manager and the messaging client share — sessions
    /// outlive a messaging-channel re-provision, so heartbeats resume on the new channel.
    /// </summary>
    internal sealed class ProxySessionRegistry
    {
        internal static readonly ProxySessionRegistry Active = new ProxySessionRegistry();

        private sealed class Entry
        {
            internal ProxyViewSessionContext Context;
            internal DateTime LastRefreshUtc;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _sessions = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Register an opened session; no-op for contextless (legacy) listeners.</summary>
        internal void Open(ProxyViewSessionContext context)
        {
            if (context == null || string.IsNullOrEmpty(context.SessionId))
            {
                return;
            }

            lock (_sync)
            {
                _sessions[context.SessionId] = new Entry
                {
                    Context = context,
                    LastRefreshUtc = DateTime.UtcNow,
                };
            }
        }

        /// <summary>Retire a session (idempotent).</summary>
        internal void Close(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            lock (_sync)
            {
                _sessions.Remove(sessionId);
            }
        }

        /// <summary>Retire every session (plugin teardown).</summary>
        internal void CloseAll()
        {
            lock (_sync)
            {
                _sessions.Clear();
            }
        }

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

        /// <summary>Snapshot of the live contexts (diagnostics/tests).</summary>
        internal IReadOnlyList<ProxyViewSessionContext> SnapshotContexts()
        {
            lock (_sync)
            {
                var contexts = new List<ProxyViewSessionContext>(_sessions.Count);
                foreach (var entry in _sessions)
                {
                    contexts.Add(entry.Value.Context);
                }

                return contexts;
            }
        }

        /// <summary>
        /// Build one Heartbeat payload per live session. A caller that cannot send (channel
        /// down) simply drops the payloads: the Event Server's idle backstop prunes the stale
        /// sessions, and the next successful tick refreshes them again.
        /// </summary>
        internal IReadOnlyList<ProxySessionEventPayload> HeartbeatPayloads(DateTime utcNow)
        {
            lock (_sync)
            {
                var payloads = new List<ProxySessionEventPayload>(_sessions.Count);
                foreach (var entry in _sessions)
                {
                    entry.Value.LastRefreshUtc = utcNow;
                    payloads.Add(new ProxySessionEventPayload
                    {
                        SessionId = entry.Value.Context.SessionId,
                        Kind = ProxySessionEventKinds.Heartbeat,
                        WorkspaceItemId = entry.Value.Context.WorkspaceItemId,
                        WorkspaceFingerprint = entry.Value.Context.WorkspaceFingerprint,
                    });
                }

                return payloads;
            }
        }
    }
}