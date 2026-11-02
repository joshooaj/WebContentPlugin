using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// Ticket #69: the Event Server's in-memory diagnostics state — per-workspace dedup'd ring
    /// buffers of recently-blocked origins and per-session live traffic counters. Nothing here
    /// persists: an Event Server (or plugin) restart resets every buffer, by design.
    ///
    /// The class is pure (all timestamps are caller-supplied) so the whole aggregation model is
    /// unit-testable without MIP. The messaging plumbing around it swallows every fault: an
    /// escaping exception here would have to come from a bug, and diagnostics must never take
    /// proxy traffic down with it.
    ///
    /// Thread-safe: reports and traffic land from messaging-receiver and fetch/tunnel worker
    /// threads; snapshots come from the status-request path.
    /// </summary>
    internal sealed class ProxyDiagnosticsAggregator
    {
        /// <summary>Distinct blocked origins kept per workspace before the oldest is evicted.</summary>
        internal const int MaxBlockedOriginsPerWorkspace = 200;

        private sealed class BlockedEntry
        {
            internal string Origin;
            internal string Reason;
            internal long Count;
            internal DateTime LastSeenUtc;

            internal string DedupKey
            {
                get { return Reason + "\n" + Origin; }
            }
        }

        internal sealed class SessionRecord
        {
            internal string SessionId;
            internal string WorkspaceItemId;
            internal string UserName;
            internal string ClientMachine;
            internal DateTime OpenedUtc;
            internal DateTime LastActivityUtc;
            internal long Requests;
            internal long BytesSent;
            internal long BytesReceived;
        }

        private sealed class WorkspaceState
        {
            /// <summary>Dedup key (origin + reason) to entry; insertion order doubles as eviction order.</summary>
            internal readonly Dictionary<string, BlockedEntry> Blocked = new Dictionary<string, BlockedEntry>(StringComparer.Ordinal);
            internal readonly LinkedList<BlockedEntry> InsertionOrder = new LinkedList<BlockedEntry>();
            internal readonly Dictionary<string, SessionRecord> Sessions = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, WorkspaceState> _byWorkspace = new Dictionary<string, WorkspaceState>(StringComparer.Ordinal);

        /// <summary>
        /// Record one blocked origin for a workspace: same origin+reason accumulates count and
        /// moves to the fresh end of the recency order; a new entry past the cap evicts the least
        /// recently seen one first. Blank fields drop the report — the ring only holds usable
        /// entries. Returns true only when the report created a NEW ring entry, which is the
        /// transition the caller logs: a refusal storm stays quiet, a newly blocked origin shows up.
        /// </summary>
        internal bool RecordBlock(string workspaceItemId, string origin, string reason, int count, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(workspaceItemId) || string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(reason))
            {
                return false;
            }

            var taken = count > 0 ? count : 1;
            lock (_sync)
            {
                var state = GetOrCreate(workspaceItemId);

                var key = reason + "\n" + origin;
                BlockedEntry existing;
                if (state.Blocked.TryGetValue(key, out existing))
                {
                    existing.Count += taken;
                    existing.LastSeenUtc = utcNow;
                    // Recency order: a refreshed origin moves to the fresh end, so the ring's
                    // eviction always drops the least recently seen one and a persistently
                    // blocked origin cannot age out of the view while stale ones linger.
                    state.InsertionOrder.Remove(existing);
                    state.InsertionOrder.AddLast(existing);
                    return false;
                }

                var entry = new BlockedEntry
                {
                    Origin = origin,
                    Reason = reason,
                    Count = taken,
                    LastSeenUtc = utcNow,
                };
                state.Blocked[key] = entry;
                state.InsertionOrder.AddLast(entry);

                while (state.InsertionOrder.Count > MaxBlockedOriginsPerWorkspace)
                {
                    var oldest = state.InsertionOrder.First.Value;
                    state.InsertionOrder.RemoveFirst();
                    state.Blocked.Remove(oldest.DedupKey);
                }

                return true;
            }
        }

        /// <summary>
        /// File a block against the workspace a live session belongs to — the Event Server's own
        /// refusals (allowlist gate, fetcher and tunnel-open failures) carry only the session id,
        /// and the workspace attribution lives in the session table. False for unknown ids.
        /// </summary>
        internal bool RecordBlockForSession(string sessionId, string origin, string reason, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            string workspaceItemId;
            lock (_sync)
            {
                var record = FindSession(sessionId);
                if (record == null)
                {
                    return false;
                }

                workspaceItemId = record.WorkspaceItemId;
            }

            RecordBlock(workspaceItemId, origin, reason, 1, utcNow);
            return true;
        }

        /// <summary>
        /// Register a session as open. A duplicate id refreshes in place (a Smart Client resend
        /// after a transient channel loss) rather than doubling the table.
        /// </summary>
        internal void OpenSession(string sessionId, string workspaceItemId, string userName, string clientMachine, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(workspaceItemId))
            {
                return;
            }

            lock (_sync)
            {
                var state = GetOrCreate(workspaceItemId);
                SessionRecord existing;
                if (state.Sessions.TryGetValue(sessionId, out existing))
                {
                    if (userName != null)
                    {
                        existing.UserName = userName;
                    }

                    if (!string.IsNullOrEmpty(clientMachine))
                    {
                        existing.ClientMachine = clientMachine;
                    }

                    existing.LastActivityUtc = utcNow;
                    return;
                }

                state.Sessions[sessionId] = new SessionRecord
                {
                    SessionId = sessionId,
                    WorkspaceItemId = workspaceItemId,
                    UserName = userName,
                    ClientMachine = clientMachine,
                    OpenedUtc = utcNow,
                    LastActivityUtc = utcNow,
                };
            }
        }

        /// <summary>Keep a session off the idle backstop. False for unknown ids.</summary>
        internal bool HeartbeatSession(string sessionId, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_sync)
            {
                var record = FindSession(sessionId);
                if (record == null)
                {
                    return false;
                }

                record.LastActivityUtc = utcNow;
                return true;
            }
        }

        /// <summary>
        /// Close a session on the client's word, folding any final traffic counters the tunnel
        /// pumps still owed it. False for unknown ids.
        /// </summary>
        internal bool CloseSession(string sessionId, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_sync)
            {
                foreach (var state in _byWorkspace.Values)
                {
                    if (state.Sessions.Remove(sessionId))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Drop sessions no client heartbeat or attributed traffic has refreshed for
        /// <paramref name="idle"/> — the backstop for Smart Clients that die without a
        /// SessionClose. Returns the reclaimed ids so the caller can log them. Empty workspace
        /// tables go with them; blocked rings never do.
        /// </summary>
        internal IReadOnlyList<string> SweepIdleSessions(TimeSpan idle, DateTime utcNow)
        {
            var cutoff = utcNow - idle;
            List<string> reclaimed = null;
            lock (_sync)
            {
                foreach (var workspace in new List<string>(_byWorkspace.Keys))
                {
                    var state = _byWorkspace[workspace];

                    List<string> stale = null;
                    foreach (var record in state.Sessions.Values)
                    {
                        if (record.LastActivityUtc < cutoff)
                        {
                            if (stale == null)
                            {
                                stale = new List<string>();
                            }

                            stale.Add(record.SessionId);
                        }
                    }

                    if (stale == null)
                    {
                        continue;
                    }

                    if (reclaimed == null)
                    {
                        reclaimed = new List<string>();
                    }

                    foreach (var sessionId in stale)
                    {
                        state.Sessions.Remove(sessionId);
                        reclaimed.Add(sessionId);
                    }

                    if (state.Sessions.Count == 0 && state.Blocked.Count == 0)
                    {
                        _byWorkspace.Remove(workspace);
                    }
                }
            }

            return reclaimed ?? (IReadOnlyList<string>)new string[0];
        }

        /// <summary>
        /// Attribute one served fetch to its session: +1 request, request-body bytes out,
        /// response-body bytes back. False for unknown ids (a stale or forged session id must
        /// not poison the live table).
        /// </summary>
        internal bool RecordFetchTraffic(string sessionId, long requestBytes, long responseBytes, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_sync)
            {
                var record = FindSession(sessionId);
                if (record == null)
                {
                    return false;
                }

                record.LastActivityUtc = utcNow;
                record.Requests += 1;
                record.BytesSent += Math.Max(0, requestBytes);
                record.BytesReceived += Math.Max(0, responseBytes);
                return true;
            }
        }

        /// <summary>
        /// Attribute a chunk of websocket-tunnel bytes to the session that opened the tunnel;
        /// no request count bump (the open already counted as one request). False for unknown ids.
        /// </summary>
        internal bool RecordTunnelTraffic(string sessionId, long clientBytes, long targetBytes, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_sync)
            {
                var record = FindSession(sessionId);
                if (record == null)
                {
                    return false;
                }

                record.LastActivityUtc = utcNow;
                record.BytesSent += Math.Max(0, clientBytes);
                record.BytesReceived += Math.Max(0, targetBytes);
                return true;
            }
        }

        /// <summary>
        /// Build one workspace's snapshot: live sessions (with req/min over the open-to-now
        /// window, minimum one minute) and the recently-blocked list. Never null; empty lists
        /// when the workspace has nothing live or nothing blocked.
        /// </summary>
        internal ProxyStatusResponsePayload Snapshot(string workspaceItemId, DateTime utcNow)
        {
            var response = new ProxyStatusResponsePayload
            {
                WorkspaceItemId = workspaceItemId,
                Success = true,
                Sessions = new List<ProxySessionStatusEntry>(),
                Blocked = new List<ProxyBlockEntry>(),
            };

            if (string.IsNullOrEmpty(workspaceItemId))
            {
                return response;
            }

            lock (_sync)
            {
                WorkspaceState state;
                if (!_byWorkspace.TryGetValue(workspaceItemId, out state))
                {
                    return response;
                }

                foreach (var record in state.Sessions.Values)
                {
                    var minutes = (utcNow - record.OpenedUtc).TotalMinutes;
                    if (minutes < 1.0)
                    {
                        minutes = 1.0;
                    }

                    response.Sessions.Add(new ProxySessionStatusEntry
                    {
                        SessionId = record.SessionId,
                        UserName = record.UserName,
                        ClientMachine = record.ClientMachine,
                        OpenedUtc = record.OpenedUtc,
                        LastActivityUtc = record.LastActivityUtc,
                        Requests = record.Requests,
                        RequestsPerMinute = record.Requests / minutes,
                        BytesSent = record.BytesSent,
                        BytesReceived = record.BytesReceived,
                    });
                }

                foreach (var entry in state.InsertionOrder)
                {
                    response.Blocked.Add(new ProxyBlockEntry
                    {
                        Origin = entry.Origin,
                        Reason = entry.Reason,
                        Count = entry.Count,
                        LastSeenUtc = entry.LastSeenUtc,
                    });
                }
            }

            return response;
        }

        /// <summary>True when the workspace currently has a tracked session (diagnostic aid).</summary>
        internal bool HasSessions(string workspaceItemId)
        {
            lock (_sync)
            {
                WorkspaceState state;
                return workspaceItemId != null
                    && _byWorkspace.TryGetValue(workspaceItemId, out state)
                    && state.Sessions.Count > 0;
            }
        }

        /// <summary>
        /// #65: the workspace a live session belongs to, straight from the session table. The
        /// fallback attribution for fetch requests from clients that stamp no workspace id: an
        /// unknown session id answers false and the request stays unattributed (fail closed).
        /// </summary>
        internal bool TryGetSessionWorkspace(string sessionId, out string workspaceItemId)
        {
            workspaceItemId = null;
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            lock (_sync)
            {
                var record = FindSession(sessionId);
                if (record == null || string.IsNullOrEmpty(record.WorkspaceItemId))
                {
                    return false;
                }

                workspaceItemId = record.WorkspaceItemId;
                return true;
            }
        }

        /// <summary>Locate a session record across workspace tables; caller must hold the lock.</summary>
        private SessionRecord FindSession(string sessionId)
        {
            foreach (var state in _byWorkspace.Values)
            {
                SessionRecord record;
                if (state.Sessions.TryGetValue(sessionId, out record))
                {
                    return record;
                }
            }

            return null;
        }

        private WorkspaceState GetOrCreate(string workspaceItemId)
        {
            WorkspaceState state;
            if (!_byWorkspace.TryGetValue(workspaceItemId, out state))
            {
                state = new WorkspaceState();
                _byWorkspace[workspaceItemId] = state;
            }

            return state;
        }
    }
}