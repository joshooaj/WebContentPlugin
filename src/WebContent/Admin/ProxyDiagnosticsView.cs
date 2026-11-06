using System;
using System.Collections.Generic;
using System.Globalization;

namespace WebContent.Admin
{
    /// <summary>
    /// Ticket #84: why a blocked row's Status cell says what it says. The row model carries the
    /// machine-readable code; the WPF page turns it into the localized <c>Strings</c> sentence —
    /// the same ErrorCode/ErrorText split the #81 allowlist grid uses, so nothing exotic has to
    /// live in the page's XAML.
    /// </summary>
    internal enum ProxyBlockedRowStatus
    {
        /// <summary>No annotation: the origin is not allowlisted and the row can be promoted.</summary>
        None = 0,

        /// <summary>An allowlist entry already covers the origin — the promotion button is moot.</summary>
        Allowlisted = 1,

        /// <summary>
        /// The refusal is a certificate-trust failure (#84 AC: these rows get NO "Add to
        /// allowlist" button — the fix is the CA trust path, never the allowlist).
        /// </summary>
        CertTrust = 2,
    }

    /// <summary>
    /// Ticket #84: one rendered row of the Management Client's Diagnostics tab — one dedup'd
    /// (origin, reason) refusal from the Event Server's in-memory recently-blocked ring. Every
    /// display value is already a string so the WPF grid binds straight through and the whole
    /// formatting stays unit tested instead of hiding in a XAML StringFormat.
    /// </summary>
    internal sealed class ProxyBlockedRowView
    {
        /// <summary>The canonical origin ("scheme://host[:port]") the proxy refused — and exactly
        /// what a promoted allowlist entry would name.</summary>
        public string Origin { get; set; }

        /// <summary>The cause category as the admin reads it (<see cref="ProxyBlockDisplay"/>).</summary>
        public string ReasonLabel { get; set; }

        public string Hits { get; set; }

        /// <summary>Relative recency ("just now", "47s ago", "4m 07s ago", "2h 05m ago").</summary>
        public string LastSeen { get; set; }

        /// <summary>Machine-readable Status-cell decision (see <see cref="ProxyBlockedRowStatus"/>).</summary>
        public ProxyBlockedRowStatus StatusCode { get; set; }

        /// <summary>The localized Status-cell sentence; the page formats <see cref="StatusCode"/>.</summary>
        public string StatusText { get; set; }

        /// <summary>True exactly when the row gets its "Add to allowlist" button: a cert-trust
        /// refusal never does, an already-covered origin never needs one, and an origin the
        /// promotion could not turn into a valid entry never can.</summary>
        public bool CanPromote { get; set; }
    }

    /// <summary>
    /// Ticket #80: one client machine that reported the deployment CA as untrusted on itself.
    /// Dedup is per MACHINE (the Event Server folds every view instance on one workstation into
    /// one row), and the values arrive pre-formatted like the blocked rows so the banner text
    /// stays pure and unit-tested rather than hiding in a XAML StringFormat.
    /// </summary>
    internal sealed class ProxyCaUntrustedMachineRowView
    {
        public string Machine { get; set; }

        /// <summary>How many untrusted-CA reports this machine's views have sent (context, not a
        /// per-view count — the dedup happened at the Event Server).</summary>
        public string Reports { get; set; }

        public string LastSeen { get; set; }
    }

    /// <summary>
    /// Ticket #84: the whole Diagnostics tab's content in one pure value: whether the Event
    /// Server answered, and the blocked-origin rows. The page turns this into text and grid rows
    /// and nothing else, so the entire tab's logic is testable without WPF or MIP.
    /// </summary>
    internal sealed class ProxyDiagnosticsView
    {
        /// <summary>False for every non-answer: the page shows the failure, never an empty table.</summary>
        public bool QuerySucceeded { get; set; }

        /// <summary>Which failure the page must name (a <see cref="ProxyFetchErrorCodes"/> value),
        /// exactly as <see cref="ProxyStatusViewBuilder"/> hands it over.</summary>
        public string FailureCode { get; set; }

        public int BlockedCount { get; set; }

        public List<ProxyBlockedRowView> Rows { get; } = new List<ProxyBlockedRowView>();

        /// <summary>
        /// #80: distinct client machines reporting the proxy CA untrusted - the aggregate that
        /// drives the Diagnostics tab's warning banner. Zero for a pre-#80 snapshot (member
        /// absent) exactly as for a quiet workspace.
        /// </summary>
        public int UntrustedMachineCount { get; set; }

        public List<ProxyCaUntrustedMachineRowView> UntrustedMachineRows { get; } = new List<ProxyCaUntrustedMachineRowView>();
    }

    /// <summary>
    /// Ticket #84: the status snapshot → Diagnostics view transform. Pure: timestamps and the
    /// current allowlist entries come from the caller, so the whole tab (row order, cause labels,
    /// recency strings, the already-allowlisted annotation via THE shared matcher, and the
    /// CertUntrusted button ban) is pinned without WPF or MIP.
    /// </summary>
    internal static class ProxyDiagnosticsViewBuilder
    {
        /// <summary>
        /// Builds one render of the tab. <paramref name="allowlistEntries"/> are the canonical
        /// entries the admin's grid currently holds (saved or unsaved), <paramref
        /// name="vmsFloorHosts"/> the VMS's own host names — both feed the SAME
        /// <see cref="ProxyPolicy.IsRequestAllowed"/> matcher the three gates consume, so the
        /// annotation never disagrees with what the Event Server would decide for the origin.
        /// </summary>
        internal static ProxyDiagnosticsView Build(
            ProxyStatusResponsePayload payload,
            IEnumerable<string> allowlistEntries,
            IEnumerable<string> vmsFloorHosts,
            DateTime utcNow)
        {
            var view = new ProxyDiagnosticsView();
            if (payload == null || !payload.Success)
            {
                view.QuerySucceeded = false;
                view.FailureCode = payload == null || string.IsNullOrWhiteSpace(payload.ErrorCode)
                    ? ProxyFetchErrorCodes.ServerUnavailable
                    : payload.ErrorCode;
                return view;
            }

            view.QuerySucceeded = true;

            // #80: the untrusted-machine aggregate renders even when nothing else was blocked -
            // the CA posture flip is its own condition, and the warning banner must not depend
            // on the blocked ring having content. A null member (pre-#80 Event Server) reads as
            // zero machines.
            if (payload.UntrustedMachines != null)
            {
                var machines = new List<ProxyCaUntrustedMachineRowView>();
                var lastSeen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in payload.UntrustedMachines)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Machine))
                    {
                        continue;
                    }

                    machines.Add(new ProxyCaUntrustedMachineRowView
                    {
                        Machine = entry.Machine,
                        Reports = (entry.Count < 1 ? 1 : entry.Count).ToString(CultureInfo.InvariantCulture),
                        LastSeen = ProxyDiagnosticsFormat.Ago(utcNow - entry.LastSeenUtc),
                    });
                    lastSeen[entry.Machine] = entry.LastSeenUtc;
                }

                // Newest report first, machine name as the stable tie-break - same reading order
                // as the blocked list, so a refresh never reshuffles equal timestamps.
                machines.Sort((a, b) =>
                {
                    var bySeen = lastSeen[b.Machine].CompareTo(lastSeen[a.Machine]);
                    return bySeen != 0 ? bySeen : string.Compare(a.Machine, b.Machine, StringComparison.Ordinal);
                });

                view.UntrustedMachineRows.AddRange(machines);
                view.UntrustedMachineCount = machines.Count;
            }

            if (payload.Blocked == null || payload.Blocked.Count == 0)
            {
                return view;
            }

            var rows = new List<ProxyBlockedRowView>();
            var order = new List<DateTime>();
            foreach (var entry in payload.Blocked)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Origin))
                {
                    continue;
                }

                var status = ProxyBlockedRowStatus.None;
                if (string.Equals(entry.Reason, ProxyBlockReasons.CertUntrusted, StringComparison.Ordinal))
                {
                    status = ProxyBlockedRowStatus.CertTrust;
                }
                else if (IsCoveredByAllowlist(entry.Origin, allowlistEntries, vmsFloorHosts))
                {
                    status = ProxyBlockedRowStatus.Allowlisted;
                }

                // #84 AC: CertUntrusted rows never get the button; a covered origin has no use for
                // one; and an origin promotion could not turn into a valid exact entry never
                // offers a button that would only fail.
                var promotable = status == ProxyBlockedRowStatus.None &&
                    ProxyPolicy.TrySplitCanonicalEntry(entry.Origin, out _, out var pattern, out _) &&
                    pattern.IndexOf('*') < 0;

                rows.Add(new ProxyBlockedRowView
                {
                    Origin = entry.Origin,
                    ReasonLabel = ProxyBlockDisplay.ReasonLabel(entry.Reason),
                    Hits = (entry.Count < 1 ? 1 : entry.Count).ToString(CultureInfo.InvariantCulture),
                    LastSeen = ProxyDiagnosticsFormat.Ago(utcNow - entry.LastSeenUtc),
                    StatusCode = status,
                    CanPromote = promotable,
                });
                order.Add(entry.LastSeenUtc);
            }

            // Newest refusal first - the ring answers oldest-first (its eviction order), and the
            // admin reads a blocked list top-down. Last-seen descending, origin ordinal as the
            // stable tie-break so a refresh never reshuffles equal timestamps.
            var indexed = new List<int>();
            for (var i = 0; i < rows.Count; i++)
            {
                indexed.Add(i);
            }

            indexed.Sort((a, b) =>
            {
                var bySeen = order[b].CompareTo(order[a]);
                return bySeen != 0 ? bySeen : string.Compare(rows[a].Origin, rows[b].Origin, StringComparison.Ordinal);
            });

            var ordered = new List<ProxyBlockedRowView>(rows.Count);
            foreach (var index in indexed)
            {
                ordered.Add(rows[index]);
            }

            view.Rows.AddRange(ordered);
            view.BlockedCount = view.Rows.Count;
            return view;
        }

        /// <summary>True when the blocked origin already sits inside the current allowlist — the
        /// shared matcher decides, with the same wildcard SSRF floor the gates apply (a wildcard
        /// entry that could never serve this origin does not annotate the row, and the promotion
        /// button stays: an exact entry is precisely what a floored origin needs).</summary>
        private static bool IsCoveredByAllowlist(string origin, IEnumerable<string> allowlistEntries, IEnumerable<string> vmsFloorHosts)
        {
            if (allowlistEntries == null)
            {
                return false;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !ProxyPolicy.IsHttpUri(uri))
            {
                return false;
            }

            return ProxyPolicy.IsRequestAllowed(allowlistEntries, uri, vmsFloorHosts);
        }
    }

    /// <summary>
    /// Ticket #84: the recency formatting the Diagnostics tab shows, culture-invariant so the
    /// unit suite can pin the exact strings an admin reads. "just now" inside the minute, then
    /// the same shapes as <see cref="ProxyStatusFormat.Duration"/> with an explicit "ago".
    /// </summary>
    internal static class ProxyDiagnosticsFormat
    {
        internal static string Ago(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            if (span.TotalMinutes < 1)
            {
                var seconds = (int)span.TotalSeconds;
                return seconds < 10 ? "just now" : $"{seconds}s ago";
            }

            return ProxyStatusFormat.Duration(span) + " ago";
        }
    }
}
