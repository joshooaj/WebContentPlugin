using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #57: the Event Server half of the websocket tunnel. It replays the rebuilt
    /// handshake over a raw socket to the target, admits the tunnel only on 101, then pumps raw
    /// bytes in both directions until either side closes — never through the HTTP chunking path.
    /// Idle tunnels are reclaimed and a concurrency cap protects the Event Server. The in-process
    /// TCP target stands in for the vendor device; the messaging layer is host plumbing covered
    /// by the live rows.
    /// </summary>
    public class ProxyTunnelServerTests : IDisposable
    {
        private WsTarget _target;

        public void Dispose() => _target?.Dispose();

        private static ProxyTunnelLimits TestLimits()
        {
            // Test-sized bounds: fast connects, fast handshakes, fast idle reclaim.
            return new ProxyTunnelLimits
            {
                ConnectTimeoutMs = 2000,
                HandshakeTimeoutMs = 1000,
                IdleTimeoutMs = 150,
            };
        }

        [Fact]
        public async Task OpensTheTunnelOnlyWhenTheTargetAcceptsTheUpgrade()
        {
            var server = NewServer();
            var opened = await server.OpenAsync(OpenPayload("/ws/live"), Capture);

            Assert.True(opened.Success, opened.ErrorMessage);
            Assert.Equal(101, opened.StatusCode);
            Assert.Contains("HTTP/1.1 101", Encoding.UTF8.GetString(opened.HandshakeBytes));
            Assert.Equal(1, server.ActiveTunnels);

            // The target saw the rebuilt origin-form handshake with the browser's key intact.
            await WaitForJournal(_target, "GET /ws/live HTTP/1.1");
            Assert.Contains("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", _target.LastRequestHead);
            Assert.Contains("Host: 127.0.0.1:" + _target.Port, _target.LastRequestHead);
        }

        [Fact]
        public async Task PumpsBytesInBothDirectionsUntilEitherSideCloses()
        {
            var server = NewServer();
            var opened = await server.OpenAsync(OpenPayload("/ws/echo"), Capture);
            Assert.True(opened.Success, opened.ErrorMessage);

            // Client -> target -> echo -> client, byte-identical: raw websocket frame bytes,
            // base64 only at the JSON edge, no Response/Chunk double-framing.
            var frame = new byte[] { 0x81, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
            server.OnClientData(new ProxyTunnelPayload
            {
                CorrelationId = _tunnelId,
                DataBase64 = Convert.ToBase64String(frame),
            });

            var echoed = await WaitForDataFrame();
            Assert.Equal(frame, Convert.FromBase64String(echoed.DataBase64));
            Assert.Equal(_tunnelId, echoed.CorrelationId);
        }

        [Fact]
        public async Task RefusesTheTunnelWhenTheTargetAnswersSomethingOtherThan101()
        {
            _target = new WsTarget(upgrade: false);
            var server = NewServer();

            var opened = await server.OpenAsync(OpenPayload("/ws/live"), Capture);

            Assert.False(opened.Success);
            Assert.Equal(ProxyFetchErrorCodes.HandshakeRefused, opened.ErrorCode);
            Assert.Equal(401, opened.StatusCode);
            Assert.Equal(0, server.ActiveTunnels);
        }

        [Fact]
        public async Task RefusesTheTunnelWhenTheTargetConnectionFails()
        {
            // A port with no listener at all: connect failure, mapped like the fetcher's.
            var dead = FreePort();
            var server = NewServer();

            var opened = await server.OpenAsync(new ProxyTunnelPayload
            {
                CorrelationId = "dead-tunnel",
                Url = $"http://127.0.0.1:{dead}/ws",
                Headers = new List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Upgrade", Value = "websocket" },
                    new ProxyFetchHeader { Name = "Connection", Value = "Upgrade" },
                    new ProxyFetchHeader { Name = "Sec-WebSocket-Key", Value = "k==" },
                },
            }, Capture);

            Assert.False(opened.Success);
            Assert.Equal(ProxyFetchErrorCodes.Unreachable, opened.ErrorCode);
        }

        [Fact]
        public void RefusesNonHttpTargetsOnTheServerEvenAfterTheClientGate()
        {
            var server = NewServer();
            var opened = server.OpenAsync(new ProxyTunnelPayload
            {
                CorrelationId = "https-tunnel",
                Url = "https://secure.test:8443/ws",
            }, Capture).GetAwaiter().GetResult();

            Assert.False(opened.Success);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, opened.ErrorCode);
        }

        [Fact]
        public async Task RejectsADuplicateTunnelIdOnTheEventServerGate()
        {
            var server = NewServer();
            Assert.True((await server.OpenAsync(OpenPayload("/a"), Capture)).Success);

            var duplicate = await server.OpenAsync(OpenPayload("/b"), Capture);
            Assert.False(duplicate.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, duplicate.ErrorCode);
            Assert.Contains("already active", duplicate.ErrorMessage);
        }

        [Fact]
        public async Task CapsConcurrentTunnelsToProtectTheEventServer()
        {
            _target = new WsTarget(upgrade: true);
            var limits = TestLimits();
            limits.EsMaxTunnels = 2;
            var server = new ProxyTunnelServer(limits, message => { });

            var first = await server.OpenAsync(OpenPayload("/a", "tunnel-a"), Capture);
            Assert.True(first.Success, first.ErrorMessage);
            var second = await server.OpenAsync(OpenPayload("/b", "tunnel-b"), Capture);
            Assert.True(second.Success, second.ErrorMessage);

            var third = await server.OpenAsync(OpenPayload("/c", "tunnel-c"), Capture);
            Assert.False(third.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, third.ErrorCode);
            Assert.Contains("tunnel limit", third.ErrorMessage);
            Assert.Equal(2, server.ActiveTunnels);
        }

        [Fact]
        public async Task ReclaimsIdleTunnelsAndTellsTheClient()
        {
            var server = NewServer(); // IdleTimeoutMs = 150
            Assert.True((await server.OpenAsync(OpenPayload("/ws"), Capture)).Success);

            await Task.Delay(400);
            server.ReclaimIdle();

            Assert.Equal(0, server.ActiveTunnels);
            var close = await WaitForClose();
            Assert.Equal(_tunnelId, close.CorrelationId);
        }

        [Fact]
        public async Task ReclaimsTheTunnelWhenTheTargetClosesItsSide()
        {
            _target = new WsTarget(upgrade: true, closeAfterHandshake: true);
            var server = NewServer();

            Assert.True((await server.OpenAsync(OpenPayload("/ws"), Capture)).Success);

            var close = await WaitForClose();
            Assert.Equal(_tunnelId, close.CorrelationId);
            Assert.Equal(0, server.ActiveTunnels);
        }

        [Fact]
        public async Task ClientCloseEndsTheTargetConnection()
        {
            var server = NewServer();
            Assert.True((await server.OpenAsync(OpenPayload("/ws"), Capture)).Success);

            server.OnClientClose(new ProxyTunnelPayload { CorrelationId = _tunnelId, ErrorMessage = "browser closed" });

            Assert.Equal(0, server.ActiveTunnels);
            // The target saw the socket drop: its echo read ended (journal records the EOF).
            await WaitForJournal(_target, "connection closed");
        }

        // --- harness -------------------------------------------------------

        private string _tunnelId = "tunnel-" + Guid.NewGuid().ToString("N");
        private readonly List<ProxyTunnelPayload> _toClient = new List<ProxyTunnelPayload>();

        private ProxyTunnelServer NewServer()
        {
            _target ??= new WsTarget(upgrade: true);
            return new ProxyTunnelServer(TestLimits(), message => { });
        }

        private ProxyTunnelPayload OpenPayload(string path, string tunnelId = null)
        {
            return new ProxyTunnelPayload
            {
                CorrelationId = tunnelId ?? _tunnelId,
                Url = $"http://127.0.0.1:{_target.Port}{path}",
                Headers = new List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Upgrade", Value = "websocket" },
                    new ProxyFetchHeader { Name = "Connection", Value = "keep-alive, Upgrade" },
                    new ProxyFetchHeader { Name = "Sec-WebSocket-Version", Value = "13" },
                    new ProxyFetchHeader { Name = "Sec-WebSocket-Key", Value = "dGhlIHNhbXBsZSBub25jZQ==" },
                    new ProxyFetchHeader { Name = "Origin", Value = "http://127.0.0.1" },
                },
            };
        }

        private Task Capture(ProxyTunnelPayload payload)
        {
            lock (_toClient)
            {
                _toClient.Add(payload);
            }

            return Task.CompletedTask;
        }

        private async Task<ProxyTunnelPayload> WaitForDataFrame()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ProxyTunnelPayload frame;
                lock (_toClient)
                {
                    frame = _toClient.FirstOrDefault(p => p.DataBase64 != null);
                }

                if (frame != null)
                {
                    return frame;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("no TunnelData frame reached the client callback");
        }

        private async Task<ProxyTunnelPayload> WaitForClose()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ProxyTunnelPayload close;
                lock (_toClient)
                {
                    close = _toClient.FirstOrDefault(p => p.DataBase64 == null && !string.IsNullOrEmpty(p.ErrorMessage));
                }

                if (close != null)
                {
                    return close;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("no tunnel-close frame reached the client callback");
        }

        private static async Task WaitForJournal(WsTarget target, string needle)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (target.JournalSnapshot().Any(entry => entry.Contains(needle, StringComparison.Ordinal)))
                {
                    return;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"target journal never recorded '{needle}': [{string.Join(" | ", target.JournalSnapshot())}]");
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>
        /// In-process stand-in for a vendor websocket endpoint: answers the handshake with 101
        /// (upgrade) or 401 (refuse), then echoes raw bytes until the socket drops.
        /// </summary>
        private sealed class WsTarget : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly bool _upgrade;
            private readonly bool _closeAfterHandshake;
            private readonly List<string> _journal = new List<string>();
            private TcpClient _live;

            internal int Port { get; }
            internal string LastRequestHead { get; private set; }

            internal WsTarget(bool upgrade, bool closeAfterHandshake = false)
            {
                _upgrade = upgrade;
                _closeAfterHandshake = closeAfterHandshake;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Task.Run(AcceptLoopAsync);
            }

            internal List<string> JournalSnapshot()
            {
                lock (_journal)
                {
                    return new List<string>(_journal);
                }
            }

            private async Task AcceptLoopAsync()
            {
                while (_listener.Server.IsBound)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    _live = client;
                    _ = Task.Run(() => ServeAsync(client));
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (true)
                    {
                        var read = await stream.ReadAsync(one, 0, 1);
                        if (read <= 0)
                        {
                            return;
                        }

                        head.Append((char)one[0]);
                        if (head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }

                    LastRequestHead = head.ToString();
                    lock (_journal)
                    {
                        _journal.Add(head.ToString());
                    }

                    if (!_upgrade)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n"));
                        return;
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: Zm9vYmFy\r\n\r\n"));
                    if (_closeAfterHandshake)
                    {
                        lock (_journal)
                        {
                            _journal.Add("closed after handshake");
                        }

                        return;
                    }

                    var buffer = new byte[4096];
                    while (true)
                    {
                        var n = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (n <= 0)
                        {
                            lock (_journal)
                            {
                                _journal.Add("connection closed");
                            }

                            return;
                        }

                        await stream.WriteAsync(buffer, 0, n);
                    }
                }
            }

            public void Dispose()
            {
                try { _live?.Close(); } catch { }
                try { _listener.Stop(); } catch { }
            }
        }
    }
}
