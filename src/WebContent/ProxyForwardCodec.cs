using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace WebContent
{
    /// <summary>
    /// One proxied request the browser sent to the loopback forwarding proxy (#54), as parsed
    /// from the absolute-form request line plus headers. Bodies are never read: the ES fetcher
    /// is GET-only, so any method with a body is refused before the body arrives.
    /// </summary>
    internal sealed class ProxyForwardedRequest
    {
        internal string Method;

        /// <summary>The request-target exactly as it arrived; CONNECT tunnels carry an authority here.</summary>
        internal string RawTarget;

        /// <summary>Absolute http(s) uri for forwarded requests; null for CONNECT tunnels.</summary>
        internal Uri Uri;

        /// <summary>Request headers, first value per name, original casing preserved.</summary>
        internal Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

            // CONNECT carries an authority ("host:port"), not a uri. Parse it through so the
            // decision layer can answer a refused tunnel (502) instead of a malformed-request 400.
            Uri uri = null;
            if (parsed.Method != "CONNECT" && !Uri.TryCreate(requestLine[1], UriKind.Absolute, out uri))
            {
                rejectReason = $"The proxy request target '{requestLine[1]}' was not an absolute uri.";
                return false;
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
        private static readonly HashSet<string> RelayBlockedHeaders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Connection",
                "Keep-Alive",
                "Proxy-Authenticate",
                "Proxy-Authorization",
                "Proxy-Connection",
                "TE",
                "Trailer",
                "Transfer-Encoding",
                "Upgrade",
                "Content-Length",
            };

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
