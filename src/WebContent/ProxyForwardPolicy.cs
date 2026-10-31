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

        /// <summary>Method other than GET (the ES fetcher is GET-only) — 405.</summary>
        MethodNotAllowed,

        /// <summary>CONNECT tunnel (https through a CONNECT-less forwarder) — 502.</summary>
        ConnectRefused,
    }

    /// <summary>
    /// Pure decision layer for the loopback forwarding proxy (#54): every browser request that
    /// reaches the listener — page document, subresources, address-bar navigation alike — goes
    /// through the same table. Allowlisted GETs forward (via ProxyPolicy from the config ticket
    /// #52); everything else is refused with an explicit status, never sent direct.
    /// </summary>
    internal static class ProxyForwardPolicy
    {
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

            if (request.Method != "GET")
            {
                rejectReason = $"The proxy fetcher is GET-only; '{request.Method}' to '{request.RawTarget}' is refused.";
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
