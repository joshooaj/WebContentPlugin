using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// Ticket #83: one dedup'd row of a browser view's in-memory blocked-resource list —
    /// the exact canonical origin an admin would need to allowlist, its refusal category,
    /// how often it was refused this page load, and when it was last seen.
    /// </summary>
    public sealed class ProxyBlockTallyEntry
    {
        /// <summary>Origin in <see cref="ProxyPolicy.CanonicalOrigin"/> form ("scheme://host[:port]") — selectable for copying.</summary>
        public string Origin { get; internal set; }

        /// <summary>One of <see cref="ProxyBlockReasons"/>.</summary>
        public string Reason { get; internal set; }

        /// <summary>Human-readable rendering of <see cref="Reason"/> for the view list.</summary>
        public string ReasonLabel => ProxyBlockDisplay.ReasonLabel(Reason);

        /// <summary>Refusals this origin+reason has accumulated since the last reset.</summary>
        public long Count { get; internal set; }

        public DateTime LastSeenUtc { get; internal set; }
    }

    /// <summary>
    /// Ticket #83: the per-browser-view in-memory tally of everything the loopback listener
    /// refused. It IS the reporter the forwarding flow calls — every refusal forwards to the
    /// Event Server's <see cref="ProxyBlockReporter"/> exactly as #69 shipped it, AND counts
    /// locally so the Smart Client's setup-mode indicator can list blocked subresources with
    /// zero ES roundtrip. Counters are per browser-view instance and reset on top-level
    /// navigation (NavigationStarting), like the browser's own console.
    ///
    /// A bounded mirror of the ES ring: at most <see cref="MaxEntries"/> distinct origin+reason
    /// rows, evicting the least recently seen, so a chatty broken page cannot grow the view's
    /// chrome without bound. Reporting never throws into the browser flow, and the local count
    /// survives a dead forwarder — "block events are counted locally regardless".
    /// </summary>
    internal sealed class ProxyBlockTally : IProxyBlockReporter
    {
        internal const int MaxEntries = 200;

        private readonly object _sync = new object();
        private readonly Dictionary<string, ProxyBlockTallyEntry> _entries = new Dictionary<string, ProxyBlockTallyEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> _log;
        private readonly Func<DateTime> _utcNow;

        private IProxyBlockReporter _forwarder;
        private long _total;

        /// <summary>
        /// Raised after any counted refusal or reset — from whatever socket thread decided the
        /// refusal. Subscribers (the view chrome) marshal to the UI thread themselves; subscriber
        /// faults are swallowed here and never reach the browser flow.
        /// </summary>
        internal event Action Changed;

        internal ProxyBlockTally(Action<string> log, Func<DateTime> utcNow = null)
        {
            _log = log ?? (message => { });
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Attach the per-listener ES reporter (#69). Swapping/clearing is safe any time.</summary>
        internal void SetForwarder(IProxyBlockReporter forwarder)
        {
            lock (_sync)
            {
                _forwarder = forwarder;
            }
        }

        /// <summary>Sum of every counted refusal since the last reset (what "N resources blocked" shows).</summary>
        internal long TotalBlocks
        {
            get
            {
                lock (_sync)
                {
                    return _total;
                }
            }
        }

        public void Report(Uri origin, string reason)
        {
            if (origin == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            // Forward first: the ES half keeps its exact #69 posture (its own guards decide
            // whether a report is sendable), and a throwing send seam stays swallowed there.
            IProxyBlockReporter forwarder;
            lock (_sync)
            {
                forwarder = _forwarder;
            }
            if (forwarder != null)
            {
                try
                {
                    forwarder.Report(origin, reason);
                }
                catch (Exception exception)
                {
                    _log($"Block forward to the Event Server failed (local tally kept): {exception.Message}");
                }
            }

            string canonical;
            try
            {
                canonical = ProxyPolicy.CanonicalOrigin(origin);
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrEmpty(canonical))
            {
                return;
            }

            var now = _utcNow();
            var isNew = false;
            lock (_sync)
            {
                var key = canonical + "|" + reason;
                ProxyBlockTallyEntry entry;
                if (_entries.TryGetValue(key, out entry))
                {
                    entry.Count += 1;
                    entry.LastSeenUtc = now;
                }
                else
                {
                    EvictLeastRecentIfNeeded(now);
                    entry = new ProxyBlockTallyEntry
                    {
                        Origin = canonical,
                        Reason = reason,
                        Count = 1,
                        LastSeenUtc = now,
                    };
                    _entries[key] = entry;
                    isNew = true;
                }
                _total += 1;
            }

            // One marker per NEW origin+reason so a refusal storm cannot bury the log
            // (same discipline as the ES's "Proxy block recorded" marker, #69).
            if (isNew)
            {
                _log($"Proxy block tallied for this view: origin='{canonical}' reason={reason}");
            }

            RaiseChanged();
        }

        /// <summary>
        /// Forget every counted refusal (top-level reload/navigation; the new page load starts
        /// its own tally). Fires Changed so the indicator drops out of sight when the count hits zero.
        /// </summary>
        internal void Reset()
        {
            lock (_sync)
            {
                if (_entries.Count == 0 && _total == 0)
                {
                    return;
                }

                _entries.Clear();
                _total = 0;
            }

            RaiseChanged();
        }

        /// <summary>Snapshot of the current rows, most recently seen first.</summary>
        internal List<ProxyBlockTallyEntry> Snapshot()
        {
            lock (_sync)
            {
                var rows = new List<ProxyBlockTallyEntry>(_entries.Values);
                rows.Sort((a, b) => b.LastSeenUtc.CompareTo(a.LastSeenUtc));
                return rows;
            }
        }

        /// <summary>Caller holds _sync.</summary>
        private void EvictLeastRecentIfNeeded(DateTime now)
        {
            if (_entries.Count < MaxEntries)
            {
                return;
            }

            string oldestKey = null;
            var oldestSeen = DateTime.MaxValue;
            foreach (var pair in _entries)
            {
                if (pair.Value.LastSeenUtc < oldestSeen)
                {
                    oldestSeen = pair.Value.LastSeenUtc;
                    oldestKey = pair.Key;
                }
            }
            if (oldestKey != null)
            {
                _entries.Remove(oldestKey);
            }
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception exception)
            {
                _log($"Blocked-resource indicator callback failed: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Ticket #83: the cause-category wording for the blocked-resource surfaces. One shared
    /// vocabulary for every surface that renders <see cref="ProxyBlockReasons"/> (the Smart
    /// Client list here, the MC Diagnostics tab later, the error pages after #66). Pure and
    /// invariant-culture; the resx layer stays out of the testable seam.
    /// </summary>
    internal static class ProxyBlockDisplay
    {
        internal static string ReasonLabel(string reason)
        {
            switch (reason)
            {
                case ProxyBlockReasons.NotAllowed:
                    return "not allowed by administrator";
                case ProxyBlockReasons.TooLarge:
                    return "response too large";
                case ProxyBlockReasons.CertUntrusted:
                    return "certificate not trusted";
                case ProxyBlockReasons.Unreachable:
                    return "target unreachable";
                case ProxyBlockReasons.Timeout:
                    return "target timed out";
                case ProxyBlockReasons.TunnelRefused:
                    return "proxy tunnel refused";
                default:
                    return "(unknown)";
            }
        }
    }
}
