using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WebContent
{
    /// <summary>
    /// Pure websocket-handshake seam for the #57 CONNECT-style tunnel. A plain ws:// request
    /// reaches the loopback forwarding proxy as an absolute-form GET carrying the RFC 6455
    /// upgrade headers; this class recognizes that request shape, validates the mandatory
    /// client-handshake fields, rebuilds the origin-form handshake the Event Server replays at
    /// the target, and validates the target's 101 response. Nothing here touches a socket — the
    /// byte plumbing lives in <see cref="ProxyForwardConnection"/> (client side) and
    /// <see cref="ProxyTunnelServer"/> (Event Server side).
    /// </summary>
    internal static class ProxyWebSocketHandshake
    {
        /// <summary>The only RFC 6455 client version browsers send and the only one relayed.</summary>
        internal const string SupportedVersion = "13";

        /// <summary>
        /// True when the parsed request is a websocket protocol upgrade: a GET whose Connection
        /// header lists the upgrade token and whose Upgrade header names websocket. Other upgrade
        /// protocols (h2c and friends) are not websockets and stay on the ordinary forward path.
        /// </summary>
        internal static bool IsWebSocketUpgrade(ProxyForwardedRequest request)
        {
            if (request == null || request.Method != "GET")
            {
                return false;
            }

            string connection;
            request.Headers.TryGetValue("Connection", out connection);
            if (!ConnectionCarriesUpgrade(connection))
            {
                return false;
            }

            string upgrade;
            request.Headers.TryGetValue("Upgrade", out upgrade);
            return string.Equals((upgrade ?? string.Empty).Trim(), "websocket", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A tunnel-replayable upgrade needs the mandatory RFC 6455 client fields: a non-blank
        /// Sec-WebSocket-Key and Sec-WebSocket-Version 13. Anything else is a malformed request,
        /// refused with 400 rather than half-tunnelled.
        /// </summary>
        internal static bool HasValidClientHandshake(ProxyForwardedRequest request, out string rejectReason)
        {
            rejectReason = null;

            string key;
            request.Headers.TryGetValue("Sec-WebSocket-Key", out key);
            if (string.IsNullOrWhiteSpace(key))
            {
                rejectReason = "The websocket upgrade carries no Sec-WebSocket-Key; a relayable upgrade needs the full RFC 6455 client handshake.";
                return false;
            }

            string version;
            request.Headers.TryGetValue("Sec-WebSocket-Version", out version);
            if (!string.Equals((version ?? string.Empty).Trim(), SupportedVersion, StringComparison.Ordinal))
            {
                rejectReason = $"The websocket upgrade declares Sec-WebSocket-Version '{version}'; only version {SupportedVersion} is relayed.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Request headers the tunnel handshake must NOT carry to the target: per-hop proxy
        /// headers and Host (restated from the target uri). Unlike the HTTP forward path, Upgrade
        /// and Connection ride along — the whole point of the tunnel is that the target answers
        /// the upgrade — and Sec-WebSocket-Key passes through byte-identical so the target's
        /// Sec-WebSocket-Accept matches what the browser will verify.
        /// </summary>
        private static readonly HashSet<string> HandshakeBlockedHeaders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Host",
                "Proxy-Connection",
                "Proxy-Authenticate",
                "Proxy-Authorization",
                "Content-Length",
                "Transfer-Encoding",
                "TE",
                "Trailer",
                "Keep-Alive",
            };

        /// <summary>
        /// Rebuild the origin-form handshake request the Event Server writes onto its raw socket
        /// to the target: request line against the target's path+query, a Host derived from the
        /// uri (never from the client), the relayed headers, and an explicit "Connection: Upgrade"
        /// / "Upgrade: websocket" pair regardless of how the browser spelled its Connection list.
        /// </summary>
        internal static byte[] BuildTargetHandshake(Uri target, List<ProxyFetchHeader> relayHeaders)
        {
            var pathAndQuery = string.IsNullOrEmpty(target.PathAndQuery) ? "/" : target.PathAndQuery;
            var head = new StringBuilder();
            head.Append($"GET {pathAndQuery} HTTP/1.1\r\n");
            head.Append($"Host: {GetHostHeader(target)}\r\n");

            if (relayHeaders != null)
            {
                foreach (var header in relayHeaders)
                {
                    if (header == null || string.IsNullOrEmpty(header.Name) || header.Value == null)
                    {
                        continue;
                    }

                    // A wire-supplied value with line breaks could inject headers at the target;
                    // the gate lives here, same as the fetcher's own header replay.
                    if (header.Value.IndexOf('\r') >= 0 || header.Value.IndexOf('\n') >= 0 ||
                        header.Name.IndexOf(':') >= 0 || HandshakeBlockedHeaders.Contains(header.Name))
                    {
                        continue;
                    }

                    head.Append($"{header.Name}: {header.Value}\r\n");
                }
            }

            head.Append("Connection: Upgrade\r\n");
            head.Append("Upgrade: websocket\r\n");
            head.Append("\r\n");
            return Encoding.UTF8.GetBytes(head.ToString());
        }

        /// <summary>
        /// The headers to carry on the tunnel-open wire message: the browser's parsed headers
        /// minus the hop-by-hop/proxy set. Casing and values are untouched.
        /// </summary>
        internal static List<ProxyFetchHeader> ToTunnelWireHeaders(ProxyForwardedRequest request)
        {
            var outbound = new List<ProxyFetchHeader>();
            foreach (var header in request.Headers)
            {
                if (HandshakeBlockedHeaders.Contains(header.Key))
                {
                    continue;
                }

                outbound.Add(new ProxyFetchHeader { Name = header.Key, Value = header.Value });
            }

            return outbound;
        }

        /// <summary>
        /// The verdict on the target's handshake response bytes: complete (with status), still
        /// incomplete, or overrun. The head terminator is CRLFCRLF (bare-LF tolerated), matching
        /// the request-head reader's rules.
        /// </summary>
        internal static bool TryFindHandshakeEnd(byte[] buffer, int received, out int headLength)
        {
            headLength = -1;
            for (var i = 0; i + 1 < received; i++)
            {
                if (buffer[i] == '\n' && buffer[i + 1] == '\n')
                {
                    headLength = i + 2;
                    return true;
                }

                if (i + 3 < received &&
                    buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    headLength = i + 4;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the handshake response status line is HTTP/1.1 (or HTTP/1.0) 101 — the only
        /// answer that admits the tunnel. Any other status (a plain 200 on the ws path, a vendor's
        /// 401) means the target refused the upgrade.
        /// </summary>
        internal static bool IsUpgradeAccepted(byte[] headBytes, int headLength)
        {
            var head = Encoding.UTF8.GetString(headBytes, 0, Math.Min(headLength, headBytes.Length));
            var firstLine = head.Split('\n')[0].Trim();
            var parts = firstLine.Split(' ');
            return parts.Length >= 2 &&
                parts[0].StartsWith("HTTP/1", StringComparison.Ordinal) &&
                parts[1] == "101";
        }

        private static bool ConnectionCarriesUpgrade(string connection)
        {
            if (string.IsNullOrEmpty(connection))
            {
                return false;
            }

            // Connection is a comma list ("keep-alive, Upgrade" is what Chromium actually sends
            // for a websocket request through a proxy).
            foreach (var token in connection.Split(','))
            {
                if (string.Equals(token.Trim(), "upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetHostHeader(Uri target)
        {
            // Host header rules: omit the port only for the scheme default. Uri.IsDefaultPort
            // already speaks http/https, which is all the tunnel relays.
            return target.IsDefaultPort ? target.IdnHost : $"{target.IdnHost}:{target.Port}";
        }
    }
}
