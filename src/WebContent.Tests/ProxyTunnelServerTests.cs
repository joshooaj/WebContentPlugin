using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        public void RefusesNonHttpSchemeTargetsOnTheServerEvenAfterTheClientGate()
        {
            // #60 widened the scheme gate to http(s); anything else stays a fail-closed refusal.
            var server = NewServer();
            var opened = server.OpenAsync(new ProxyTunnelPayload
            {
                CorrelationId = "ftp-tunnel",
                Url = "ftp://somewhere.test:21/ws",
            }, Capture).GetAwaiter().GetResult();

            Assert.False(opened.Success);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, opened.ErrorCode);
        }

        // #60: an https-scheme tunnel target (a wss upgrade inside a terminated TLS tunnel) gets
        // a TLS leg to the target with FULL chain validation by default. The in-process TLS ws
        // target stands in for the vendor device; the test-only validation resolver stands in
        // for the follow-on ticket's accept-untrusted override, never for a production bypass.

        [Fact]
        public async Task OpensTheWssTunnelOverATlsLegAndPumpsThroughIt()
        {
            _target = new TlsWsTarget(upgrade: true);
            var server = new ProxyTunnelServer(TestLimits(), message => { }, uri => (s, c, ch, e) => true);

            var opened = await server.OpenAsync(OpenPayload("/ws/live", scheme: "https"), Capture);

            Assert.True(opened.Success, opened.ErrorMessage);
            Assert.Equal(101, opened.StatusCode);
            Assert.Contains("HTTP/1.1 101", Encoding.UTF8.GetString(opened.HandshakeBytes));

            // The rebuilt handshake rode the TLS leg: the target reads it DECRYPTED.
            await WaitForJournal(_target, "GET /ws/live HTTP/1.1");
            Assert.Contains("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==", _target.LastRequestHead);
            Assert.Contains("Host: 127.0.0.1:" + _target.Port, _target.LastRequestHead);

            // And the byte pumps carry decrypted websocket frames both ways.
            var frame = new byte[] { 0x81, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
            server.OnClientData(new ProxyTunnelPayload
            {
                CorrelationId = _tunnelId,
                DataBase64 = Convert.ToBase64String(frame),
            });

            var echoed = await WaitForDataFrame();
            Assert.Equal(frame, Convert.FromBase64String(echoed.DataBase64));
        }

        [Fact]
        public async Task RefusesTheWssTunnelWhenTheTargetsCertificateCannotBeValidated()
        {
            // Full chain validation is the DEFAULT posture (no callback): a self-signed vendor
            // cert fails, and the message names both the target origin and the failing side.
            _target = new TlsWsTarget(upgrade: true);
            var server = NewServer(); // no validation resolver - production posture

            var opened = await server.OpenAsync(OpenPayload("/ws/live", scheme: "https"), Capture);

            Assert.False(opened.Success);
            Assert.Equal(ProxyFetchErrorCodes.TlsValidation, opened.ErrorCode);
            Assert.Contains("127.0.0.1:" + _target.Port, opened.ErrorMessage);
            Assert.Contains("Event Server", opened.ErrorMessage);
            Assert.Equal(0, server.ActiveTunnels);
        }

        [Fact]
        public void ClassifiesAWrappedAuthenticationExceptionAsTlsValidationQuotingTheCause()
        {
            // Live fail-closed runs show Schannel surfacing a chain refusal as an IOException
            // wrapping the AuthenticationException. The classification must follow the cause, and
            // the message must quote the cause's text, not the wrapper's.
            var cause = new AuthenticationException("The certificate chain was issued by an authority that is not trusted");
            var outcome = ProxyTunnelServer.ClassifyOpenFailure(
                new Uri("https://172.16.128.210:18127"),
                new IOException("The decryption operation failed, see inner exception.", cause));

            Assert.False(outcome.Success);
            Assert.Equal(ProxyFetchErrorCodes.TlsValidation, outcome.ErrorCode);
            Assert.Contains("172.16.128.210:18127", outcome.ErrorMessage);
            Assert.Contains("Event Server", outcome.ErrorMessage);
            Assert.Contains("certificate chain was issued by an authority", outcome.ErrorMessage);
            Assert.DoesNotContain("decryption operation failed", outcome.ErrorMessage);
        }

        [Fact]
        public void ClassifiesARawAuthenticationExceptionAsTlsValidationQuotingItsMessage()
        {
            var outcome = ProxyTunnelServer.ClassifyOpenFailure(
                new Uri("https://172.16.128.210:18127"),
                new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain"));

            Assert.False(outcome.Success);
            Assert.Equal(ProxyFetchErrorCodes.TlsValidation, outcome.ErrorCode);
            Assert.Contains("errors in the certificate chain", outcome.ErrorMessage);
        }

        [Fact]
        public void ClassifiesAWrappedSocketExceptionAsUnreachable()
        {
            var outcome = ProxyTunnelServer.ClassifyOpenFailure(
                new Uri("https://172.16.128.210:18126"),
                new IOException("Unable to read data from the transport connection",
                    new SocketException((int)SocketError.ConnectionRefused)));

            Assert.False(outcome.Success);
            Assert.Equal(ProxyFetchErrorCodes.Unreachable, outcome.ErrorCode);
        }

        [Fact]
        public void ClassifiesAnUnrelatedFailureAsGenericError()
        {
            var outcome = ProxyTunnelServer.ClassifyOpenFailure(
                new Uri("https://172.16.128.210:18126"),
                new InvalidOperationException("something else went wrong"));

            Assert.False(outcome.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, outcome.ErrorCode);
            Assert.Contains("something else went wrong", outcome.ErrorMessage);
        }

        [Fact]
        public async Task RefusesTheWssTunnelWhenTheTargetAnswersSomethingOtherThan101OverTls()
        {
            _target = new TlsWsTarget(upgrade: false);
            var server = new ProxyTunnelServer(TestLimits(), message => { }, uri => (s, c, ch, e) => true);

            var opened = await server.OpenAsync(OpenPayload("/ws/live", scheme: "https"), Capture);

            Assert.False(opened.Success);
            Assert.Equal(ProxyFetchErrorCodes.HandshakeRefused, opened.ErrorCode);
            Assert.Equal(401, opened.StatusCode);
            Assert.Equal(0, server.ActiveTunnels);
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

        private ProxyTunnelPayload OpenPayload(string path, string tunnelId = null, string scheme = "http")
        {
            return new ProxyTunnelPayload
            {
                CorrelationId = tunnelId ?? _tunnelId,
                Url = $"{scheme}://127.0.0.1:{_target.Port}{path}",
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
        private class WsTarget : IDisposable
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

            protected void Note(string entry)
            {
                lock (_journal)
                {
                    _journal.Add(entry);
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

            internal virtual Task ServeAsync(TcpClient client)
            {
                return ServePlainAsync(client);
            }

            private async Task ServePlainAsync(TcpClient client)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    await ServeHandshakeAndEchoAsync(stream);
                }
            }

            /// <summary>Handshake + echo logic over any (possibly decrypted) stream.</summary>
            protected async Task ServeHandshakeAndEchoAsync(Stream stream)
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
                Note(head.ToString());

                if (!_upgrade)
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n"));
                    return;
                }

                await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: Zm9vYmFy\r\n\r\n"));
                if (_closeAfterHandshake)
                {
                    Note("closed after handshake");
                    return;
                }

                var buffer = new byte[4096];
                while (true)
                {
                    var n = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (n <= 0)
                    {
                        Note("connection closed");
                        return;
                    }

                    await stream.WriteAsync(buffer, 0, n);
                }
            }

            public void Dispose()
            {
                try { _live?.Close(); } catch { }
                try { _listener.Stop(); } catch { }
            }
        }

        /// <summary>
        /// #60: the same vendor endpoint behind TLS - what an https-scheme tunnel target looks
        /// like to the Event Server dialer. The certificate is self-signed: default chain
        /// validation MUST refuse it (fail-closed), and only the test-only validation resolver
        /// (standing in for the follow-on ticket's per-target override) admits the pump tests.
        /// </summary>
        private sealed class TlsWsTarget : WsTarget
        {
            private readonly X509Certificate2 _certificate;

            internal TlsWsTarget(bool upgrade) : base(upgrade)
            {
                _certificate = BuildSelfSignedTlsCertificate();
            }

            internal override async Task ServeAsync(TcpClient client)
            {
                using (client)
                using (var ssl = new SslStream(client.GetStream(), false, (s, cert, chain, errors) => true))
                {
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(_certificate, false, System.Security.Authentication.SslProtocols.None, false);
                    }
                    catch (Exception exception)
                    {
                        Note($"TLS handshake failed: {exception.GetType().Name}");
                        return;
                    }

                    await ServeHandshakeAndEchoAsync(ssl);
                }
            }

            private static X509Certificate2 BuildSelfSignedTlsCertificate()
            {
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    "CN=webcontent-tunnel-test",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

                var san = new SubjectAlternativeNameBuilder();
                san.AddIpAddress(IPAddress.Loopback);
                san.AddDnsName("localhost");
                request.CertificateExtensions.Add(san.Build());

                using var created = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(7));

                // Re-import through PFX so SslStream gets a key context Schannel will use.
                return new X509Certificate2(created.Export(X509ContentType.Pfx));
            }
        }
    }
}
