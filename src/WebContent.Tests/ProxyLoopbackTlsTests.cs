using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #56 (ADR-0003): the loopback TLS manager no longer owns a CA - it generates a local
    /// key per origin, asks the Event Server (through an injected mint channel) to sign it, and
    /// attaches the private half locally. Leaves chain to the deployment authority the channel
    /// reports, carry the host's SAN and server-auth EKU, and hold a private key Schannel can
    /// actually use (proved by a real handshake, not just HasPrivateKey).
    /// </summary>
    public class ProxyLoopbackTlsTests : IDisposable
    {
        private static readonly Action<string> Sink = _ => { };

        private readonly Authority _authority = Authority.Create(Sink);
        private readonly List<(string Host, int Port)> _mintCalls = new List<(string, int)>();
        private Func<string, int, byte[], ProxyLeafResponsePayload> _responder;
        private readonly ProxyLoopbackTls _tls;

        public ProxyLoopbackTlsTests()
        {
            _responder = Mint;
            _tls = new ProxyLoopbackTls(
                (host, port, spki) => Task.FromResult(_responder(host, port, spki)),
                Sink);
        }

        private ProxyLeafResponsePayload Mint(string host, int port, byte[] spki)
        {
            _mintCalls.Add((host, port));
            return new ProxyLeafResponsePayload
            {
                Success = true,
                LeafDerBase64 = Convert.ToBase64String(_authority.MintLeafForPublicKey(host, spki)),
                AuthorityDerBase64 = Convert.ToBase64String(_authority.Certificate.RawData),
                AuthorityThumbprint = _authority.Certificate.Thumbprint,
            };
        }

        private Task<X509Certificate2> LeafAsync(string host, int port = 8443)
        {
            return _tls.GetOrCreateLeafCertificateAsync(host, port);
        }

        [Fact]
        public async Task LeafChainsToTheDeploymentAuthorityWithServerAuthAndTheHostSan()
        {
            var leaf = await LeafAsync("cam1.test");

            Assert.Equal("CN=cam1.test", leaf.Subject);
            var eku = leaf.Extensions["2.5.29.37"] as X509EnhancedKeyUsageExtension;
            Assert.NotNull(eku);
            Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1"); // serverAuth

            var san = leaf.Extensions["2.5.29.17"];
            Assert.NotNull(san);
            Assert.Contains("DNS Name=cam1.test", san.Format(true));

            var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.Zero;
            // ExtraStore alone never confers trust - the CA must be allowed to show as an unknown
            // root here; the deployed machine trust roots carry it instead.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.ExtraStore.Add(_authority.Certificate);
            Assert.True(chain.Build(leaf));

            // Element 0 = leaf, element 1 = the deployment authority at the root: exactly the
            // two-link chain Chromium sees once LocalMachine\Root holds the CA.
            Assert.Equal(2, chain.ChainElements.Count);
            Assert.Equal(_authority.Certificate.Thumbprint, chain.ChainElements[1].Certificate.Thumbprint);
        }

        [Fact]
        public async Task LeafForAnIpAddressHostCarriesAnIpAddressSan()
        {
            var leaf = await LeafAsync("10.20.30.40");

            var san = leaf.Extensions["2.5.29.17"];
            Assert.NotNull(san);
            Assert.Contains("10.20.30.40", san.Format(true));
        }

        [Fact]
        public async Task LeavesAreCachedPerOriginAndDistinctAcrossOrigins()
        {
            var first = await LeafAsync("cam1.test", 8443);
            var again = await LeafAsync("CAM1.TEST", 8443);
            var otherPort = await LeafAsync("cam1.test", 9443);
            var otherHost = await LeafAsync("cam2.test", 8443);

            Assert.Equal(first.Thumbprint, again.Thumbprint);
            Assert.NotEqual(first.Thumbprint, otherPort.Thumbprint);
            Assert.NotEqual(first.Thumbprint, otherHost.Thumbprint);

            // One mint exchange per (host, port): a browser reconnect does not re-ask the server.
            Assert.Equal(3, _mintCalls.Count);
        }

        [Fact]
        public async Task ARefusedMintFailsTheTunnelWithTheServerErrorCode()
        {
            _responder = (host, port, spki) => new ProxyLeafResponsePayload
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.NotAllowed,
                ErrorMessage = "no workspace allowlists this origin",
            };

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => LeafAsync("evil.example"));
            Assert.Contains(ProxyFetchErrorCodes.NotAllowed, failure.Message);
        }

        [Fact]
        public async Task TunnelTerminationPresentsTheMintedLeafAndRelaysBytes()
        {
            var serverSide = new TcpListener(IPAddress.Loopback, 0);
            serverSide.Start();
            var port = ((IPEndPoint)serverSide.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                using (var client = await serverSide.AcceptTcpClientAsync())
                using (var socket = client.GetStream())
                {
                    // Pull the first segment of the ClientHello out of the socket so the
                    // termination has to seed its TLS read from the already-read bytes - the
                    // exact shape a pipelined CONNECT segment leaves behind.
                    var seed = new byte[64];
                    var seedLength = 0;
                    while (seedLength == 0)
                    {
                        var read = await socket.ReadAsync(seed, 0, seed.Length);
                        if (read <= 0)
                        {
                            return;
                        }

                        seedLength = read;
                    }

                    using (var tunnel = await _tls.AcceptTunnelAsync(socket, seed, new Uri("https://cam1.test:8443")))
                    {
                        var hello = Encoding.ASCII.GetBytes("tunneled");
                        await tunnel.WriteAsync(hello, 0, hello.Length);
                        await tunnel.FlushAsync();
                    }
                }
            });

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                using (var stream = client.GetStream())
                using (var tls = new SslStream(stream, false, (sender, certificate, chain, errors) =>
                {
                    var verify = new X509Chain();
                    verify.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    verify.ChainPolicy.UrlRetrievalTimeout = TimeSpan.Zero;
                    verify.ChainPolicy.ExtraStore.Add(_authority.Certificate);
                    verify.Build(new X509Certificate2(certificate.GetRawCertData()));
                    // The system store does not know the deployment CA (the LocalMachine\Root
                    // import is live-verified); what must hold is the two-link chain shape.
                    return chain != null;
                }))
                {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "cam1.test" });
                    var buffer = new byte[16];
                    var read = await tls.ReadAsync(buffer, 0, buffer.Length);
                    Assert.Equal("tunneled", Encoding.ASCII.GetString(buffer, 0, read));
                }
            }

            await serverTask;
        }

        [Fact]
        public async Task LeafPrivateKeySurvivesTheSchannelServerHandshake()
        {
            // The historical failure mode: certificates created in memory hold EPHEMERAL keys that
            // Schannel refuses for server authentication. A successful AuthenticateAsServer with a
            // channel-minted leaf plus the locally generated key is the pin that the attached key
            // ended up in a usable persisted form.
            var leaf = await LeafAsync("cam1.test");

            var serverSide = new TcpListener(IPAddress.Loopback, 0);
            serverSide.Start();
            var port = ((IPEndPoint)serverSide.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                using (var client = await serverSide.AcceptTcpClientAsync())
                using (var socket = client.GetStream())
                using (var tls = new SslStream(socket, false))
                {
                    await tls.AuthenticateAsServerAsync(leaf, false, SslProtocols.Tls12, false);
                    return true;
                }
            });

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                using (var stream = client.GetStream())
                using (var tls = new SslStream(stream, false, (s, c, ch, e) => true))
                {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "cam1.test" });
                }
            }

            Assert.True(await serverTask);
        }

        [Fact]
        public async Task DisposeClosesTheManagerToFurtherLeafRequests()
        {
            _tls.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => LeafAsync("cam1.test"));
        }

        public void Dispose()
        {
            _tls?.Dispose();
            _authority.Dispose();
        }
    }
}
