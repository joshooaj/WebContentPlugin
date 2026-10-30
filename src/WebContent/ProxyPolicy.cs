using System;

namespace WebContent
{
    /// <summary>
    /// Pure policy for the ticket #52 proxy allowlist: canonicalizes admin-entered targets to a
    /// comparable origin string (lower-case scheme and host, explicit port unless it is the
    /// scheme default) and matches request origins against the allowlist. Invalid entries never
    /// enter the allowlist; duplicates and other overlapping entries collapse onto the same
    /// canonical origin, so the allowlist is effectively a set.
    /// </summary>
    internal static class ProxyPolicy
    {
        /// <summary>
        /// Canonicalizes one admin allowlist entry (a scheme/host/port origin, with the bare
        /// host and host:port shorthands taken as https) to "scheme://host[:port]". Returns false
        /// for anything that is not an http(s) origin; paths, queries, and fragments are ignored.
        /// </summary>
        internal static bool TryNormalizeTarget(string entry, out string normalized)
        {
            normalized = null;
            if (!WebContentConfiguration.TryCreateNavigationUri(entry, out var uri))
            {
                return false;
            }

            normalized = CanonicalOrigin(uri);
            return true;
        }

        /// <summary>
        /// The canonical origin form used for both stored allowlist entries and request matching.
        /// </summary>
        internal static string CanonicalOrigin(Uri uri)
        {
            // Uri.Host carries IPv6 hosts already bracketed ("[::1]"), so it needs no re-wrapping.
            var host = uri.Host.ToLowerInvariant();
            var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme.ToLowerInvariant()}://{host}{portPart}";
        }

        /// <summary>
        /// Canonicalizes a stored allowlist entry from its scheme/host/port parts (the shape the
        /// workspace JSON carries). Invalid parts - a non-http scheme, a blank or malformed host,
        /// an out-of-range port - reject the entry instead of poisoning the allowlist. A zero port
        /// means the scheme default.
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

            var ipv6 = hostPart.StartsWith("[", StringComparison.Ordinal) && hostPart.EndsWith("]", StringComparison.Ordinal);
            if (hostPart.IndexOf(':') >= 0 && !ipv6)
            {
                hostPart = $"[{hostPart}]";
                ipv6 = true;
            }

            if (port < 0 || port > 65535)
            {
                return false;
            }

            var schemeDefault = schemePart == Uri.UriSchemeHttp ? 80 : 443;
            var portPart = port == 0 || port == schemeDefault ? string.Empty : $":{port}";
            normalized = $"{schemePart}://{hostPart}{portPart}";
            return true;
        }

        /// <summary>
        /// True when the request origin already appears in the allowlisted canonical origins —
        /// exact origin match, no wildcards, no path or prefix semantics.
        /// </summary>
        internal static bool IsRequestAllowed(System.Collections.Generic.IEnumerable<string> canonicalTargets, Uri requestUri)
        {
            if (canonicalTargets == null || requestUri == null)
            {
                return false;
            }

            var requestOrigin = CanonicalOrigin(requestUri);
            foreach (var target in canonicalTargets)
            {
                if (string.Equals(target, requestOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
