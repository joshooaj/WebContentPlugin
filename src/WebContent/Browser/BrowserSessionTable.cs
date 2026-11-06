using System;
using System.Collections.Generic;

namespace WebContent.Browser
{
    /// <summary>
    /// One browser session known to the plugin module instance: the Edge/WebView2 user data folder that carries
    /// that session's browser state, plus the session's display name and trigger tally. Entries are created for
    /// explicit ids carried by metadata or trigger messages — never for the default view sessions, which have no
    /// identity beyond their view.
    ///
    /// Thread safety: lock-guarded; metadata registration happens on the module load thread while trigger
    /// resolution happens on Smart Client UI threads.
    /// </summary>
    internal sealed class BrowserSessionTable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();

        /// <summary>Registers a declared (metadata-carried) browser session. An id that already exists keeps its
        /// current display name — the first declaration wins, and live sessions already bound to the entry are
        /// never renamed or re-homed mid-flight.</summary>
        public void Declare(int id, string displayName)
        {
            if (!BrowserSessionId.IsValid(id))
            {
                throw new ArgumentOutOfRangeException(nameof(id), "Browser session ids must be positive integers.");
            }

            lock (_sync)
            {
                if (!_entries.TryGetValue(id, out Entry entry))
                {
                    entry = new Entry(id, displayName, @explicit: true);
                    _entries.Add(id, entry);
                }
            }
        }

        /// <summary>Looks up a known session, or creates an implicit entry for an id that a trigger message
        /// referenced without a prior declaration. Implicit entries carry no display name until first used.</summary>
        public Entry Resolve(int id)
        {
            if (!BrowserSessionId.IsValid(id))
            {
                throw new ArgumentOutOfRangeException(nameof(id), "Browser session ids must be positive integers.");
            }

            lock (_sync)
            {
                if (!_entries.TryGetValue(id, out Entry entry))
                {
                    entry = new Entry(id, displayName: null, @explicit: false);
                    _entries.Add(id, entry);
                }
                return entry;
            }
        }

        public Entry Find(int id)
        {
            lock (_sync)
            {
                Entry entry;
                _entries.TryGetValue(id, out entry);
                return entry;
            }
        }

        public IEnumerable<Entry> Snapshot()
        {
            lock (_sync)
            {
                var entries = new List<Entry>(_entries.Values);
                entries.Sort((a, b) => a.Id.CompareTo(b.Id));
                return entries;
            }
        }

        internal sealed class Entry
        {
            public Entry(int id, string displayName, bool @explicit)
            {
                Id = id;
                DisplayName = displayName;
                IsExplicit = @explicit;
            }

            public int Id { get; }
            public string DisplayName { get; private set; }

            /// <summary>True when a module descriptor declared this session; false for implicit sessions first
            /// named by a trigger message.</summary>
            public bool IsExplicit { get; private set; }

            /// <summary>The live browsing data folder for this session — created once, on first use, and reused
            /// by every view that opens the session afterwards.</summary>
            public string ProfileDir { get; set; }

            /// <summary>Number of accepted trigger navigations into this session; drives the proxy trigger banner
            /// and block tallies. Survives the closing and reopening of views, which is why it lives here rather
            /// than on a view.</summary>
            public int TriggerCount { get; set; }

            public void SetDisplayNameIfMissing(string displayName)
            {
                if (string.IsNullOrEmpty(DisplayName) && !string.IsNullOrEmpty(displayName))
                {
                    DisplayName = displayName;
                    IsExplicit = true;
                }
            }
        }
    }
}
