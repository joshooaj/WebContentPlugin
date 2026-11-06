using System;
using System.Collections.Generic;

namespace WebContent.Browser
{
    /// <summary>
    /// The browser sessions owned by one plugin module instance — the single place that maps browser session
    /// ids to their live browser state. Entries exist only for explicit sessions (declared in module metadata or
    /// first named by a trigger message); views without an id run in per-view default sessions that live in
    /// <see cref="ViewBrowserSessions"/> instead.
    ///
    /// The table is keyed by a stable per-view owner object (the view's view-id wrapped in <see cref="ViewIdentity"/>,
    /// or the descriptor object of a trigger's owning module). The <see cref="ForView"/> lookup creates the owner's
    /// entry on demand.
    ///
    /// Thread safety: lock-guarded; registration happens on the module load thread, resolution on Smart Client UI
    /// threads.
    /// </summary>
    internal sealed class ViewBrowserSessions
    {
        /// <summary>Display name prefix for the auto-named default sessions of plain views.</summary>
        public const string ViewPrefix = "View";

        private readonly object _sync = new object();
        private readonly Dictionary<OwnerKey, Sessions> _byOwner = new Dictionary<OwnerKey, Sessions>();
        private int _nextViewSlot;

        /// <summary>The default-session collection owned by <paramref name="owner"/> (the PluginView for
        /// view-initiated actions, the descriptor for module-initiated ones). Created on first use.</summary>
        public Sessions ForView(object owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            lock (_sync)
            {
                Sessions sessions;
                if (!_byOwner.TryGetValue(new OwnerKey(owner), out sessions))
                {
                    sessions = new Sessions(this);
                    _byOwner.Add(new OwnerKey(owner), sessions);
                }
                return sessions;
            }
        }

        /// <summary>Hands out the next auto-name for a new view default session ("View 1", "View 2", …). The
        /// caller commits it by storing it on the session it opens; the slot is not consumed until then.</summary>
        internal int NextViewSlot()
        {
            lock (_sync)
            {
                return ++_nextViewSlot;
            }
        }

        internal string AllocateViewName(int slot)
        {
            lock (_sync)
            {
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in _byOwner.Values)
                {
                    foreach (var session in entry.SnapshotLocked())
                    {
                        if (!string.IsNullOrEmpty(session.DisplayName))
                        {
                            taken.Add(session.DisplayName);
                        }
                    }
                }

                string candidate = ViewPrefix + " " + slot;
                int bump = slot;
                while (taken.Contains(candidate))
                {
                    candidate = ViewPrefix + " " + (++bump);
                }
                return candidate;
            }
        }

        /// <summary>Number of live default sessions for an owner whose auto-name was committed; used to size
        /// recovery numbering after a view reopens.</summary>
        internal int CountOpenViewSessions(object owner)
        {
            lock (_sync)
            {
                Sessions sessions;
                if (!_byOwner.TryGetValue(new OwnerKey(owner), out sessions))
                {
                    return 0;
                }
                return sessions.OpenDefaultCount;
            }
        }

        /// <summary>Browser sessions of one owner (a view or a trigger's module).</summary>
        internal sealed class Sessions
        {
            private readonly ViewBrowserSessions _table;
            private readonly object _sync;
            private readonly List<Session> _sessions = new List<Session>();

            internal Sessions(ViewBrowserSessions table)
            {
                _table = table;
                _sync = table._sync;
            }

            /// <summary>Finds the owner's live session for <paramref name="browserSessionId"/>, or creates one
            /// (first use of that id by this owner). The display name is fixed at creation and never renamed.</summary>
            public Session OpenSession(int browserSessionId, string displayName)
            {
                lock (_sync)
                {
                    foreach (var session in _sessions)
                    {
                        if (session.BrowserSessionId == browserSessionId)
                        {
                            return session;
                        }
                    }
                    var created = new Session(browserSessionId, displayName);
                    _sessions.Add(created);
                    return created;
                }
            }

            public Session FindSession(int browserSessionId)
            {
                lock (_sync)
                {
                    foreach (var session in _sessions)
                    {
                        if (session.BrowserSessionId == browserSessionId)
                        {
                            return session;
                        }
                    }
                    return null;
                }
            }

            /// <summary>Opens (or recovers) one default session for this owner. <paramref name="slot"/> is the
            /// owner's current auto-name slot; the name is only committed for a freshly created session.</summary>
            public Session OpenDefaultSession(int slot)
            {
                lock (_sync)
                {
                    if (_sessions.Count > 0)
                    {
                        return _sessions[_sessions.Count - 1];
                    }
                    var created = new Session(null, _table.AllocateViewName(slot));
                    _sessions.Add(created);
                    return created;
                }
            }

            public void CloseSession(Session session)
            {
                lock (_sync)
                {
                    _sessions.Remove(session);
                }
            }

            public IReadOnlyList<Session> Snapshot()
            {
                lock (_sync)
                {
                    return new List<Session>(_sessions);
                }
            }

            internal IEnumerable<Session> SnapshotLocked()
            {
                return _sessions;
            }

            internal int OpenDefaultCount
            {
                get
                {
                    lock (_sync)
                    {
                        int count = 0;
                        foreach (var session in _sessions)
                        {
                            if (session.BrowserSessionId == null)
                            {
                                count++;
                            }
                        }
                        return count;
                    }
                }
            }
        }

        /// <summary>One browser session as seen by its owner: the id (null for default view sessions) and the
        /// display name used for the tab title.</summary>
        internal sealed class Session
        {
            public Session(int? browserSessionId, string displayName)
            {
                BrowserSessionId = browserSessionId;
                DisplayName = displayName;
            }

            public int? BrowserSessionId { get; }
            public string DisplayName { get; private set; }

            public void SetDisplayName(string displayName)
            {
                if (!string.IsNullOrEmpty(displayName))
                {
                    DisplayName = displayName;
                }
            }
        }

        /// <summary>Dictionary key wrapper: reference identity of the owner, with a stable hash.</summary>
        private struct OwnerKey
        {
            private readonly object _owner;

            public OwnerKey(object owner)
            {
                _owner = owner;
            }

            public override bool Equals(object obj)
            {
                return obj is OwnerKey && ReferenceEquals(((OwnerKey)obj)._owner, _owner);
            }

            public override int GetHashCode()
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_owner);
            }
        }
    }
}
