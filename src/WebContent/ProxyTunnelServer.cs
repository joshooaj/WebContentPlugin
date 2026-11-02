using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Ticket #57: the Event Server half of the websocket tunnel. Given the client's tunnel-open
    /// (already allowlist-gated twice — client decision table and the ES allowlist union), it
    /// replays the rebuilt origin-form handshake to the target, admits the tunnel only when the
    /// target answers 101, and then pumps raw bytes in both directions until either side closes.
    /// Tunnel bytes never enter the HTTP fetch/chunking path: each direction rides its own
    /// directional message, base64 at the JSON edge only.
    ///
    /// Ticket #60 widened the target schemes: a plain ws:// upgrade tunnels to an http-scheme
    /// target over a raw socket; a wss:// upgrade (an upgrade inside a terminated https tunnel,
    /// or an absolute wss GET) gets a TLS leg — the handshake rides an SslStream the dialer
    /// validates with FULL chain validation unless the target's own allowlist entry carries the
    /// per-target accept-untrusted override (#61, wired through <see cref="_tlsValidationResolver"/>
    /// — scoped to this tunnel's SslStream, never global), and the pumps then carry decrypted
    /// websocket frames.
    ///
    /// Event-Server protection: a hard concurrency cap (a tunnel is a parked socket plus two
    /// pumps), a per-message byte cap, and idle reclamation that also catches half-open tunnels
    /// (one side gone without a clean close). The idle reaper disposes the socket, which breaks
    /// the parked read and unwinds the pumps.
    /// </summary>
    internal sealed class ProxyTunnelServer
    {
        private sealed class Session
        {
            internal string Id;

            /// <summary>#69: the browser-view session this tunnel belongs to (from the open
            /// payload); traffic is attributed to that session's live status. Null on pre-#69
            /// clients, where the traffic callback simply skips.</summary>
            internal string SessionId;

            internal TcpClient Socket;
            internal Stream Stream;
            internal Func<ProxyTunnelPayload, Task> ToClient;
            internal DateTime LastActivityUtc;
            internal bool Closed;

            // Client->target writes are serialized through this queue by one writer pump: two
            // overlapping WriteAsync calls could interleave and corrupt the byte stream.
            internal readonly ConcurrentQueue<byte[]> Outbound = new ConcurrentQueue<byte[]>();
            internal TaskCompletionSource<byte[]> OutboundWaiter;
        }

        private readonly ProxyTunnelLimits _limits;
        private readonly Action<string> _log;

        /// <summary>
        /// Per-target TLS validation override for the https tunnel leg (#61). The Event Server
        /// wires this to the allowlist's per-target accept-untrusted flag: a flagged target gets
        /// the accept-untrusted posture on THIS tunnel's own SslStream, anything else resolves to
        /// null = SslStream's default FULL chain validation. Never a process-wide bypass.
        /// </summary>
        private readonly Func<Uri, RemoteCertificateValidationCallback> _tlsValidationResolver;

        /// <summary>
        /// #69: per-chunk traffic seam - (sessionId, browser→target bytes, target→browser bytes).
        /// The Event Server plugin wires this to the diagnostics aggregator; the server itself
        /// keeps no diagnostics state. Null (tests, unattributed clients) simply means no accounting.
        /// </summary>
        private readonly Action<string, long, long> _traffic;

        private readonly object _sync = new object();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private Timer _reaper;

        internal ProxyTunnelServer(
            ProxyTunnelLimits limits,
            Action<string> log,
            Func<Uri, RemoteCertificateValidationCallback> tlsValidationResolver = null,
            Action<string, long, long> traffic = null)
        {
            _limits = limits ?? ProxyTunnelLimits.Default();
            _log = log ?? (message => { });
            _tlsValidationResolver = tlsValidationResolver;
            _traffic = traffic;
            var period = TimeSpan.FromMilliseconds(Math.Max(1000, Math.Min(30_000, _limits.IdleTimeoutMs / 2)));
            _reaper = new Timer(_ => ReapSafely(), null, period, period);
        }

        internal int ActiveTunnels
        {
            get
            {
                lock (_sync)
                {
                    return _sessions.Count;
                }
            }
        }

        /// <summary>
        /// Open one tunnel: connect, replay the handshake, and on 101 register the session and
        /// start both pumps. <paramref name="toClient"/> is the caller-bound sender for this
        /// tunnel's requester endpoint (unicast; a broadcast would leak another device's frames).
        /// Never throws — every outcome is reported in the returned result.
        /// </summary>
        internal async Task<ProxyTunnelOpenOutcome> OpenAsync(ProxyTunnelPayload open, Func<ProxyTunnelPayload, Task> toClient)
        {
            if (open == null || string.IsNullOrEmpty(open.CorrelationId) || toClient == null)
            {
                return OpenFailure(ProxyFetchErrorCodes.BadRequest, "The tunnel open carried no tunnel id or answer channel.");
            }

            Uri target;
            if (!Uri.TryCreate(open.Url, UriKind.Absolute, out target) || !ProxyPolicy.IsHttpUri(target))
            {
                return OpenFailure(
                    ProxyFetchErrorCodes.BadRequest,
                    $"The tunnel target '{open.Url}' is not an absolute http(s) url; plain ws:// origins tunnel over http, wss:// origins over https.");
            }

            lock (_sync)
            {
                if (_sessions.ContainsKey(open.CorrelationId))
                {
                    return OpenFailure(ProxyFetchErrorCodes.Error, "A tunnel with this id is already active.");
                }

                if (_sessions.Count >= _limits.EsMaxTunnels)
                {
                    return OpenFailure(
                        ProxyFetchErrorCodes.Error,
                        $"The Event Server is at its websocket tunnel limit ({_limits.EsMaxTunnels} concurrent tunnels); close some proxied pages and retry.");
                }
            }

            TcpClient socket = null;
            try
            {
                socket = new TcpClient();
                var connectTask = socket.ConnectAsync(target.IdnHost, target.Port);
                if (await Task.WhenAny(connectTask, Task.Delay(_limits.ConnectTimeoutMs)) != connectTask)
                {
                    CloseSocket(socket);
                    return OpenFailure(ProxyFetchErrorCodes.Unreachable, $"{OriginOf(target)}: no tunnel connection within {_limits.ConnectTimeoutMs} ms.");
                }

                await connectTask;
                Stream stream = socket.GetStream();

                // #60/#61: an https-scheme tunnel target (a wss upgrade) gets a TLS leg with the
                // same per-target posture as the https fetcher: full-chain validation unless THIS
                // target's allowlist entry carries the accept-untrusted override. The rebuilt
                // handshake and every frame byte ride this SslStream; the browser's frames reach
                // it already decrypted by the loopback tunnel termination, so this is the
                // target's real wss leg - exactly the leg ADR-0002 keeps the security decision on.
                if (target.Scheme == Uri.UriSchemeHttps)
                {
                    var ssl = new SslStream(
                        socket.GetStream(),
                        false,
                        _tlsValidationResolver == null ? null : _tlsValidationResolver(target));
                    stream = ssl;

                    var authenticateTask = ssl.AuthenticateAsClientAsync(target.IdnHost);
                    if (await Task.WhenAny(authenticateTask, Task.Delay(_limits.HandshakeTimeoutMs)) != authenticateTask)
                    {
                        CloseSocket(socket);
                        return OpenFailure(
                            ProxyFetchErrorCodes.Timeout,
                            $"{OriginOf(target)}: the TLS handshake for the websocket tunnel did not complete within {_limits.HandshakeTimeoutMs} ms.");
                    }

                    await authenticateTask;
                }

                var handshake = ProxyWebSocketHandshake.BuildTargetHandshake(target, open.Headers);
                var writeTask = stream.WriteAsync(handshake, 0, handshake.Length);
                if (await Task.WhenAny(writeTask, Task.Delay(_limits.ConnectTimeoutMs)) != writeTask)
                {
                    CloseSocket(socket);
                    return OpenFailure(ProxyFetchErrorCodes.Timeout, $"{OriginOf(target)}: the handshake request was not accepted within {_limits.ConnectTimeoutMs} ms.");
                }

                await writeTask;

                var buffer = new byte[_limits.HandshakeMaxBytes];
                var received = 0;
                int headLength;
                while (!ProxyWebSocketHandshake.TryFindHandshakeEnd(buffer, received, out headLength))
                {
                    var readTask = stream.ReadAsync(buffer, received, buffer.Length - received);
                    if (await Task.WhenAny(readTask, Task.Delay(_limits.HandshakeTimeoutMs)) != readTask)
                    {
                        CloseSocket(socket);
                        return OpenFailure(ProxyFetchErrorCodes.Timeout, $"{OriginOf(target)}: no websocket handshake answer within {_limits.HandshakeTimeoutMs} ms.");
                    }

                    var read = await readTask;
                    if (read <= 0)
                    {
                        CloseSocket(socket);
                        return OpenFailure(ProxyFetchErrorCodes.HandshakeRefused, $"{OriginOf(target)}: the target closed the connection without answering the websocket handshake.");
                    }

                    received += read;
                    if (received >= buffer.Length)
                    {
                        CloseSocket(socket);
                        return OpenFailure(ProxyFetchErrorCodes.HandshakeRefused, $"{OriginOf(target)}: the websocket handshake head is past the {_limits.HandshakeMaxBytes} byte cap.");
                    }
                }

                if (!ProxyWebSocketHandshake.IsUpgradeAccepted(buffer, headLength))
                {
                    var status = ReadStatusCode(buffer, headLength);
                    CloseSocket(socket);
                    return OpenFailure(
                        ProxyFetchErrorCodes.HandshakeRefused,
                        $"{OriginOf(target)}: the target answered the websocket upgrade with {status}, not 101.",
                        status);
                }

                var session = new Session
                {
                    Id = open.CorrelationId,
                    SessionId = open.SessionId,
                    Socket = socket,
                    Stream = stream,
                    ToClient = toClient,
                    LastActivityUtc = DateTime.UtcNow,
                };

                lock (_sync)
                {
                    _sessions[open.CorrelationId] = session;
                }

                _ = Task.Run(() => PumpClientToTargetAsync(session));
                _ = Task.Run(() => PumpTargetToClientAsync(session));

                // The handshake read may already have buffered target frame bytes past the head:
                // they are the tunnel's first data, relayed before the open outcome is even sent.
                if (received > headLength)
                {
                    var initial = new byte[received - headLength];
                    Buffer.BlockCopy(buffer, headLength, initial, 0, initial.Length);
                    await SendDataAsync(session, initial);
                }

                var handshakeBytes = new byte[headLength];
                Buffer.BlockCopy(buffer, 0, handshakeBytes, 0, headLength);
                return new ProxyTunnelOpenOutcome
                {
                    Success = true,
                    StatusCode = 101,
                    HandshakeBytes = handshakeBytes,
                };
            }
            catch (Exception exception)
            {
                CloseSocket(socket);
                return ClassifyOpenFailure(target, exception);
            }
        }

        /// <summary>
        /// Turns a target-connect or handshake failure into an explicit outcome. A chain refusal
        /// is TlsValidation whether Schannel throws the AuthenticationException directly or wraps
        /// it (live fail-closed runs showed an IOException wrapping), and the message quotes the
        /// AuthenticationException itself so the wrapper never hides the real cause. A socket
        /// failure anywhere in the chain is Unreachable; anything else is the generic Error.
        /// </summary>
        internal static ProxyTunnelOpenOutcome ClassifyOpenFailure(Uri target, Exception exception)
        {
            for (var inner = exception; inner != null; inner = inner.InnerException)
            {
                if (inner is AuthenticationException authenticationException)
                {
                    return TlsValidationFailure(target, authenticationException);
                }

                if (inner is SocketException)
                {
                    return OpenFailure(ProxyFetchErrorCodes.Unreachable, $"{OriginOf(target)}: {exception.Message}");
                }
            }

            return OpenFailure(ProxyFetchErrorCodes.Error, $"{OriginOf(target)}: {exception.Message}");
        }

        /// <summary>Relay client bytes toward the target. Unknown ids (closed/timed-out tunnels) are logged, not thrown.</summary>
        internal void OnClientData(ProxyTunnelPayload frame)
        {
            var session = Find(frame?.CorrelationId);
            if (session == null)
            {
                return;
            }

            byte[] bytes;
            try
            {
                bytes = string.IsNullOrEmpty(frame.DataBase64) ? new byte[0] : Convert.FromBase64String(frame.DataBase64);
            }
            catch (FormatException)
            {
                _log($"Tunnel {session.Id} data frame was not valid base64; the tunnel is closed.");
                CloseSession(session, "the tunnel data frame was malformed", notifyClient: true);
                return;
            }

            if (bytes.Length > _limits.FrameMaxBytes)
            {
                _log($"Tunnel {session.Id} data frame ({bytes.Length} bytes) is past the {_limits.FrameMaxBytes} byte tunnel-frame cap; the tunnel is closed.");
                CloseSession(session, "a tunnel data frame exceeded the frame cap", notifyClient: true);
                return;
            }

            if (bytes.Length == 0)
            {
                return;
            }

            session.LastActivityUtc = DateTime.UtcNow;
            _traffic?.Invoke(session.SessionId, bytes.Length, 0);
            lock (_sync)
            {
                if (session.Closed)
                {
                    return;
                }

                session.Outbound.Enqueue(bytes);
                var waiter = session.OutboundWaiter;
                session.OutboundWaiter = null;
                waiter?.TrySetResult(bytes);
            }
        }

        /// <summary>The client side ended the tunnel: drop it without echoing a close back.</summary>
        internal void OnClientClose(ProxyTunnelPayload frame)
        {
            var session = Find(frame?.CorrelationId);
            if (session != null)
            {
                CloseSession(session, frame.ErrorMessage ?? "the browser side closed the websocket", notifyClient: false);
            }
        }

        private void ReapSafely()
        {
            // An escaping exception on a Timer thread is an unhandled exception in the
            // Event Server process - the sweep must swallow whatever a dying tunnel throws.
            try
            {
                ReclaimIdle();
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _log("ProxyTunnelServer: idle sweep faulted: " + ex.Message);
            }
        }

        /// <summary>Reclaim tunnels with no traffic in either direction for IdleTimeoutMs.</summary>
        internal void ReclaimIdle()
        {
            List<Session> stale = null;
            var cutoff = DateTime.UtcNow.AddMilliseconds(-_limits.IdleTimeoutMs);
            lock (_sync)
            {
                if (_sessions.Count == 0)
                {
                    return;
                }

                foreach (var session in _sessions.Values)
                {
                    if (!session.Closed && session.LastActivityUtc <= cutoff)
                    {
                        if (stale == null)
                        {
                            stale = new List<Session>();
                        }

                        stale.Add(session);
                    }
                }
            }

            foreach (var session in stale ?? Enumerable.Empty<Session>())
            {
                _log($"Tunnel {session.Id} idle past {_limits.IdleTimeoutMs} ms; reclaimed.");
                CloseSession(session, "the tunnel was idle past the proxy's reclaim timeout", notifyClient: true);
            }
        }

        /// <summary>Drop every tunnel (plugin teardown). The messaging session dies alongside, so no close frames are sent.</summary>
        internal void CloseAll()
        {
            List<Session> all;
            lock (_sync)
            {
                all = new List<Session>(_sessions.Values);
            }

            foreach (var session in all)
            {
                CloseSession(session, "the proxy fetcher stopped", notifyClient: false);
            }
        }

        internal void Dispose()
        {
            var reaper = _reaper;
            _reaper = null;
            reaper?.Dispose();
            CloseAll();
        }

        private async Task PumpClientToTargetAsync(Session session)
        {
            try
            {
                while (true)
                {
                    byte[] bytes;
                    lock (_sync)
                    {
                        if (session.Closed)
                        {
                            return;
                        }

                        if (!session.Outbound.TryDequeue(out bytes))
                        {
                            session.OutboundWaiter = new TaskCompletionSource<byte[]>();
                        }
                    }

                    if (bytes == null)
                    {
                        bytes = await session.OutboundWaiter.Task;
                        if (bytes == null)
                        {
                            return;
                        }

                        lock (_sync)
                        {
                            if (!session.Outbound.TryDequeue(out bytes))
                            {
                                continue;
                            }
                        }
                    }

                    session.LastActivityUtc = DateTime.UtcNow;
                    await session.Stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }
            catch (Exception exception)
            {
                if (!SessionIsClosed(session))
                {
                    _log($"Tunnel {session.Id} target write failed: {exception.Message}");
                    CloseSession(session, "the target connection failed", notifyClient: true);
                }
            }
        }

        private async Task PumpTargetToClientAsync(Session session)
        {
            var buffer = new byte[_limits.FrameMaxBytes];
            try
            {
                while (true)
                {
                    var read = await session.Stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        if (!SessionIsClosed(session))
                        {
                            _log($"Tunnel {session.Id}: the target closed the websocket.");
                            CloseSession(session, "the target closed the websocket", notifyClient: true);
                        }

                        return;
                    }

                    session.LastActivityUtc = DateTime.UtcNow;
                    var bytes = new byte[read];
                    Buffer.BlockCopy(buffer, 0, bytes, 0, read);
                    await SendDataAsync(session, bytes);
                }
            }
            catch (Exception exception)
            {
                // Socket disposal (idle reclaim, client close, teardown) is the designed exit of
                // a parked read; anything else is a real target-side fault.
                if (!SessionIsClosed(session))
                {
                    _log($"Tunnel {session.Id} target read failed: {exception.Message}");
                    CloseSession(session, "the target connection failed", notifyClient: true);
                }
            }
        }

        private async Task SendDataAsync(Session session, byte[] bytes)
        {
            _traffic?.Invoke(session.SessionId, 0, bytes.Length);
            await session.ToClient(new ProxyTunnelPayload
            {
                CorrelationId = session.Id,
                Kind = ProxyTunnelFrameKinds.Data,
                DataBase64 = Convert.ToBase64String(bytes),
            });
        }

        private void CloseSession(Session session, string reason, bool notifyClient)
        {
            lock (_sync)
            {
                if (session.Closed)
                {
                    return;
                }

                session.Closed = true;
                _sessions.Remove(session.Id);

                var waiter = session.OutboundWaiter;
                session.OutboundWaiter = null;
                waiter?.TrySetResult(null);
            }

            // Dispose the stream first: for a TLS leg that sends the close_notify and unwinds the
            // pumps parked on it; then the socket, which is the caller's to own on the plain leg.
            try { session.Stream?.Dispose(); } catch (Exception) { }
            CloseSocket(session.Socket);

            if (notifyClient)
            {
                _ = SendCloseAsync(session, reason);
            }
        }

        private async Task SendCloseAsync(Session session, string reason)
        {
            try
            {
                await session.ToClient(new ProxyTunnelPayload
                {
                    CorrelationId = session.Id,
                    Kind = ProxyTunnelFrameKinds.Close,
                    ErrorMessage = reason,
                });
            }
            catch (Exception exception)
            {
                _log($"Tunnel {session.Id} close notice not sent: {exception.Message}");
            }
        }

        private Session Find(string tunnelId)
        {
            if (string.IsNullOrEmpty(tunnelId))
            {
                return null;
            }

            lock (_sync)
            {
                Session session;
                return _sessions.TryGetValue(tunnelId, out session) && !session.Closed ? session : null;
            }
        }

        private bool SessionIsClosed(Session session)
        {
            lock (_sync)
            {
                return session.Closed;
            }
        }

        private static void CloseSocket(TcpClient socket)
        {
            try
            {
                socket?.Close();
            }
            catch (Exception)
            {
            }
        }

        private static int ReadStatusCode(byte[] buffer, int headLength)
        {
            var statusLine = Encoding.UTF8.GetString(buffer, 0, headLength).Split('\n')[0].Trim();
            int code;
            return int.TryParse(statusLine.Split(' ').ElementAtOrDefault(1), out code) ? code : 0;
        }

        private static string OriginOf(Uri target) => ProxyPolicy.CanonicalOrigin(target);

        /// <summary>
        /// #60 AC: an unvalidatable TLS target fails with an explicit TLS-validation error
        /// naming the target origin and the failing side (the Event Server's leg). Quoting the
        /// authentication cause keeps the Schannel-wrapped case as informative as the raw one.
        /// </summary>
        private static ProxyTunnelOpenOutcome TlsValidationFailure(Uri target, Exception cause) =>
            OpenFailure(
                ProxyFetchErrorCodes.TlsValidation,
                $"{OriginOf(target)}: the Event Server refused the target's TLS certificate on the websocket tunnel leg: {cause.Message}");

        private static ProxyTunnelOpenOutcome OpenFailure(string errorCode, string message, int statusCode = 0)
        {
            return new ProxyTunnelOpenOutcome
            {
                Success = false,
                StatusCode = statusCode,
                ErrorCode = errorCode,
                ErrorMessage = message,
            };
        }
    }
}