using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>What the loopback forwarding proxy does with one parsed browser request (#54).</summary>
    internal enum ProxyForwardDisposition
    {
        /// <summary>Forward the request to the Event Server fetcher and relay the answer.</summary>
        Forward,

        /// <summary>Malformed proxy request — 400.</summary>
        BadRequest,

        /// <summary>Origin not in the workspace allowlist (or address-bar navigation to it) — 403.</summary>
        OriginNotAllowed,

        /// <summary>Verb outside the browser-flow method set (#55) — 405.</summary>
        MethodNotAllowed,

        /// <summary>CONNECT tunnel (https through a CONNECT-less forwarder) — 502.</summary>
        ConnectRefused,
    }

    /// <summary>
    /// Pure decision layer for the loopback forwarding proxy (#54, write verbs since #55): every
    /// browser request that reaches the listener — page document, subresources, login-form POSTs,
    /// background XHR, address-bar navigation alike — goes through the same table. Allowlisted
    /// requests in the browser-flow method set forward (via ProxyPolicy from the config ticket
    /// #52); everything else is refused with an explicit status, never sent direct.
    /// </summary>
    internal static class ProxyForwardPolicy
    {
        /// <summary>
        /// Verbs the proxy relays (#55): the set a browser page actually generates — document GET,
        /// vendor login POSTs, XHR PUT/DELETE/PATCH, CORS preflight OPTIONS. CONNECT is refused as
        /// a tunnel, not treated as a verb; TRACE and other verbs are refused (405), never relayed.
        /// </summary>
        private static readonly HashSet<string> ForwardedMethods =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "PATCH",
            };

        /// <summary>
        /// True for the verbs the whole proxy chain relays. The Event Server fetcher re-checks
        /// decoded request payloads against this same set, so client and server agree on one table.
        /// </summary>
        internal static bool IsForwardedMethod(string method)
        {
            return !string.IsNullOrWhiteSpace(method) && ForwardedMethods.Contains(method);
        }

        internal static ProxyForwardDisposition Decide(
            IEnumerable<string> canonicalTargets,
            string requestHead,
            out ProxyForwardedRequest request,
            out string rejectReason)
        {
            if (!ProxyForwardCodec.TryParseRequestHead(requestHead, out request, out rejectReason))
            {
                return ProxyForwardDisposition.BadRequest;
            }

            if (request.Method == "CONNECT")
            {
                rejectReason = $"The proxy does not open CONNECT tunnels; https routes like '{request.RawTarget}' cannot be relayed and are never taken direct.";
                return ProxyForwardDisposition.ConnectRefused;
            }

            if (!ForwardedMethods.Contains(request.Method))
            {
                rejectReason = $"The proxy relays browser-flow verbs (GET/HEAD/POST/PUT/DELETE/OPTIONS/PATCH); '{request.Method}' to '{request.RawTarget}' is refused.";
                return ProxyForwardDisposition.MethodNotAllowed;
            }

            if (!ProxyPolicy.IsRequestAllowed(canonicalTargets, request.Uri))
            {
                rejectReason = $"The request origin '{ProxyPolicy.CanonicalOrigin(request.Uri)}' is not in the workspace proxy allowlist.";
                return ProxyForwardDisposition.OriginNotAllowed;
            }

            return ProxyForwardDisposition.Forward;
        }
    }
}
