using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #56 (ADR-0003): the Event Server mints the loopback leaf certificates. These tests
    /// pin the leaf-request/leaf-response contract, the minting gate (the workspace allowlist is
    /// the minting policy - fail-closed like the fetcher), and the requester-side correlation for
    /// the single-frame leaf exchange.
    /// </summary>
    public class ProxyLeafContractTests
    {
        private static void Sink(string message) { }

        private static Dictionary<string, string> WorkspaceProperties(string targetsJson)
        {
            return new Dictionary<string, string>
            {
                ["WorkspaceConfig"] =
                    "{\"version\":1,\"layout\":\"1x1\",\"items\":[{\"type\":\"WebContent\",\"position\":0,\"settings\":" +
                    "{\"homeAddress\":\"http://x/\",\"proxyEnabled\":true,\"proxyTargets\":[" + targetsJson + "]}}]}",
            };
        }

        private static string Target(string host, int port) =>
            $"{{\"scheme\":\"https\",\"host\":\"{host}\",\"port\":{port}}}";

        // ---------------- wire codec ----------------

        [Fact]
        public void LeafRequestRoundTripsThroughTheJsonWireCodec()
        {
            var request = new ProxyLeafRequestPayload
            {
                CorrelationId = "abc123",
                Host = "portal.test",
                Port = 8443,
                SpkiBase64 = Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo()),
            };

            var decoded = ProxyFetchWireCodec.LeafRequestFromWire(ProxyFetchWireCodec.ToWire(request));

            Assert.NotNull(decoded);
            Assert.Equal("abc123", decoded.CorrelationId);
            Assert.Equal("portal.test", decoded.Host);
            Assert.Equal(8443, decoded.Port);
            Assert.Equal(request.SpkiBase64, decoded.SpkiBase64);
        }

        [Fact]
        public void LeafResponseRoundTripsThroughTheJsonWireCodec()
        {
            var response = new ProxyLeafResponsePayload
            {
                CorrelationId = "abc123",
                Success = true,
                LeafDerBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                AuthorityDerBase64 = Convert.ToBase64String(new byte[] { 4, 5, 6 }),
                AuthorityThumbprint = "AABBCC",
            };

            var decoded = ProxyFetchWireCodec.LeafResponseFromWire(ProxyFetchWireCodec.ToWire(response));

            Assert.NotNull(decoded);
            Assert.True(decoded.Success);
            Assert.Equal("AABBCC", decoded.AuthorityThumbprint);
            Assert.Null(decoded.ErrorCode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("{\"something\":17}")]
        public void ForeignOrGarbledLeafFramesDecodeToNullNotThrow(object data)
        {
            Assert.Null(ProxyFetchWireCodec.LeafRequestFromWire(data));
            Assert.Null(ProxyFetchWireCodec.LeafResponseFromWire(data));
        }

        // ---------------- Event Server minting gate ----------------

        private static ProxyLeafIssuer IssuerFor(params string[] targetsJson)
        {
            var snapshots = new List<Dictionary<string, string>>();
            foreach (var target in targetsJson)
            {
                snapshots.Add(WorkspaceProperties(target));
            }

            var allowlist = new ProxyServerAllowlist(() => snapshots, Sink);
            return new ProxyLeafIssuer(allowlist, () => Authority.Create(Sink), Sink);
        }

        private static ProxyLeafRequestPayload LeafRequest(string host, int port, byte[] spki = null)
        {
            return new ProxyLeafRequestPayload
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                Host = host,
                Port = port,
                SpkiBase64 = Convert.ToBase64String(spki ?? RSA.Create(2048).ExportSubjectPublicKeyInfo()),
            };
        }

        [Fact]
        public void MintsForAllowlistedOriginSignedForTheClientsPublicKey()
        {
            var issuer = IssuerFor(Target("portal.test", 8443));
            using (var clientKey = RSA.Create(2048))
            {
                var response = issuer.Issue(LeafRequest("portal.test", 8443, clientKey.ExportSubjectPublicKeyInfo()));

                Assert.True(response.Success, response.ErrorMessage);
                var leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(response.LeafDerBase64));
                Assert.Contains("portal.test", leaf.Subject);
                Assert.False(leaf.HasPrivateKey);   // the private half never left the client

                var san = leaf.Extensions["2.5.29.17"];
                Assert.NotNull(san);
                Assert.Contains("portal.test", san.Format(true));

                // The chain head the client must see in its machine trust roots.
                var authority = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(response.AuthorityDerBase64));
                Assert.Contains(ProxyLoopbackTls.AuthorityCommonName, authority.Subject);
                Assert.Equal(authority.Thumbprint, response.AuthorityThumbprint);
                Assert.Equal(authority.Subject, leaf.Issuer);
            }
        }

        [Fact]
        public void RefusesHostsNoProxiedWorkspaceAllowlists()
        {
            var issuer = IssuerFor(Target("portal.test", 8443));

            var wrongHost = issuer.Issue(LeafRequest("evil.example", 8443));
            Assert.False(wrongHost.Success);
            Assert.Equal(ProxyFetchErrorCodes.NotAllowed, wrongHost.ErrorCode);

            var wrongPort = issuer.Issue(LeafRequest("portal.test", 9999));
            Assert.False(wrongPort.Success);
            Assert.Equal(ProxyFetchErrorCodes.NotAllowed, wrongPort.ErrorCode);
        }

        [Fact]
        public void FailsClosedWhenTheAllowlistHasNeverResolved()
        {
            var allowlist = new ProxyServerAllowlist(
                () => throw new InvalidOperationException("configuration lookup failed"),
                Sink);
            var issuer = new ProxyLeafIssuer(allowlist, () => Authority.Create(Sink), Sink);

            var response = issuer.Issue(LeafRequest("portal.test", 443));
            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.NotAllowed, response.ErrorCode);
        }

        [Theory]
        [InlineData("", 443)]
        [InlineData("portal.test", 0)]
        [InlineData("portal.test", -1)]
        [InlineData("portal.test", 70000)]
        public void RejectsMalformedOriginsWithBadRequest(string host, int port)
        {
            var issuer = IssuerFor(Target("portal.test", 443));
            var response = issuer.Issue(LeafRequest(host, port));
            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, response.ErrorCode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("this is not base64!!")]
        [InlineData("aW52YWxpZA==")] // valid base64, garbage bytes: not an SPKI
        public void RejectsUnusableClientPublicKeysWithBadRequest(string spkiBase64)
        {
            var issuer = IssuerFor(Target("portal.test", 443));
            var request = LeafRequest("portal.test", 443);
            request.SpkiBase64 = spkiBase64;

            var response = issuer.Issue(request);
            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, response.ErrorCode);
        }

        [Fact]
        public void AuthorityUnavailableAnswersNoAuthorityNotATHrow()
        {
            var allowlist = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>> { WorkspaceProperties(Target("portal.test", 443)) },
                Sink);
            var issuer = new ProxyLeafIssuer(
                allowlist,
                () => throw new InvalidOperationException("cannot open the machine certificate store"),
                Sink);

            var response = issuer.Issue(LeafRequest("portal.test", 443));
            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.NoAuthority, response.ErrorCode);
        }

        // ---------------- requester-side correlation ----------------

        [Fact]
        public async Task LeafCorrelationCompletesOnTheSuccessResponse()
        {
            var correlation = new ProxyLeafCorrelation();
            var pending = correlation.Begin("id1", TimeSpan.FromSeconds(30));

            Assert.True(correlation.Feed(new ProxyLeafResponsePayload
            {
                CorrelationId = "id1",
                Success = true,
                LeafDerBase64 = "AAEC",
                AuthorityDerBase64 = "BAAE",
                AuthorityThumbprint = "AA",
            }));

            var result = await pending;
            Assert.True(result.Success);
            Assert.Equal("AA", result.AuthorityThumbprint);
        }

        [Fact]
        public async Task LeafCorrelationCompletesOnTheFailureResponse()
        {
            var correlation = new ProxyLeafCorrelation();
            var pending = correlation.Begin("id2", TimeSpan.FromSeconds(30));

            correlation.Feed(new ProxyLeafResponsePayload
            {
                CorrelationId = "id2",
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.NotAllowed,
                ErrorMessage = "no workspace allowlists this origin",
            });

            var result = await pending;
            Assert.False(result.Success);
            Assert.Equal(ProxyFetchErrorCodes.NotAllowed, result.ErrorCode);
        }

        [Fact]
        public async Task LeafCorrelationCompletesWithTimeoutWhenNoAnswerArrives()
        {
            var correlation = new ProxyLeafCorrelation();
            var pending = correlation.Begin("id3", TimeSpan.FromMilliseconds(50));

            var result = await pending;
            Assert.False(result.Success);
            Assert.Equal(ProxyFetchErrorCodes.Timeout, result.ErrorCode);
        }

        [Fact]
        public void LeafCorrelationIgnoresFramesForUnknownCorrelationIds()
        {
            var correlation = new ProxyLeafCorrelation();
            Assert.False(correlation.Feed(new ProxyLeafResponsePayload { CorrelationId = "nobody-waits" }));
            Assert.False(correlation.Feed(null));
        }
    }
}

