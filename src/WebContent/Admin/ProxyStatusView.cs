using System;
using System.Collections.Generic;
using System.Globalization;

namespace WebContent.Admin
{
    /// <summary>
    /// Ticket #76: one rendered row of the Management Client's Status tab — one live proxied
    /// browser-view instance, exactly as the Event Server sees it. Everything here is already a
    /// display string so the WPF grid binds straight through and the formatting itself is unit
    /// tested instead of being left to a StringFormat in XAML.
    /// </summary>
    internal sealed class ProxySessionRowView
    {
        /// <summary>XProtect login name from the messaging sender identity, never a client claim.</summary>
        public string UserName { get; set; }

        /// <summary>Machine name the Smart Client reported for this session (display only).</summary>
        public string ClientMachine { get; set; }

        /// <summary>How long the session has been open.</summary>
        public string Duration { get; set; }

        /// <summary>Quiet time since the last attributed fetch/tunnel traffic.</summary>
        public string IdleFor { get; set; }

        public string Requests { get; set; }

        /// <summary>Requests per minute over the session's own open-to-now window.</summary>
        public string RequestsPerMinute { get; set; }

        public string BytesSent { get; set; }

        public string BytesReceived { get; set; }

        /// <summary>Bitrate computed from the Event Server's own byte counters between two polls.</summary>
        public string SendRate { get; set; }

        public string ReceiveRate { get; set; }
    }

    /// <summary>
    /// Ticket #76: the whole Status tab's content in one pure value: whether the Event Server
    /// answered, the summary line's inputs, and the session rows. The page turns this into text and
    /// grid rows and nothing else, so the entire tab's logic is testable without WPF or MIP.
    /// </summary>
    internal sealed class ProxyStatusView
    {
        /// <summary>False for every non-answer: the page shows the failure, never an empty table.</summary>
        public bool QuerySucceeded { get; set; }

        /// <summary>
        /// Which failure the page must name (a <see cref="ProxyFetchErrorCodes"/> value): an unknown
        /// workspace reads differently from "no live sessions", and an unanswered query differently
        /// from both.
        /// </summary>
        public string FailureCode { get; set; }

        public int SessionCount { get; set; }

        public double TotalRequestsPerMinute { get; set; }

        public double SendBytesPerSecond { get; set; }

        public double ReceiveBytesPerSecond { get; set; }

        public List<ProxySessionRowView> Sessions { get; } = new List<ProxySessionRowView>();
    }

    /// <summary>
    /// Ticket #76: turns the Event Server's cumulative per-session byte counters into a bitrate by
    /// differencing two consecutive snapshots. The counters are cumulative since the session opened,
    /// so a rate only exists between two sightings — a first sighting (or a page reopen after
    /// <see cref="Reset"/>) is honestly zero, never a spike built out of the session's whole life.
    /// Thread-unsafe by design: one Management Client page owns one tracker.
    /// </summary>
    internal sealed class ProxyStatusRateTracker
    {
        private sealed class Sample
        {
            internal long Sent;
            internal long Received;
            internal DateTime AtUtc;
        }

        private readonly Dictionary<string, Sample> _last = new Dictionary<string, Sample>(StringComparer.Ordinal);

        /// <summary>Forget every sighting; what the page does when the tab closes or the item changes.</summary>
        internal void Reset()
        {
            _last.Clear();
        }

        /// <summary>
        /// Bytes per second for one session since it was last seen. Unknown session ids, a snapshot
        /// no later than the stored one, and counters that went backwards (a session id reused after
        /// an Event Server restart) all answer zero rather than inventing a rate.
        /// </summary>
        internal ProxyStatusRates For(ProxySessionStatusEntry entry, DateTime utcNow)
        {
            if (entry == null || string.IsNullOrEmpty(entry.SessionId))
            {
                return ProxyStatusRates.Zero;
            }

            Sample previous;
            _last.TryGetValue(entry.SessionId, out previous);

            var current = new Sample
            {
                Sent = entry.BytesSent < 0 ? 0 : entry.BytesSent,
                Received = entry.BytesReceived < 0 ? 0 : entry.BytesReceived,
                AtUtc = utcNow,
            };
            _last[entry.SessionId] = current;

            if (previous == null)
            {
                return ProxyStatusRates.Zero;
            }

            var elapsed = utcNow - previous.AtUtc;
            if (elapsed <= TimeSpan.Zero)
            {
                return ProxyStatusRates.Zero;
            }

            var sent = current.Sent - previous.Sent;
            var received = current.Received - previous.Received;
            return new ProxyStatusRates
            {
                SendBytesPerSecond = sent <= 0 ? 0d : sent / elapsed.TotalSeconds,
                ReceiveBytesPerSecond = received <= 0 ? 0d : received / elapsed.TotalSeconds,
            };
        }
    }

    /// <summary>Ticket #76: one session's bitrate between two status snapshots.</summary>
    internal sealed class ProxyStatusRates
    {
        internal static readonly ProxyStatusRates Zero = new ProxyStatusRates();

        internal double SendBytesPerSecond;
        internal double ReceiveBytesPerSecond;
    }

    /// <summary>
    /// Ticket #76: the snapshot → view transform. Pure: the timestamps come from the caller, so the
    /// whole tab (aggregate totals, row order, unknown identity placeholders, every failure posture)
    /// is pinned without WPF or MIP.
    /// </summary>
    internal static class ProxyStatusViewBuilder
    {
        /// <summary>Placeholder for a session whose platform identity or machine name never arrived.</summary>
        internal const string UnknownText = "(unknown)";

        internal static ProxyStatusView Build(ProxyStatusResponsePayload payload, ProxyStatusRateTracker rates, DateTime utcNow)
        {
            var view = new ProxyStatusView();
            if (payload == null || !payload.Success)
            {
                view.QuerySucceeded = false;
                view.FailureCode = payload == null || string.IsNullOrWhiteSpace(payload.ErrorCode)
                    ? ProxyFetchErrorCodes.ServerUnavailable
                    : payload.ErrorCode;
                return view;
            }

            view.QuerySucceeded = true;
            if (payload.Sessions == null || payload.Sessions.Count == 0)
            {
                return view;
            }

            foreach (var entry in payload.Sessions)
            {
                if (entry == null)
                {
                    continue;
                }

                var sessionRates = rates != null
                    ? rates.For(entry, utcNow)
                    : ProxyStatusRates.Zero;

                view.Sessions.Add(new ProxySessionRowView
                {
                    UserName = string.IsNullOrEmpty(entry.UserName) ? UnknownText : entry.UserName,
                    ClientMachine = string.IsNullOrEmpty(entry.ClientMachine) ? UnknownText : entry.ClientMachine,
                    Duration = ProxyStatusFormat.Duration(utcNow - entry.OpenedUtc),
                    IdleFor = ProxyStatusFormat.Duration(utcNow - entry.LastActivityUtc),
                    Requests = entry.Requests.ToString(CultureInfo.InvariantCulture),
                    RequestsPerMinute = ProxyStatusFormat.PerMinute(entry.RequestsPerMinute),
                    BytesSent = ProxyStatusFormat.Bytes(entry.BytesSent),
                    BytesReceived = ProxyStatusFormat.Bytes(entry.BytesReceived),
                    SendRate = ProxyStatusFormat.Rate(sessionRates.SendBytesPerSecond),
                    ReceiveRate = ProxyStatusFormat.Rate(sessionRates.ReceiveBytesPerSecond),
                });

                view.TotalRequestsPerMinute += entry.RequestsPerMinute;
                view.SendBytesPerSecond += sessionRates.SendBytesPerSecond;
                view.ReceiveBytesPerSecond += sessionRates.ReceiveBytesPerSecond;
            }

            view.SessionCount = view.Sessions.Count;
            return view;
        }
    }

    /// <summary>
    /// Ticket #76: the tab's number formatting, culture-invariant so the unit suite can pin the
    /// exact strings an admin reads. Binary units (1024), one decimal from KB up.
    /// </summary>
    internal static class ProxyStatusFormat
    {
        internal static string Bytes(long value)
        {
            if (value < 0)
            {
                value = 0;
            }

            if (value < 1024)
            {
                return value.ToString(CultureInfo.InvariantCulture) + " B";
            }

            return Human(value, 1024d, "KB") ?? Human(value, 1024d * 1024d, "MB") ?? Human(value, 1024d * 1024d * 1024d, "GB")
                ?? (value / (1024d * 1024d * 1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " TB";
        }

        internal static string Rate(double bytesPerSecond)
        {
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0)
            {
                return "0 B/s";
            }

            return Bytes((long)Math.Round(bytesPerSecond)) + "/s";
        }

        internal static string PerMinute(double requestsPerMinute)
        {
            if (double.IsNaN(requestsPerMinute) || double.IsInfinity(requestsPerMinute) || requestsPerMinute < 0)
            {
                requestsPerMinute = 0;
            }

            return requestsPerMinute.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>"47s" under a minute, "4m 07s" under an hour, "2h 05m" beyond it.</summary>
        internal static string Duration(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            if (span.TotalHours >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}h {1:00}m", (int)span.TotalHours, span.Minutes);
            }

            if (span.TotalMinutes >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}m {1:00}s", (int)span.TotalMinutes, span.Seconds);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0}s", (int)span.TotalSeconds);
        }

        private static string Human(long value, double unit, string suffix)
        {
            var scaled = value / unit;
            if (scaled < 1 || scaled >= 1024)
            {
                return null;
            }

            return scaled.ToString("0.0", CultureInfo.InvariantCulture) + " " + suffix;
        }
    }
}