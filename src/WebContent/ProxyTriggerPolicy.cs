using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// Ticket #65: the matcher half of the "XProtect rule fires on a client HTTP request" feature.
    /// An admin-defined trigger row says: a proxied fetch whose FULL url matches this glob, whose
    /// method matches, and whose RESPONSE status matches, raises one plugin custom event on the
    /// Event Server. This is the trigger grammar - deliberately a SEPARATE grammar from the proxy
    /// allowlist (#73): the allowlist gates origins (scheme+host+port, never paths), while a
    /// trigger pattern names full URLs including path wildcards, and any matching host may be
    /// whatever the allowlist already admitted. Nothing here grants access; it only decides which
    /// already-served fetches become events.
    ///
    /// The stored shape (a <see cref="ProxyTriggerConfiguration"/> row) is written by the admin
    /// config story (#78); the Event Server reads and normalizes it here. An invalid row never
    /// crashes the store - the matcher reports it, the caller logs it once per refresh, and the
    /// row simply never matches.
    /// </summary>
    internal static class ProxyTriggerPolicy
    {
        /// <summary>
        /// The single fixed event type name this plugin raises (and only ever raises): it rides
        /// <c>EventHeader.Message</c>, which is the field the Event Server matches admin-created
        /// event/alarm definitions against. One event type for every trigger row - per-workspace
        /// and per-row granularity lives in this matcher, not in a growing zoo of event types.
        /// </summary>
        internal const string EventType = "WebContent proxy request";

        internal const string HostWildcard = "*";
        internal const string LabelWildcardPrefix = "*.";

        /// <summary>
        /// One parsed, normalized trigger row: url glob + method filter + status filter, each
        /// already validated. <see cref="Matches"/> is the whole row's verdict for one served
        /// fetch; the raw members ride along for audit lines.
        /// </summary>
        internal sealed class TriggerRow
        {
            internal string RawUrl;
            internal string RawMethod;
            internal string RawStatus;

            // Url parts: scheme is "http"/"https"/"*"; host is exact-lower, "*.tail", or "*";
            // port 0 means "the url's own scheme default"; pathGlob null means "any path".
            internal string Scheme;
            internal string HostPattern;
            internal int Port;
            internal string PathGlob;
            internal string NormalizedUrl;

            /// <summary>Normalized uppercase method, or null for "any method".</summary>
            internal string Method;

            /// <summary>Exact status code, or 0 when no exact filter is set.</summary>
            internal int StatusExact;

            /// <summary>Leading digit of a class filter ("2xx" → 2), or 0 when no class filter is set.</summary>
            internal int StatusClass;

            /// <summary>
            /// The row's verdict for one served fetch. Only fetch-path traffic with an actual
            /// response status reaches this: a transport failure (status 0) never matches, and a
            /// CONNECT-tunnelled connection never produces a status here at all.
            /// </summary>
            internal bool Matches(Uri url, string method, int statusCode)
            {
                if (url == null || statusCode <= 0 || !MatchesStatus(statusCode))
                {
                    return false;
                }

                if (Method != null && !string.Equals((method ?? "GET").Trim().ToUpperInvariant(), Method, StringComparison.Ordinal))
                {
                    return false;
                }

                return MatchesUrl(url);
            }

            internal bool MatchesUrl(Uri url)
            {
                if (url == null || !ProxyPolicy.IsHttpUri(url))
                {
                    return false;
                }

                if (Scheme != HostWildcard &&
                    !string.Equals(url.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!MatchesHost(url))
                {
                    return false;
                }

                // Port 0 = "the url's own scheme default": an explicit non-default port in the
                // fetched url (https on 8443) only matches a pattern that names that port.
                if (Port != 0)
                {
                    if (url.Port != Port)
                    {
                        return false;
                    }
                }
                else if (!url.IsDefaultPort)
                {
                    return false;
                }

                if (PathGlob == null)
                {
                    return true;
                }

                return GlobMatches(PathGlob, url.AbsolutePath ?? "/");
            }

            private bool MatchesHost(Uri url)
            {
                var host = url.Host.ToLowerInvariant();

                if (HostPattern == HostWildcard)
                {
                    return true;
                }

                if (HostPattern.StartsWith(LabelWildcardPrefix, StringComparison.Ordinal))
                {
                    var tail = HostPattern.Substring(LabelWildcardPrefix.Length);

                    // Same apex rule as the #73 allowlist matcher: a leftmost-label wildcard
                    // covers one-or-more labels below the tail and never the tail itself.
                    return host.Length > tail.Length + 1 &&
                           host.EndsWith("." + tail, StringComparison.OrdinalIgnoreCase) &&
                           host.IndexOf('.') == host.Length - tail.Length - 1;
                }

                return string.Equals(host, HostPattern, StringComparison.OrdinalIgnoreCase);
            }

            internal bool MatchesStatus(int statusCode)
            {
                if (StatusExact != 0)
                {
                    return statusCode == StatusExact;
                }

                if (StatusClass != 0)
                {
                    return statusCode / 100 == StatusClass;
                }

                return true;
            }
        }

        /// <summary>
        /// Parses and normalizes one trigger url glob. Grammar: <c>scheme://host[:port][/path-glob]</c>
        /// where scheme is http/https/<c>*</c>; host is an exact host, <c>*.tail</c> (leftmost label,
        /// the tail itself never matches), or a lone <c>*</c>; port is an explicit 1-65535 or absent
        /// (the url's own scheme default); the path glob is literal text with <c>*</c> matching any
        /// run (including <c>/</c>) and <c>?</c> matching exactly one character, or absent (any path).
        /// Query/fragment are never part of the grammar - the fetched url is matched on scheme,
        /// host, port, and path only. Scheme and host normalize to lower case; the path glob stays
        /// case-sensitive, matching web convention.
        /// </summary>
        internal static bool TryNormalizeUrlPattern(string urlGlob, out TriggerRow row, out string error)
        {
            row = null;
            error = null;

            var text = (urlGlob ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = "the trigger url pattern is empty";
                return false;
            }

            if (text.IndexOf('#') >= 0)
            {
                error = "the trigger url pattern carries a fragment; patterns match scheme, host, port, and path only";
                return false;
            }

            // The lone "*" is the documented any-scheme, any-host, any-path hatch; everything
            // else must be written absolute.
            if (text == HostWildcard)
            {
                row = new TriggerRow
                {
                    RawUrl = urlGlob,
                    Scheme = HostWildcard,
                    HostPattern = HostWildcard,
                    Port = 0,
                    PathGlob = null,
                    NormalizedUrl = HostWildcard,
                };
                return true;
            }

            var schemeCut = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeCut < 0)
            {
                error = "the trigger url pattern must be absolute (scheme://host...) or the lone '*' hatch";
                return false;
            }

            var scheme = text.Substring(0, schemeCut).Trim().ToLowerInvariant();
            if (scheme != HostWildcard && scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps)
            {
                error = $"the trigger url scheme '{scheme}' is not http, https, or the '*' wildcard";
                return false;
            }

            var rest = text.Substring(schemeCut + 3);
            var pathCut = rest.IndexOf('/');
            var authority = pathCut >= 0 ? rest.Substring(0, pathCut) : rest;
            var pathPart = pathCut >= 0 ? rest.Substring(pathCut) : null;

            if (authority.Length == 0 || authority.IndexOf('@') >= 0)
            {
                error = "the trigger url host is empty or carries credentials";
                return false;
            }

            var hostPart = authority;
            var port = 0;
            var lastColon = authority.LastIndexOf(':');
            if (lastColon > 0)
            {
                var portPart = authority.Substring(lastColon + 1);
                if (IsAllDigits(portPart))
                {
                    if (!int.TryParse(portPart, out port) || port < 1 || port > 65535)
                    {
                        error = $"the trigger url port '{portPart}' is outside 1-65535";
                        return false;
                    }

                    hostPart = authority.Substring(0, lastColon);
                }
            }

            hostPart = hostPart.Trim().ToLowerInvariant();
            if (hostPart.Length == 0)
            {
                error = "the trigger url host is empty";
                return false;
            }

            if (hostPart.IndexOf('*') >= 0)
            {
                if (!IsValidWildcardHostPattern(hostPart))
                {
                    error = $"the trigger url host '{hostPart}' is outside the host wildcard grammar (exact, '*.tail', or a lone '*')";
                    return false;
                }
            }
            else
            {
                if (hostPart.IndexOf('?') >= 0 || hostPart.IndexOf(' ') >= 0)
                {
                    error = $"the trigger url host '{hostPart}' carries wildcard or invalid characters";
                    return false;
                }

                if (hostPart[0] == '.' || hostPart[hostPart.Length - 1] == '.' || hostPart.IndexOf("..") >= 0)
                {
                    error = $"the trigger url host '{hostPart}' carries an empty label";
                    return false;
                }
            }

            string pathGlob = null;
            if (!string.IsNullOrEmpty(pathPart))
            {
                // A lone "/" means exactly the root path - distinct from the absent path ("any").
                pathGlob = pathPart;
            }

            row = new TriggerRow
            {
                RawUrl = urlGlob,
                Scheme = scheme,
                HostPattern = hostPart,
                Port = port,
                PathGlob = pathGlob,
                NormalizedUrl = FormatPattern(scheme, hostPart, port, pathGlob),
            };
            return true;
        }

        /// <summary>
        /// Normalizes a method filter: null/blank/"*" means any method; otherwise one uppercase
        /// verb token. A row naming a verb the proxy never relays simply never matches - the
        /// fetch path itself already refuses verbs outside the relayed set.
        /// </summary>
        internal static bool TryNormalizeMethodFilter(string method, out string normalized, out string error)
        {
            normalized = null;
            error = null;

            var text = (method ?? string.Empty).Trim();
            if (text.Length == 0 || text == HostWildcard)
            {
                return true;
            }

            var upper = text.ToUpperInvariant();
            foreach (var c in upper)
            {
                if (c < 'A' || c > 'Z')
                {
                    error = $"the trigger method filter '{method}' is not a single verb token";
                    return false;
                }
            }

            normalized = upper;
            return true;
        }

        /// <summary>
        /// Normalizes a status filter into (exact, class): null/blank means any status; "2xx"
        /// (any case) is a class; a plain 100-599 number is exact. Anything else is refused.
        /// </summary>
        internal static bool TryNormalizeStatusFilter(string status, out int statusExact, out int statusClass, out string error)
        {
            statusExact = 0;
            statusClass = 0;
            error = null;

            var text = (status ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return true;
            }

            if (text.Length == 3 && (text[1] == 'x' || text[1] == 'X') && (text[2] == 'x' || text[2] == 'X'))
            {
                var digit = text[0];
                if (digit >= '1' && digit <= '5')
                {
                    statusClass = digit - '0';
                    return true;
                }

                error = $"the trigger status class '{text}' is not one of 1xx..5xx";
                return false;
            }

            int code;
            if (int.TryParse(text, out code) && code >= 100 && code <= 599)
            {
                statusExact = code;
                return true;
            }

            error = $"the trigger status filter '{status}' is neither a class (1xx..5xx) nor an exact code";
            return false;
        }

        /// <summary>
        /// Validates one stored row into a matchable <see cref="TriggerRow"/>; the parts normalize
        /// through the single grammar path and any part's refusal is reported with its reason.
        /// </summary>
        internal static bool TryBuildRow(ProxyTriggerConfiguration trigger, out TriggerRow row, out string error)
        {
            row = null;
            error = null;

            if (trigger == null)
            {
                error = "the trigger row is missing";
                return false;
            }

            TriggerRow parsed;
            string urlError;
            if (!TryNormalizeUrlPattern(trigger.Url, out parsed, out urlError))
            {
                error = urlError;
                return false;
            }

            string method;
            string methodError;
            if (!TryNormalizeMethodFilter(trigger.Method, out method, out methodError))
            {
                error = methodError;
                return false;
            }

            int statusExact;
            int statusClass;
            string statusError;
            if (!TryNormalizeStatusFilter(trigger.Status, out statusExact, out statusClass, out statusError))
            {
                error = statusError;
                return false;
            }

            parsed.RawMethod = trigger.Method;
            parsed.RawStatus = trigger.Status;
            parsed.Method = method;
            parsed.StatusExact = statusExact;
            parsed.StatusClass = statusClass;
            row = parsed;
            return true;
        }

        // The wildcard host grammar mirrors the #73 allowlist shape on purpose - the SAME host
        // wildcard means the same thing everywhere an admin types it - while the URL/path grammar
        // around it is this matcher's own.
        private static bool IsValidWildcardHostPattern(string host)
        {
            if (host == HostWildcard)
            {
                return true;
            }

            if (!host.StartsWith(LabelWildcardPrefix, StringComparison.Ordinal) || host.Length == LabelWildcardPrefix.Length)
            {
                return false;
            }

            return IsValidDomainLabels(host.Substring(LabelWildcardPrefix.Length));
        }

        // Domain label validation for wildcard tails: non-empty labels of letters, digits, and
        // hyphens (hyphens never leading or trailing); no '*' - the only wildcard position is the
        // leftmost label already checked by the caller.
        private static bool IsValidDomainLabels(string host)
        {
            if (string.IsNullOrEmpty(host) || host[0] == '.' || host[host.Length - 1] == '.' || host.IndexOf("..") >= 0)
            {
                return false;
            }

            foreach (var label in host.Split('.'))
            {
                if (label.Length == 0 || label[0] == '-' || label[label.Length - 1] == '-')
                {
                    return false;
                }

                foreach (var c in label)
                {
                    if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-')
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsAllDigits(string value)
        {
            if (value.Length == 0)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Anchored path glob: '*' matches any run of characters including '/', '?' matches
        /// exactly one. Iterative two-pointer match, so a storm of fetches never turns into a
        /// regex storm inside the Event Server process.
        /// </summary>
        internal static bool GlobMatches(string pattern, string value)
        {
            if (pattern == null)
            {
                return true;
            }

            value = value ?? string.Empty;

            var patternIndex = 0;
            var valueIndex = 0;
            var starIndex = -1;
            var starMatchIndex = 0;

            while (valueIndex < value.Length)
            {
                var patternChar = patternIndex < pattern.Length ? pattern[patternIndex] : '\0';

                if (patternChar == '*')
                {
                    starIndex = patternIndex;
                    starMatchIndex = valueIndex;
                    patternIndex++;
                    continue;
                }

                if (patternIndex < pattern.Length &&
                    (patternChar == '?' || patternChar == value[valueIndex]))
                {
                    patternIndex++;
                    valueIndex++;
                    continue;
                }

                if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    starMatchIndex++;
                    valueIndex = starMatchIndex;
                    continue;
                }

                return false;
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }

            return patternIndex == pattern.Length;
        }

        private static string FormatPattern(string scheme, string host, int port, string pathGlob)
        {
            var portPart = port == 0 ? string.Empty : $":{port}";
            return $"{scheme}://{host}{portPart}{pathGlob ?? string.Empty}";
        }

        /// <summary>Audit/one-line rendering of a matched row for the Event Server log.</summary>
        internal static string Describe(TriggerRow row)
        {
            if (row == null)
            {
                return "(no trigger row)";
            }

            var method = row.Method ?? "any";
            var status = row.StatusExact != 0
                ? row.StatusExact.ToString()
                : (row.StatusClass != 0 ? row.StatusClass + "xx" : "any");
            return $"url={row.NormalizedUrl} method={method} status={status}";
        }
    }
}