using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Ticket #57: the Event Server half of the websocket tunnel. Given the client's tunnel-open
    /// (already allowlist-gated twice — client decision table and the ES allowlist union), it
    /// replays the rebuilt origin-form handshake over a raw socket to the target, admits the
    /// tunnel only when the target answers 101, and then pumps raw bytes in both directions
    /// until either side closes. Tunnel bytes never enter the HTTP fetch/chunking path: each
    /// direction rides its own directional message, base64 at the JSON edge only.
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
            internal TcpClient Socket;
            internal NetworkStream Stream;
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
        private readonly object _sync = new object();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private Timer _reaper;

        internal ProxyTunnelServer(ProxyTunnelLimits limits, Action<string> log)
        {
            _limits = limits ?? ProxyTunnelLimits.Default();
            _log = log ?? (message => { });
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
            if (!Uri.TryCreate(open.Url, UriKind.Absolute, out target) || target.Scheme != Uri.UriSchemeHttp)
            {
                return OpenFailure(ProxyFetchErrorCodes.BadRequest, $"The tunnel target '{open.Url}' is not an absolute http url; only plain ws:// origins (http scheme) are tunnelled.");
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
                var stream = socket.GetStream();

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
            catch (SocketException socketException)
            {
                CloseSocket(socket);
                return OpenFailure(ProxyFetchErrorCodes.Unreachable, $"{OriginOf(target)}: {socketException.Message}");
            }
            catch (Exception exception)
            {
                CloseSocket(socket);
                for (var inner = exception; inner != null; inner = inner.InnerException)
                {
                    if (inner is SocketException)
                    {
                        return OpenFailure(ProxyFetchErrorCodes.Unreachable, $"{OriginOf(target)}: {exception.Message}");
                    }
                }

                return OpenFailure(ProxyFetchErrorCodes.Error, $"{OriginOf(target)}: {exception.Message}");
            }
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

        private static async Task SendDataAsync(Session session, byte[] bytes)
        {
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
