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

        /// <summary>
        /// CONNECT to an allowlisted https origin (#56): the loopback listener accepts the tunnel
        /// and terminates TLS with an ephemeral local certificate (ADR-0002).
        /// </summary>
        ConnectTerminate,

        /// <summary>CONNECT that cannot be terminated here — 502, never taken direct.</summary>
        ConnectRefused,

        /// <summary>
        /// A websocket upgrade to an allowlisted origin (#57 plain ws://, #60 wss:// inside a
        /// terminated TLS tunnel or absolute wss:// against an https entry): carried as a
        /// CONNECT-style byte tunnel to the Event Server, never through the HTTP request/response
        /// forward path. An https-scheme tunnel target gets a validated TLS leg at the target.
        /// </summary>
        WebSocketTunnel,

        /// <summary>
        /// CONNECT to an origin whose HTTP twin is allowlisted (#57): live Chromium proxies plain
        /// ws:// as CONNECT + a plaintext websocket handshake inside the tunnel (live repro -
        /// not the absolute GET ws:// form RFC 6455 describes). The listener answers the CONNECT,
        /// then admits the tunnel ONLY when its first bytes are a valid ws:// upgrade head; the
        /// tunnel is closed before any Event Server socket opens for anything else.
        /// </summary>
        ConnectWebSocketTunnel,
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
            return Decide(canonicalTargets, requestHead, null, null, out request, out rejectReason);
        }

        /// <summary>
        /// Decide a request head, optionally one read from inside a TLS tunnel the listener itself
        /// terminated (#56). A tunnel origin makes origin-form targets legal — resolved against
        /// the tunnel — and outlaws a nested CONNECT.
        /// </summary>
        internal static ProxyForwardDisposition Decide(
            IEnumerable<string> canonicalTargets,
            string requestHead,
            Uri tunnelOrigin,
            out ProxyForwardedRequest request,
            out string rejectReason)
        {
            return Decide(canonicalTargets, requestHead, tunnelOrigin, null, out request, out rejectReason);
        }

        /// <summary>
        /// #73: the full decision form - the workspace's canonical allowlist entries (exact
        /// origins or wildcard patterns) plus the VMS host names the wildcard SSRF floor is
        /// applied against. Every origin comparison below is the one shared
        /// <see cref="ProxyPolicy.IsRequestAllowed"/> matcher; this gate keeps no comparison
        /// logic of its own.
        /// </summary>
        internal static ProxyForwardDisposition Decide(
            IEnumerable<string> canonicalTargets,
            string requestHead,
            Uri tunnelOrigin,
            IEnumerable<string> vmsFloorHosts,
            out ProxyForwardedRequest request,
            out string rejectReason)
        {
            if (!ProxyForwardCodec.TryParseRequestHead(requestHead, tunnelOrigin, out request, out rejectReason))
            {
                return ProxyForwardDisposition.BadRequest;
            }

            if (request.Method == "CONNECT")
            {
                if (tunnelOrigin != null)
                {
                    rejectReason = "A CONNECT inside an already-terminated TLS tunnel is refused; the tunnel's origin is the only endpoint.";
                    return ProxyForwardDisposition.MethodNotAllowed;
                }

                // #56: an allowlisted https origin gets the tunnel TERMINATED at this listener
                // (ephemeral local certificate, ADR-0002); anything else is refused with the same
                // origin posture as a plain request, and the browser is never let go direct.
                if (!ProxyPolicy.IsRequestAllowed(canonicalTargets, request.Uri, vmsFloorHosts))
                {
                    // #57 (live repro): plain ws:// reaches the browser proxy as CONNECT too, so
                    // an origin allowlisted as its http twin gets the tunnel accepted - but only
                    // for a plaintext ws handshake, which the connection flow validates before
                    // any target socket is opened. The allowlist entry's scheme IS the posture:
                    // https entries terminate as MITM TLS, http entries may only ride a verified
                    // ws:// byte tunnel.
                    var httpTwin = new UriBuilder(request.Uri) { Scheme = Uri.UriSchemeHttp }.Uri;
                    if (!ProxyPolicy.IsRequestAllowed(canonicalTargets, httpTwin, vmsFloorHosts))
                    {
                        rejectReason = $"The CONNECT target '{ProxyPolicy.CanonicalOrigin(request.Uri)}' is not an allowlisted https proxy origin (nor an http twin for a ws:// CONNECT tunnel); the tunnel is refused and the browser is never taken direct.";
                        return ProxyForwardDisposition.OriginNotAllowed;
                    }

                    return ProxyForwardDisposition.ConnectWebSocketTunnel;
                }

                return ProxyForwardDisposition.ConnectTerminate;
            }

            if (!ForwardedMethods.Contains(request.Method))
            {
                rejectReason = $"The proxy relays browser-flow verbs (GET/HEAD/POST/PUT/DELETE/OPTIONS/PATCH); '{request.Method}' to '{request.RawTarget}' is refused.";
                return ProxyForwardDisposition.MethodNotAllowed;
            }

            // #57/#60: a websocket upgrade is not an HTTP forward. A plain ws:// upgrade
            // (http-scheme origin) to an allowlisted target tunnels as raw bytes over a plain
            // socket; a wss:// upgrade — absolute form or inside a TLS tunnel this listener
            // terminated (#56) — tunnels too, with the Event Server dialing a validated TLS leg
            // to the target. Either way the bytes never ride the chunked fetch path, never direct.
            if (ProxyWebSocketHandshake.IsWebSocketUpgrade(request))
            {
                if (!ProxyWebSocketHandshake.HasValidClientHandshake(request, out rejectReason))
                {
                    return ProxyForwardDisposition.BadRequest;
                }

                if (!ProxyPolicy.IsRequestAllowed(canonicalTargets, request.Uri, vmsFloorHosts))
                {
                    rejectReason = $"The websocket origin '{ProxyPolicy.CanonicalOrigin(request.Uri)}' is not in the workspace proxy allowlist; the tunnel is refused and the browser is never taken direct.";
                    return ProxyForwardDisposition.OriginNotAllowed;
                }

                return ProxyForwardDisposition.WebSocketTunnel;
            }

            if (!ProxyPolicy.IsRequestAllowed(canonicalTargets, request.Uri, vmsFloorHosts))
            {
                rejectReason = $"The request origin '{ProxyPolicy.CanonicalOrigin(request.Uri)}' is not in the workspace proxy allowlist.";
                return ProxyForwardDisposition.OriginNotAllowed;
            }

            return ProxyForwardDisposition.Forward;
        }
    }
}
