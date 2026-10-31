using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #56: the Event Server fetcher performs full TLS chain validation against an https
    /// target by default and refuses an untrusted certificate with an actionable error; only the
    /// per-target allowlist override (never a global or browser-side bypass) makes it accept one.
    /// These pins run the real transport against a local self-signed TLS listener.
    /// </summary>
    public class ProxyFetchTlsDecisionTests : IDisposable
    {
        private readonly TestTlsServer _server = new TestTlsServer();

        [Fact]
        public async Task AnUntrustedTlsTargetIsRefusedWithTlsValidationAndAnActionableMessage()
        {
            var result = await new ProxyHttpFetcher().FetchAsync(
                Get(_server.UrlFor("/index.html")), ProxyFetchLimits.Default());

            Assert.False(result.Success);
            Assert.Equal(ProxyFetchErrorCodes.TlsValidation, result.ErrorCode);
            Assert.Contains("certificate", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("accept untrusted", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ThePerTargetOverrideAcceptsTheUntrustedCertificateForThatFetchOnly()
        {
            var result = await new ProxyHttpFetcher().FetchAsync(
                Get(_server.UrlFor("/index.html")),
                ProxyFetchLimits.Default(),
                ProxyFetchTlsMode.AcceptUntrusted);

            Assert.True(result.Success);
            Assert.Equal(200, result.StatusCode);
            Assert.Equal(Encoding.UTF8.GetBytes("tls probe page"), result.Body);
        }

        [Fact]
        public async Task TheDefaultModeStillFetchesAnOrdinaryHttpTargetUnchanged()
        {
            // Guard that the TLS decision never disturbs the plain-http path that every #53-#55
            // pin runs through: an http target must fetch exactly as before.
            var server = new PlainHttpProbe();
            try
            {
                var result = await new ProxyHttpFetcher().FetchAsync(
                    Get(server.UrlFor("/plain")), ProxyFetchLimits.Default());

                Assert.True(result.Success);
                Assert.Equal(Encoding.UTF8.GetBytes("plain"), result.Body);
            }
            finally
            {
                server.Dispose();
            }
        }

        private static ProxyFetchQuery Get(string url)
        {
            return new ProxyFetchQuery { Uri = new Uri(url), Method = "GET" };
        }

        public void Dispose()
        {
            _server.Dispose();
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
        /// Loopback TLS listener with a self-signed (untrusted-chain) certificate — stands in for
        /// the device web server of the ticket. Serves one fixed page per request head.
        /// </summary>
        private sealed class TestTlsServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly X509Certificate2 _certificate;
            private readonly int _port;

            internal TestTlsServer()
            {
                _certificate = CreateSelfSignedServerCertificate();
                _port = FreePort();
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();

                Task.Run(AcceptLoopAsync);
            }

            internal string UrlFor(string path) => $"https://127.0.0.1:{_port}{path}";

            private async Task AcceptLoopAsync()
            {
                while (true)
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

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (client)
                            using (var stream = client.GetStream())
                            using (var tls = new SslStream(stream, false))
                            {
                                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                                {
                                    ServerCertificate = _certificate,
                                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12,
                                });

                                // Drain the request head, then answer with the fixed probe page.
                                var reader = new byte[8];
                                var head = new StringBuilder();
                                while (true)
                                {
                                    var read = await tls.ReadAsync(reader, 0, reader.Length);
                                    if (read <= 0)
                                    {
                                        return;
                                    }

                                    head.Append(Encoding.ASCII.GetString(reader, 0, read));
                                    if (head.ToString().Contains("\r\n\r\n"))
                                    {
                                        break;
                                    }
                                }

                                var body = Encoding.UTF8.GetBytes("tls probe page");
                                var response =
                                    "HTTP/1.1 200 OK\r\n" +
                                    "Content-Type: text/plain\r\n" +
                                    $"Content-Length: {body.Length}\r\n" +
                                    "\r\n";
                                await tls.WriteAsync(Encoding.ASCII.GetBytes(response), 0, response.Length);
                                await tls.WriteAsync(body, 0, body.Length);
                                await tls.FlushAsync();
                            }
                        }
                        catch (Exception)
                        {
                            // A fetcher that refuses the certificate hangs up mid-handshake: expected.
                        }
                    });
                }
            }

            public void Dispose()
            {
                try
                {
                    _listener.Stop();
                }
                catch
                {
                }
            }
        }

        private static X509Certificate2 CreateSelfSignedServerCertificate()
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=webcontent-proxy-tls-probe", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                var san = new SubjectAlternativeNameBuilder();
                san.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(san.Build());

                var notBefore = DateTime.UtcNow.AddDays(-1);
                var notAfter = DateTime.UtcNow.AddDays(365);
                var selfSigned = request.CreateSelfSigned(notBefore, notAfter);

                // Schannel refuses ephemeral keys for server auth: round-trip through PFX so the
                // private key lands in a persisted, exportable form SslStream can present.
                using (selfSigned)
                {
                    return new X509Certificate2(selfSigned.Export(X509ContentType.Pfx));
                }
            }
        }

        /// <summary>Smallest plain-http stand-in reusing the raw-socket style of the TLS probe.</summary>
        private sealed class PlainHttpProbe : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly int _port;

            internal PlainHttpProbe()
            {
                _port = FreePort();
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                Task.Run(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            using (var client = await _listener.AcceptTcpClientAsync())
                            using (var stream = client.GetStream())
                            {
                                var buffer = new byte[8];
                                var head = new StringBuilder();
                                while (true)
                                {
                                    var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                                    if (read <= 0)
                                    {
                                        break;
                                    }

                                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                                    if (head.ToString().Contains("\r\n\r\n"))
                                    {
                                        break;
                                    }
                                }

                                var body = Encoding.UTF8.GetBytes("plain");
                                var response =
                                    "HTTP/1.1 200 OK\r\n" +
                                    "Content-Type: text/plain\r\n" +
                                    $"Content-Length: {body.Length}\r\n" +
                                    "\r\n";
                                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), 0, response.Length);
                                await stream.WriteAsync(body, 0, body.Length);
                                await stream.FlushAsync();
                            }
                        }
                        catch (Exception)
                        {
                            return;
                        }
                    }
                });
            }

            internal string UrlFor(string path) => $"http://127.0.0.1:{_port}{path}";

            public void Dispose()
            {
                try
                {
                    _listener.Stop();
                }
                catch
                {
                }
            }
        }
    }
}
