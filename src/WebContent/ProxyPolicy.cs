using System;

namespace WebContent
{
    /// <summary>
    /// Pure policy for the proxy allowlist (#52, wildcards since #73): canonicalizes admin-entered
    /// targets to a comparable origin string (lower-case scheme and host, explicit port unless it is
    /// the scheme default) and matches request origins against the allowlist. Since #73 a host part
    /// may also be a <see cref="HostWildcard"/>-shaped pattern: a leftmost-label wildcard
    /// ("*.foo.bar.com" matches subdomains but never the apex itself) or the lone full-host
    /// escape hatch ("*"). Origin-level only - paths, mid-label wildcards, and everything else
    /// malformed never enter the allowlist; duplicates collapse onto one canonical entry.
    /// <see cref="IsRequestAllowed"/> is THE matcher every gate consumes (#73): the Event Server
    /// fetch/mint gate, the client forward policy, and the leaf-issuance gate - no gate compares
    /// entries by itself, and the SSRF floor (a wildcard never matches a literal IP, "localhost",
    /// or the VMS's own hosts; explicit exact entries still may) lives here, not in any gate or UI.
    /// </summary>
    internal static class ProxyPolicy
    {
        /// <summary>#73: the full-host escape-hatch pattern (matches any host on the entry's scheme).</summary>
        internal const string HostWildcard = "*";

        /// <summary>#73: the leftmost-label wildcard prefix ("*.tail" matches subdomains of tail).</summary>
        internal const string LabelWildcardPrefix = "*.";

        /// <summary>#73: the one loopback/VMS name every wildcard is floored against.</summary>
        internal const string LocalhostName = "localhost";

        /// <summary>
        /// Canonicalizes one admin allowlist entry (a scheme/host/port origin, with the bare
        /// host and host:port shorthands taken as https) to "scheme://host[:port]". Since #73 a
        /// host may also be a wildcard pattern. Returns false for anything that is not a valid
        /// http(s) origin or wildcard origin; paths, queries, and fragments are ignored.
        /// </summary>
        internal static bool TryNormalizeTarget(string entry, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(entry))
            {
                return false;
            }

            // #73: a '*' in the entry is pattern syntax, never something the Uri parser is
            // asked to make sense of, so wildcard lines take the pattern path exclusively.
            if (entry.IndexOf('*') >= 0)
            {
                return TryNormalizeWildcardEntryLine(entry, out normalized);
            }

            if (!WebContentConfiguration.TryCreateNavigationUri(entry, out var uri))
            {
                return false;
            }

            normalized = CanonicalOrigin(uri);
            return true;
        }

        /// <summary>
        /// True for absolute http(s) uris — the scheme gate every proxy-fetch flow (admin harness,
        /// Event Server request handling, transport) applies before touching the network.
        /// </summary>
        internal static bool IsHttpUri(Uri uri)
        {
            return uri != null &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// The canonical origin form used for both stored allowlist entries and request matching.
        /// </summary>
        internal static string CanonicalOrigin(Uri uri)
        {
            // Uri.Host carries IPv6 hosts already bracketed ("[::1]"), so it needs no re-wrapping.
            var host = uri.Host.ToLowerInvariant();
            var port = uri.IsDefaultPort ? 0 : uri.Port;
            return FormatOrigin(uri.Scheme.ToLowerInvariant(), host, port);
        }

        /// <summary>
        /// Canonicalizes a stored allowlist entry from its scheme/pattern/port parts (the shape
        /// the workspace JSON carries since #73; the legacy host part of pre-#73 records feeds
        /// the same path). Invalid parts - a non-http scheme, a blank or malformed host or
        /// pattern, an out-of-range port - reject the entry instead of poisoning the allowlist.
        /// A zero port means the scheme default.
        /// </summary>
        internal static bool TryNormalizeTargetParts(string scheme, string host, int port, out string normalized)
        {
            normalized = null;
            var schemePart = (scheme ?? string.Empty).Trim().ToLowerInvariant();
            if (schemePart != Uri.UriSchemeHttp && schemePart != Uri.UriSchemeHttps)
            {
                return false;
            }

            var hostPart = (host ?? string.Empty).Trim().ToLowerInvariant();
            if (hostPart.Length == 0 || hostPart.IndexOfAny(new[] { ' ', '/', '?', '#', '@' }) >= 0)
            {
                return false;
            }

            // #73: a stored pattern entry ("*" or "*.tail") validates against the wildcard
            // grammar; a '*' anywhere else rejects the whole entry.
            if (hostPart.IndexOf('*') >= 0)
            {
                if (!IsValidWildcardHostPattern(hostPart) || port < 0 || port > 65535)
                {
                    return false;
                }

                normalized = FormatOrigin(schemePart, hostPart, port);
                return true;
            }

            if (hostPart.IndexOf(':') >= 0 &&
                !(hostPart.StartsWith("[", StringComparison.Ordinal) && hostPart.EndsWith("]", StringComparison.Ordinal)))
            {
                hostPart = $"[{hostPart}]";
            }

            if (port < 0 || port > 65535)
            {
                return false;
            }

            normalized = FormatOrigin(schemePart, hostPart, port);
            return true;
        }

        /// <summary>
        /// #73: the wildcard host grammar, exactly: the lone "*" or "*." followed by a valid
        /// multi-part lower-case domain tail. Mid-label wildcards ("foo.*.bar"), "*x", "**",
        /// "*.tail with a bad label", and bare "*." are all invalid entries.
        /// </summary>
        internal static bool IsValidWildcardHostPattern(string host)
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
        // hyphens (hyphens never leading or trailing); no '*' - the only wildcard position is
        // the leftmost label already checked by the caller.
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

        // #73: one typed allowlist line whose host part carries '*' (scheme://*.tail[:port],
        // scheme://*[:port], or the bare shorthands taken as https). Paths are ignored like the
        // exact-origin path ignores them; anything outside the grammar is refused.
        private static bool TryNormalizeWildcardEntryLine(string entry, out string normalized)
        {
            normalized = null;
            var text = entry.Trim().ToLowerInvariant();

            // Scheme first - cutting at '/' before this point would chop "scheme://host" at the
            // "://" itself and leave nothing parseable behind.
            var scheme = Uri.UriSchemeHttps;
            if (text.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                var separator = text.IndexOf("://", StringComparison.Ordinal);
                scheme = text.Substring(0, separator);
                if (scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps)
                {
                    return false;
                }

                text = text.Substring(separator + 3);
            }
            else if (text.IndexOf(':') >= 0 && text.IndexOf('*') > text.IndexOf(':'))
            {
                // A ':' before the wildcard means the admin typed no scheme and no port pattern
                // we recognize ("host-like:*") - refuse rather than guess.
                return false;
            }

            var pathCut = text.IndexOfAny(new[] { '/', '?', '#' });
            if (pathCut >= 0)
            {
                text = text.Substring(0, pathCut);
            }

            var port = 0;
            var lastColon = text.LastIndexOf(':');
            if (lastColon > 0)
            {
                var portPart = text.Substring(lastColon + 1);
                if (IsAllDigits(portPart))
                {
                    if (!int.TryParse(portPart, out port) || port < 0 || port > 65535)
                    {
                        return false;
                    }

                    text = text.Substring(0, lastColon);
                }
            }

            if (!IsValidWildcardHostPattern(text))
            {
                return false;
            }

            normalized = FormatOrigin(scheme, text, port);
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
        /// The one origin formatter everything else funnels through: port 0 or the scheme
        /// default renders without an explicit port.
        /// </summary>
        private static string FormatOrigin(string scheme, string host, int port)
        {
            var schemeDefault = scheme == Uri.UriSchemeHttp ? 80 : 443;
            var portPart = port == 0 || port == schemeDefault ? string.Empty : $":{port}";
            return $"{scheme}://{host}{portPart}";
        }

        /// <summary>
        /// #56: the optional per-entry flag token marking "accept an untrusted TLS certificate for
        /// this target". The flag is stored with the entry, never as a global toggle.
        /// </summary>
        internal const string AcceptUntrustedFlag = "accept-untrusted";

        /// <summary>
        /// Parses one admin proxy-target line (#56): an origin entry plus the optional
        /// <see cref="AcceptUntrustedFlag"/> token, in either order. An unknown token or a second
        /// origin on the line rejects the whole entry — a typo must not silently drop the flag or
        /// smuggle an extra origin past the admin.
        /// </summary>
        internal static bool TryParseTargetEntry(string line, out string normalized, out bool acceptUntrusted)
        {
            normalized = null;
            acceptUntrusted = false;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string originEntry = null;
            foreach (var token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(token, AcceptUntrustedFlag, StringComparison.OrdinalIgnoreCase))
                {
                    acceptUntrusted = true;
                    continue;
                }

                if (originEntry != null)
                {
                    return false;
                }

                originEntry = token;
            }

            if (originEntry == null)
            {
                return false;
            }

            if (!TryNormalizeTarget(originEntry, out normalized))
            {
                normalized = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Renders a stored entry back as the admin-page line that created it (#56): the canonical
        /// origin plus the flag token only when the entry carries the override.
        /// </summary>
        internal static string DescribeTargetEntry(string canonicalOrigin, bool acceptUntrusted)
        {
            return acceptUntrusted ? $"{canonicalOrigin} {AcceptUntrustedFlag}" : canonicalOrigin;
        }

        /// <summary>
        /// True when the request origin already appears in the allowlisted canonical entries —
        /// the exact-match semantics of the pre-#73 world when no entry is a wildcard. Kept as the
        /// two-argument convenience form of the shared matcher (an empty VMS floor: wildcard
        /// entries still never match literal IPs or "localhost").
        /// </summary>
        internal static bool IsRequestAllowed(System.Collections.Generic.IEnumerable<string> canonicalTargets, Uri requestUri)
        {
            return IsRequestAllowed(canonicalTargets, requestUri, null);
        }

        /// <summary>
        /// #73: THE canonical allowlist matcher every gate consumes - the Event Server fetch/mint
        /// gate (<see cref="ProxyServerAllowlist"/>, which is also the leaf-minting gate
        /// <see cref="ProxyLeafIssuer"/> consults) and the client forward policy
        /// (<see cref="ProxyForwardPolicy"/>). Entries are canonical "scheme://pattern[:port]"
        /// strings; a request matches an entry when scheme and port match exactly and the host
        /// part is either the exact host or a wildcard pattern covering it.
        ///
        /// The wildcard SSRF floor lives here, not in any gate or the UI: a wildcard entry never
        /// matches a literal IPv4/IPv6 host, "localhost", or any of the VMS's own host names
        /// <paramref name="vmsFloorHosts"/> carries (from <c>EnvironmentManager</c> in
        /// production). Exact entries may still name those explicitly.
        /// </summary>
        internal static bool IsRequestAllowed(
            System.Collections.Generic.IEnumerable<string> canonicalTargets,
            Uri requestUri,
            System.Collections.Generic.IEnumerable<string> vmsFloorHosts)
        {
            if (canonicalTargets == null || requestUri == null)
            {
                return false;
            }

            var requestScheme = requestUri.Scheme.ToLowerInvariant();
            var requestHost = requestUri.Host.ToLowerInvariant();
            var requestPort = requestUri.IsDefaultPort ? 0 : requestUri.Port;

            foreach (var entry in canonicalTargets)
            {
                if (!TrySplitCanonicalEntry(entry, out var scheme, out var pattern, out var port))
                {
                    continue;
                }

                if (scheme != requestScheme || port != requestPort)
                {
                    continue;
                }

                if (pattern == HostWildcard)
                {
                    if (!IsWildcardFlooredHost(requestHost, vmsFloorHosts))
                    {
                        return true;
                    }

                    continue;
                }

                if (pattern.StartsWith(LabelWildcardPrefix, StringComparison.Ordinal))
                {
                    // "*.tail" matches one-or-more-label subdomains of tail, never the apex:
                    // the request host must end with ".tail" and carry a leftmost label.
                    var tail = pattern.Substring(LabelWildcardPrefix.Length);
                    if (!IsWildcardFlooredHost(requestHost, vmsFloorHosts) &&
                        requestHost.Length > tail.Length + 1 &&
                        requestHost.EndsWith("." + tail, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    continue;
                }

                if (string.Equals(pattern, requestHost, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// #73: splits a canonical entry back into scheme, host pattern (lower-cased; wildcard
        /// patterns validate here so a malformed entry can never smuggle a looser comparison),
        /// and port (0 = scheme default). Entries the canonicalizer could not have produced are
        /// refused, so every comparison downstream is grammar-checked.
        /// </summary>
        internal static bool TrySplitCanonicalEntry(string entry, out string scheme, out string pattern, out int port)
        {
            scheme = null;
            pattern = null;
            port = 0;
            if (string.IsNullOrWhiteSpace(entry))
            {
                return false;
            }

            var text = entry.Trim().ToLowerInvariant();
            var separator = text.IndexOf("://", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            scheme = text.Substring(0, separator);
            if (scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps)
            {
                scheme = null;
                return false;
            }

            var rest = text.Substring(separator + 3);
            if (rest.Length == 0)
            {
                scheme = null;
                return false;
            }

            var lastColon = rest.LastIndexOf(':');
            if (lastColon > 0 && lastColon < rest.Length - 1 && IsAllDigits(rest.Substring(lastColon + 1)))
            {
                if (!int.TryParse(rest.Substring(lastColon + 1), out port) || port < 0 || port > 65535)
                {
                    scheme = null;
                    return false;
                }

                rest = rest.Substring(0, lastColon);
            }

            if (rest.Length == 0 || rest.IndexOf('/') >= 0)
            {
                scheme = null;
                port = 0;
                return false;
            }

            if (rest.IndexOf('*') >= 0)
            {
                if (!IsValidWildcardHostPattern(rest))
                {
                    scheme = null;
                    return false;
                }

                pattern = rest;
                return true;
            }

            pattern = rest;
            return true;
        }

        // #73: the wildcard floor - hosts a pattern entry is never allowed to reach. Literal IP
        // hosts are recognized syntactically (bracketed or colon-bearing IPv6, dotted-quad and
        // all-numeric IPv4 forms) so the answer never depends on DNS or runtime parse quirks.
        private static bool IsWildcardFlooredHost(string host, System.Collections.Generic.IEnumerable<string> vmsFloorHosts)
        {
            if (host == LocalhostName || IsLiteralIpHost(host))
            {
                return true;
            }

            if (vmsFloorHosts != null)
            {
                foreach (var vmsHost in vmsFloorHosts)
                {
                    if (!string.IsNullOrWhiteSpace(vmsHost) &&
                        string.Equals(vmsHost.Trim(), host, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsLiteralIpHost(string host)
        {
            var text = host;
            if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
            {
                text = text.Substring(1, text.Length - 2);
            }

            if (text.IndexOf(':') >= 0)
            {
                return true;
            }

            var isDottedNumeric = text.Length > 0;
            var hasDot = false;
            var allDigits = text.Length > 0;
            foreach (var c in text)
            {
                if (c == '.')
                {
                    hasDot = true;
                }
                else if (c < '0' || c > '9')
                {
                    allDigits = false;
                    break;
                }
            }

            // "127.0.0.1" and the shorthand "3232235777" are both literal-IP forms.
            return allDigits || (hasDot && IsDottedQuad(text));
        }

        private static bool IsDottedQuad(string text)
        {
            var parts = text.Split('.');
            if (parts.Length != 4)
            {
                return false;
            }

            foreach (var part in parts)
            {
                if (!IsAllDigits(part) || part.Length > 3 || int.Parse(part) > 255)
                {
                    return false;
                }
            }

            return true;
        }
    }
}