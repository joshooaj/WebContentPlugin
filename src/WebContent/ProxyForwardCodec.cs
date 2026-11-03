using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace WebContent
{
    /// <summary>
    /// One proxied request the browser sent to the loopback forwarding proxy (#54), as parsed
    /// from the absolute-form request line plus headers. The body itself is read separately by
    /// <see cref="ProxyForwardConnection"/> per Content-Length (#55) and travels to the fetcher
    /// on <see cref="ProxyFetchQuery"/>.
    /// </summary>
    internal sealed class ProxyForwardedRequest
    {
        internal string Method;

        /// <summary>The request-target exactly as it arrived; CONNECT tunnels carry an authority here.</summary>
        internal string RawTarget;

        /// <summary>
        /// Absolute uri for forwarded requests; for CONNECT (#56) the https origin the tunnel
        /// targets, resolved from the authority — the inner TLS-terminated requests extend it
        /// with paths.
        /// </summary>
        internal Uri Uri;

        /// <summary>Request headers, first value per name, original casing preserved.</summary>
        internal Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Ticket #69: the browser-view session that issued this request, stamped by the listener
        /// before the request crosses the fetch/tunnel seams; relayed on the tunnel-open payload so
        /// the Event Server attributes tunnel traffic to the right session. Null on unrouted paths.
        /// </summary>
        internal string SessionId;
    }

    /// <summary>
    /// Pure HTTP codec for the loopback forwarding proxy (#54): parses the absolute-form request
    /// head Chromium sends to an HTTP proxy, renders a reassembled <see cref="ProxyFetchResult"/>
    /// back onto the socket as a forwarded response, and renders locally-generated refusal
    /// responses. The socket plumbing lives in <c>LoopbackProxyListener</c>; this class never
    /// touches the network so every decision is unit-testable.
    /// </summary>
    internal static class ProxyForwardCodec
    {
        internal static bool TryParseRequestHead(string head, out ProxyForwardedRequest request, out string rejectReason)
        {
            return TryParseRequestHead(head, null, out request, out rejectReason);
        }

        /// <summary>
        /// Parse a request head, optionally inside a TLS tunnel whose origin the caller
        /// terminated (#56). With a tunnel origin the request-target is origin-form
        /// ("GET /path HTTP/1.1") and combines with the tunnel origin — the tunnel IS the origin
        /// the browser believes it is talking to, so the Host header is never believed. Without a
        /// tunnel origin an absolute-form target stays required, exactly as an HTTP proxy request.
        /// </summary>
        internal static bool TryParseRequestHead(string head, Uri tunnelOrigin, out ProxyForwardedRequest request, out string rejectReason)
        {
            request = null;
            rejectReason = null;

            if (string.IsNullOrEmpty(head))
            {
                rejectReason = "The proxy received an empty request head.";
                return false;
            }

            var lines = head.Replace("\n", "\r\n").Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(new[] { ' ' }, 3);
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1", StringComparison.Ordinal))
            {
                rejectReason = "The proxy request line was malformed.";
                return false;
            }

            var parsed = new ProxyForwardedRequest
            {
                Method = requestLine[0].ToUpperInvariant(),
                RawTarget = requestLine[1],
            };

            // CONNECT carries an authority ("host:port"), not a uri. #56 resolves it to the https
            // origin the tunnel would open so the decision layer can allowlist-gate the tunnel
            // termination; a malformed authority is a malformed request.
            Uri uri = null;
            if (parsed.Method == "CONNECT")
            {
                // A CONNECT request-target is strictly an authority - a path or query in it is
                // not something a browser sends and is rejected rather than guessed at.
                if (requestLine[1].IndexOf('/') >= 0 ||
                    !Uri.TryCreate($"https://{requestLine[1]}", UriKind.Absolute, out uri) ||
                    !ProxyPolicy.IsHttpUri(uri))
                {
                    rejectReason = $"The CONNECT target '{requestLine[1]}' was not a host:port authority.";
                    return false;
                }
            }
            else if (tunnelOrigin != null)
            {
                // Inside a terminated tunnel the tunnel IS the origin: origin-form targets
                // resolve against it, and an absolute-form target may only name the tunnel's
                // own origin (never smuggle another through the accepted gate).
                if (!Uri.TryCreate(tunnelOrigin, requestLine[1], out uri) ||
                    !ProxyPolicy.IsHttpUri(uri) ||
                    ProxyPolicy.CanonicalOrigin(uri) != ProxyPolicy.CanonicalOrigin(tunnelOrigin))
                {
                    rejectReason = $"The in-tunnel request target '{requestLine[1]}' does not belong to the tunnel origin '{tunnelOrigin.OriginalString}'.";
                    return false;
                }
            }
            else if (!Uri.TryCreate(requestLine[1], UriKind.Absolute, out uri))
            {
                rejectReason = $"The proxy request target '{requestLine[1]}' was not an absolute uri.";
                return false;
            }
            else if (uri.Scheme == "ws" || uri.Scheme == "wss")
            {
                // RFC 6455 5.1: a websocket upgrade reaches an HTTP proxy with the ABSOLUTE ws://
                // or wss:// target, not the http one. The scheme letter carries no gating meaning
                // here (ws is to http what wss is to https), so rebase the uri onto its transport
                // twin: the origin allowlist, the decision table, and the tunnel handshake all
                // speak http origins, and RawTarget keeps exactly what the wire carried.
                var transportScheme = uri.Scheme == "ws" ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
                if (!Uri.TryCreate(new UriBuilder(uri) { Scheme = transportScheme }.ToString(), UriKind.Absolute, out uri))
                {
                    rejectReason = $"The proxy websocket target '{requestLine[1]}' was not a usable uri.";
                    return false;
                }
            }

            if (uri != null && !ProxyPolicy.IsHttpUri(uri))
            {
                rejectReason = $"The proxy request target '{requestLine[1]}' uses the unsupported scheme '{uri.Scheme}'.";
                return false;
            }

            parsed.Uri = uri;

            for (var line = 1; line < lines.Length; line++)
            {
                if (lines[line].Length == 0)
                {
                    break;
                }

                var colon = lines[line].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var name = lines[line].Substring(0, colon).Trim();
                var value = lines[line].Substring(colon + 1).Trim();
                if (!parsed.Headers.ContainsKey(name))
                {
                    parsed.Headers[name] = value;
                }
            }

            request = parsed;
            return true;
        }

        /// <summary>
        /// Headers that describe the fetcher's own connection (or body framing the framer already
        /// replaced), never the browser's — they must not be relayed onto the loopback socket.
        /// Content-Length is in the set because the proxy restates it from the reassembled body.
        /// </summary>
        private static readonly HashSet<string> RelayBlockedHeaders = BuildRelayBlockedHeaders();

        private static HashSet<string> BuildRelayBlockedHeaders()
        {
            var blocked = new HashSet<string>(ProxyHeaderPolicy.ForbiddenHeaderNames, StringComparer.OrdinalIgnoreCase);
            // Host is NOT relay-blocked: it is the request line's authority the codec parses for
            // policy; the fetcher derives its own from the target uri.
            blocked.Remove("Host");
            return blocked;
        }

        /// <summary>
        /// Browser request headers the fetcher must NOT replay at the target (#55): the hop-by-hop
        /// set plus Host (the fetcher's HttpWebRequest derives it from the uri) and Content-Length
        /// (restated from the relayed body). Everything else — Cookie, Authorization, Content-Type,
        /// Accept-*, custom app verbs' headers — passes through unmodified; the proxy never
        /// injects or inspects credentials.
        /// </summary>
        private static readonly HashSet<string> OutboundRequestBlockedHeaders =
            new HashSet<string>(ProxyHeaderPolicy.ForbiddenHeaderNames, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The request headers to carry on the wire to the Event Server fetcher: the parsed
        /// headers (first value per name) minus the outbound-blocked set, original casing and
        /// values intact.
        /// </summary>
        internal static List<ProxyFetchHeader> ToOutboundRequestHeaders(ProxyForwardedRequest request)
        {
            var outbound = new List<ProxyFetchHeader>();
            foreach (var header in request.Headers)
            {
                if (OutboundRequestBlockedHeaders.Contains(header.Key))
                {
                    continue;
                }

                outbound.Add(new ProxyFetchHeader { Name = header.Key, Value = header.Value });
            }

            return outbound;
        }

        /// <summary>
        /// Render a reassembled fetch outcome (AC3: status code and headers intact) as an HTTP/1.1
        /// response for the browser connection. Body framing is the proxy's own Content-Length;
        /// the connection closes after the response.
        /// </summary>
        internal static byte[] RenderForwardedResponse(ProxyFetchResult result)
        {
            var body = result.Body ?? new byte[0];
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {result.StatusCode} {result.StatusText ?? "OK"}\r\n");

            foreach (var header in result.Headers)
            {
                if (header == null || string.IsNullOrEmpty(header.Name) || RelayBlockedHeaders.Contains(header.Name))
                {
                    continue;
                }

                head.Append($"{header.Name}: {header.Value}\r\n");
            }

            head.Append($"Content-Length: {body.Length}\r\n");
            head.Append("Connection: close\r\n");
            head.Append("\r\n");

            return Combine(Encoding.UTF8.GetBytes(head.ToString()), body);
        }

        /// <summary>
        /// Render a locally-generated refusal page (400/403/405/502/504) the browser can display,
        /// explaining the proxy's decision.
        /// </summary>
        internal static byte[] RenderErrorResponse(int statusCode, string reasonPhrase, string message)
        {
            var safeMessage = WebUtility.HtmlEncode(message ?? string.Empty);
            var bodyBytes = Encoding.UTF8.GetBytes(
                $"<html><head><title>Web Content proxy: {statusCode}</title></head>" +
                $"<body><h2>Web Content proxy: {reasonPhrase}</h2><p>{safeMessage}</p></body></html>");

            var head =
                $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n";

            return Combine(System.Text.Encoding.UTF8.GetBytes(head), bodyBytes);
        }

        private static byte[] Combine(byte[] head, byte[] body)
        {
            var combined = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, combined, 0, head.Length);
            Buffer.BlockCopy(body, 0, combined, head.Length, body.Length);
            return combined;
        }
    }
}
