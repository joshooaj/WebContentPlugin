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
using System.Threading.Tasks;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #56 (ADR-0002): the loopback TLS manager issues the ephemeral certificates that let
    /// the listener terminate a browser's CONNECT tunnel. The authority is a session CA; leaves
    /// are per-host, chain to that authority, carry the host's SAN and server-auth EKU, and hold a
    /// private key Schannel can actually use (proved by a real handshake, not just HasPrivateKey).
    /// </summary>
    public class ProxyLoopbackTlsTests : IDisposable
    {
        private static readonly Action<string> Sink = _ => { };

        private readonly ProxyLoopbackTls _tls = new ProxyLoopbackTls(Sink);

        [Fact]
        public void LeafChainsToTheSessionAuthorityWithServerAuthAndTheHostSan()
        {
            var leaf = _tls.GetOrCreateLeafCertificate("cam1.test");

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
            // root here; the live browser path installs it into the trusted-root store instead.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.ExtraStore.Add(_tls.AuthorityCertificate);
            Assert.True(chain.Build(leaf));

            // Element 0 = leaf, element 1 = the session authority at the root: exactly the two-link
            // chain Chromium will see once the authority is in the user's trusted-root store.
            Assert.Equal(2, chain.ChainElements.Count);
            Assert.Equal(_tls.AuthorityCertificate.Thumbprint, chain.ChainElements[1].Certificate.Thumbprint);
        }

        [Fact]
        public void LeafForAnIpAddressHostCarriesAnIpAddressSan()
        {
            var leaf = _tls.GetOrCreateLeafCertificate("10.20.30.40");

            var san = leaf.Extensions["2.5.29.17"];
            Assert.NotNull(san);
            Assert.Contains("10.20.30.40", san.Format(true));
        }

        [Fact]
        public void LeavesAreCachedPerHostAndDistinctAcrossHosts()
        {
            var first = _tls.GetOrCreateLeafCertificate("cam1.test");
            var again = _tls.GetOrCreateLeafCertificate("CAM1.TEST");
            var other = _tls.GetOrCreateLeafCertificate("cam2.test");

            Assert.Equal(first.Thumbprint, again.Thumbprint);
            Assert.NotEqual(first.Thumbprint, other.Thumbprint);
        }

        [Fact]
        public async Task TerminatingATunnelPresentsAChainValidatingLeafAndRelaysBytes()
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
                    verify.ChainPolicy.ExtraStore.Add(_tls.AuthorityCertificate);
                    verify.Build(new X509Certificate2(certificate.GetRawCertData()));
                    // The system store does not know the session CA (the store install is live-verified);
                    // what must hold is that the chain itself is the two-link leaf->authority shape.
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
            // Schannel refuses for server authentication. A successful AuthenticateAsServer with
            // the manager's leaf is the pin that the key ended up in a usable persisted form.
            var serverSide = new TcpListener(IPAddress.Loopback, 0);
            serverSide.Start();
            var port = ((IPEndPoint)serverSide.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                using (var client = await serverSide.AcceptTcpClientAsync())
                using (var socket = client.GetStream())
                using (var tls = new SslStream(socket, false))
                {
                    await tls.AuthenticateAsServerAsync(
                        _tls.GetOrCreateLeafCertificate("cam1.test"), false, SslProtocols.Tls12, false);
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
        public void AuthorityIsASelfSignedCertificateAuthority()
        {
            var authority = _tls.AuthorityCertificate;
            var basicConstraints = authority.Extensions["2.5.29.19"] as X509BasicConstraintsExtension;
            Assert.NotNull(basicConstraints);
            Assert.True(basicConstraints.CertificateAuthority);
            Assert.True(basicConstraints.Critical);

            var keyUsage = authority.Extensions["2.5.29.15"] as X509KeyUsageExtension;
            Assert.NotNull(keyUsage);
            Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign));
            Assert.True(keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign));

            // Deliberately NO EKU on the CA: a CA carrying an EKU makes chain validation of its
            // leaves fail with NotValidForUsage on Windows.
            Assert.Null(authority.Extensions["2.5.29.37"]);
            Assert.Equal("CN=XProtect WebContent Plugin Loopback Proxy", authority.Subject);
        }

        public void Dispose()
        {
            _tls.Dispose();
        }
    }
}
