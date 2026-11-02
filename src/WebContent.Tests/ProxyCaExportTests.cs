using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

using WebContent.Admin;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #75: the Event Server half of the deployment CA public-certificate export —
    /// provenance-independent resolve from the LocalMachine\Personal authority store, the
    /// "does not exist yet" refusal posture, and the never-an-empty-file discipline on the
    /// Management Client side. The store itself is never touched: the responder runs against a
    /// fake finder of minted certificates, and source guards pin the read-only store access.
    /// </summary>
    public class ProxyCaExportTests
    {
        // ---------------------------------------------------------------- responses

        [Fact]
        public void AMissingCorrelationIdIsRefusedAsBadRequest()
        {
            var responder = new ProxyCaExportResponder(() => new List<X509Certificate2>(), null);

            var noRequest = responder.Respond(null);
            Assert.False(noRequest.Success);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, noRequest.ErrorCode);

            var noCorrelation = responder.Respond(new ProxyCaExportRequestPayload());
            Assert.False(noCorrelation.Success);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, noCorrelation.ErrorCode);
        }

        [Fact]
        public void NoCertificateYetRefusesWithTheActionableDoesNotExistMessage()
        {
            var responder = new ProxyCaExportResponder(() => new List<X509Certificate2>(), null);

            var response = responder.Respond(new ProxyCaExportRequestPayload { CorrelationId = "c-1" });

            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.NoAuthority, response.ErrorCode);
            Assert.Contains("does not exist yet", response.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("provision-event-server-authority.ps1", response.ErrorMessage, StringComparison.Ordinal);
            Assert.Null(response.AuthorityDerBase64);
        }

        [Fact]
        public void OnlyExpiredCopiesRefuseAsExpiredNotAsMissing()
        {
            using var expired = MintAuthorityCertificate(
                DateTimeOffset.UtcNow.AddYears(-3), DateTimeOffset.UtcNow.AddYears(-1));

            var responder = new ProxyCaExportResponder(
                () => new List<X509Certificate2> { ClonePublic(expired) }, null);

            var response = responder.Respond(new ProxyCaExportRequestPayload { CorrelationId = "c-2" });

            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.NoAuthority, response.ErrorCode);
            Assert.Contains("expired", response.ErrorMessage, StringComparison.Ordinal);
            Assert.Null(response.AuthorityDerBase64);
        }

        [Fact]
        public void AValidCertificateExportsDerThumbprintAndValidityProvenanceIndependently()
        {
            // Provenance-independence: the responder never asks who created the copy — the fake
            // finder hands it a minted certificate exactly as the store read would, and the DER
            // that comes back is byte-identical to what was stored.
            using var ca = MintAuthorityCertificate(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(3650));

            var responder = new ProxyCaExportResponder(
                () => new List<X509Certificate2> { ClonePublic(ca) }, null);

            var response = responder.Respond(new ProxyCaExportRequestPayload { CorrelationId = "c-3" });

            Assert.True(response.Success);
            Assert.Equal("c-3", response.CorrelationId);
            Assert.Equal(Convert.ToBase64String(ca.RawData), response.AuthorityDerBase64);
            Assert.Equal(ca.Thumbprint, response.AuthorityThumbprint);
            var caNotBeforeUtc = ca.NotBefore.ToUniversalTime();
            var caNotAfterUtc = ca.NotAfter.ToUniversalTime();
            Assert.InRange(response.NotBeforeUtc, caNotBeforeUtc.AddMinutes(-1), caNotBeforeUtc.AddMinutes(1));
            Assert.InRange(response.NotAfterUtc, caNotAfterUtc.AddMinutes(-1), caNotAfterUtc.AddMinutes(1));

            // The AC's no-chunking proof: a production-shaped 2048-bit CA frame stays far under
            // the cap, and the cap itself is under the proven single-frame chunk headroom.
            Assert.True(ca.RawData.Length <= ProxyCaExportLimits.MaxAuthorityDerBytes,
                $"CA DER {ca.RawData.Length} bytes exceeds the sanity cap.");
            var wire = ProxyFetchWireCodec.ToWire(response);
            Assert.True(wire.Length <= ProxyCaExportLimits.MaxResponseWireChars,
                $"Export frame {wire.Length} chars exceeds the single-frame cap.");
            var provenChunkHeadroom = (int)Math.Ceiling(ProxyFetchLimits.DefaultChunkSizeBytes / 3d) * 4 + 256;
            Assert.True(ProxyCaExportLimits.MaxResponseWireChars < provenChunkHeadroom,
                "The cap must stay under the proven fetch-chunk single-frame size.");
        }

        [Fact]
        public void ExpiredAndValidCopiesInAnyOrderExportTheValidOne()
        {
            using var expired = MintAuthorityCertificate(
                DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(-1));
            using var fresh = MintAuthorityCertificate(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3650));

            foreach (var order in new[]
            {
                new List<X509Certificate2> { ClonePublic(expired), ClonePublic(fresh) },
                new List<X509Certificate2> { ClonePublic(fresh), ClonePublic(expired) },
            })
            {
                var responder = new ProxyCaExportResponder(() => order, null);

                var response = responder.Respond(new ProxyCaExportRequestPayload { CorrelationId = "c-4" });

                Assert.True(response.Success);
                Assert.Equal(fresh.Thumbprint, response.AuthorityThumbprint);
            }
        }

        [Fact]
        public void ThePrivateKeyCopyOfTheAuthorityIsPreferredWhenBothCopiesExist()
        {
            // Two DIFFERENT valid certificates sit in the store (a re-imported public-only copy
            // and the minting path's copy whose private key lives here). Distinct thumbprints
            // make the preference observable: the live authority must answer, never the shadow.
            using var publicAuthority = MintAuthorityCertificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3650));
            using var keyedAuthority = MintAuthorityCertificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3650));

            foreach (var order in new[]
            {
                new List<X509Certificate2> { ClonePublic(publicAuthority), WithPrivateKey(keyedAuthority) },
                new List<X509Certificate2> { WithPrivateKey(keyedAuthority), ClonePublic(publicAuthority) },
            })
            {
                var responder = new ProxyCaExportResponder(() => order, null);

                var response = responder.Respond(new ProxyCaExportRequestPayload { CorrelationId = "c-5" });

                Assert.True(response.Success);
                Assert.Equal(keyedAuthority.Thumbprint, response.AuthorityThumbprint);
            }
        }

        [Fact]
        public void AStoreFaultAnswersErrorAndNeverThrowsOnTheMessagingThread()
        {
            var responder = new ProxyCaExportResponder(
                () => throw new InvalidOperationException("Access denied to the machine store."), null);

            var response = responder.Respond(new ProxyCaExportRequestPayload { CorrelationId = "c-6" });

            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, response.ErrorCode);
            Assert.Contains("Access denied", response.ErrorMessage, StringComparison.Ordinal);
        }

        [Fact]
        public void AnImplausiblyLargeCertificateIsRefusedNotTransmitted()
        {
            using var bloated = MintAuthorityCertificate(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3650), paddingBytes: 12 * 1024);
            Assert.True(bloated.RawData.Length > ProxyCaExportLimits.MaxAuthorityDerBytes,
                "The fixture must exceed the cap for this test to mean anything.");

            var responder = new ProxyCaExportResponder(
                () => new List<X509Certificate2> { ClonePublic(bloated) }, null);

            var response = responder.Respond(new ProxyCaExportRequestPayload { CorrelationId = "c-7" });

            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.Error, response.ErrorCode);
            Assert.Contains("implausibly large", response.ErrorMessage, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- wire codec

        [Fact]
        public void ExportRequestRoundTripsAsOneJsonString()
        {
            var wire = ProxyFetchWireCodec.ToWire(new ProxyCaExportRequestPayload { CorrelationId = "c-8" });

            var back = ProxyFetchWireCodec.CaExportRequestFromWire(wire);
            Assert.NotNull(back);
            Assert.Equal("c-8", back.CorrelationId);
        }

        [Fact]
        public void ExportSuccessFrameRoundTripsWithDerThumbprintAndValidity()
        {
            using var ca = MintAuthorityCertificate(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(100));

            var wire = ProxyFetchWireCodec.ToWire(new ProxyCaExportResponsePayload
            {
                CorrelationId = "c-9",
                Success = true,
                AuthorityDerBase64 = Convert.ToBase64String(ca.RawData),
                AuthorityThumbprint = ca.Thumbprint,
                NotBeforeUtc = ca.NotBefore.ToUniversalTime(),
                NotAfterUtc = ca.NotAfter.ToUniversalTime(),
            });

            var back = ProxyFetchWireCodec.CaExportResponseFromWire(wire);
            Assert.NotNull(back);
            Assert.True(back.Success);
            Assert.Equal(ca.Thumbprint, back.AuthorityThumbprint);
            Assert.Equal(Convert.ToBase64String(ca.RawData), back.AuthorityDerBase64);
            Assert.Equal(ca.NotBefore.ToUniversalTime(), back.NotBeforeUtc);
            Assert.Equal(ca.NotAfter.ToUniversalTime(), back.NotAfterUtc);
        }

        [Fact]
        public void ExportFailureFrameRoundTripsAndForeignDataStaysUnusable()
        {
            var wire = ProxyFetchWireCodec.ToWire(new ProxyCaExportResponsePayload
            {
                CorrelationId = "c-10",
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.NoAuthority,
                ErrorMessage = "The proxy authority certificate does not exist yet.",
            });

            var back = ProxyFetchWireCodec.CaExportResponseFromWire(wire);
            Assert.NotNull(back);
            Assert.False(back.Success);
            Assert.Equal(ProxyFetchErrorCodes.NoAuthority, back.ErrorCode);

            Assert.Null(ProxyFetchWireCodec.CaExportRequestFromWire(42));
            Assert.Null(ProxyFetchWireCodec.CaExportResponseFromWire("not-json"));
        }

        // ---------------------------------------------------------------- correlation

        [Fact]
        public void AnUnansweredPullCompletesAsServerUnavailable()
        {
            var correlation = new ProxyCaExportCorrelation();

            var completion = correlation.Begin("c-11", TimeSpan.FromMilliseconds(50));
            var completed = completion.Wait(2000);

            Assert.True(completed, "the deadline never fired");
            var response = completion.Result;
            Assert.False(response.Success);
            Assert.Equal(ProxyFetchErrorCodes.ServerUnavailable, response.ErrorCode);
        }

        [Fact]
        public void OnlyThePendingExchangeOwnsAResponseFrame()
        {
            var correlation = new ProxyCaExportCorrelation();
            var completion = correlation.Begin("c-12", TimeSpan.FromSeconds(30));

            Assert.False(correlation.Feed(new ProxyCaExportResponsePayload { CorrelationId = "other" }));
            Assert.True(correlation.Feed(new ProxyCaExportResponsePayload
            {
                CorrelationId = "c-12",
                Success = true,
                AuthorityThumbprint = "AA",
            }));
            Assert.False(correlation.Feed(new ProxyCaExportResponsePayload { CorrelationId = "c-12" }));

            Assert.True(completion.Wait(1000));
            Assert.True(completion.Result.Success);
        }

        // ---------------------------------------------------------------- MC view

        [Fact]
        public void ThePageNeverSeesDecodableBytesForAnyFailurePosture()
        {
            // The ticket's failure to avoid: an empty .cer. Every non-answer — silence, an
            // absent CA, even a "success" frame with a blank or corrupt DER — decodes to a
            // failure with no bytes to save.
            var cases = new[]
            {
                null,
                new ProxyCaExportResponsePayload { Success = false, ErrorCode = ProxyFetchErrorCodes.NoAuthority },
                new ProxyCaExportResponsePayload { Success = false, ErrorCode = ProxyFetchErrorCodes.ServerUnavailable },
                new ProxyCaExportResponsePayload { Success = true },
                new ProxyCaExportResponsePayload { Success = true, AuthorityDerBase64 = string.Empty },
                new ProxyCaExportResponsePayload { Success = true, AuthorityDerBase64 = "not base64 !!" },
                new ProxyCaExportResponsePayload { Success = true, AuthorityDerBase64 = Convert.ToBase64String(Array.Empty<byte>()) },
            };

            foreach (var payload in cases)
            {
                var view = ProxyCaExportViewBuilder.Build(payload);
                Assert.False(view.ExportAvailable);
                Assert.NotNull(view.FailureCode);
                Assert.Null(view.DerBytes);
            }
        }

        [Fact]
        public void AUsableCertificateDecodesToBytesWithThumbprintAndValidity()
        {
            using var ca = MintAuthorityCertificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));

            var view = ProxyCaExportViewBuilder.Build(new ProxyCaExportResponsePayload
            {
                Success = true,
                AuthorityDerBase64 = Convert.ToBase64String(ca.RawData),
                AuthorityThumbprint = ca.Thumbprint,
                NotBeforeUtc = ca.NotBefore.ToUniversalTime(),
                NotAfterUtc = ca.NotAfter.ToUniversalTime(),
            });

            Assert.True(view.ExportAvailable);
            Assert.Equal(ca.RawData, view.DerBytes);
            Assert.Equal(ca.Thumbprint, view.Thumbprint);
            Assert.Equal(ca.NotAfter.ToUniversalTime(), view.NotAfterUtc);
            Assert.Null(view.FailureCode);
        }

        [Fact]
        public void TheMissingCaCopyNamesTheTicketPhraseInBothEndpointStrings()
        {
            // The AC's exact posture lives in two places: the Event Server payload message and
            // the Management Client's rendered sentence. Pin both wordings here.
            var responder = new ProxyCaExportResponder(() => new List<X509Certificate2>(), null);
            Assert.Contains(
                "proxy authority certificate does not exist yet",
                responder.Respond(new ProxyCaExportRequestPayload { CorrelationId = "c-13" }).ErrorMessage,
                StringComparison.OrdinalIgnoreCase);

            var resx = File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "Strings.resx"));
            var missingEntry = Regex.Match(resx, @"<data name=""ProxyCaExportMissing""[^>]*>\s*<value>(.*?)</value>", RegexOptions.Singleline);
            Assert.True(missingEntry.Success, "ProxyCaExportMissing is not in Strings.resx.");
            Assert.Contains(
                "proxy authority certificate does not exist yet",
                missingEntry.Groups[1].Value,
                StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- source guards

        [Fact]
        public void TheStoreReadIsReadOnlyAgainstTheFixedAuthorityName()
        {
            // Export must never provision or mutate (that stays the minting path's job) and must
            // resolve the exact same authority the leaf issuer loads: fixed CN, LocalMachine\Personal.
            var responderSource = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "ProxyCaExportResponder.cs"));

            Assert.Contains("OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly", responderSource);
            Assert.Contains("FindBySubjectName, ProxyLoopbackTls.AuthorityCommonName, false", responderSource);
            Assert.Contains("StoreName.My, StoreLocation.LocalMachine", responderSource);
            Assert.DoesNotContain("store.Add(", responderSource);
            Assert.DoesNotContain("ReadWrite", responderSource);
        }

        [Fact]
        public void TheExportAnswerIsUnicastToTheRequesterNeverBroadcast()
        {
            var fetcher = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "ProxyFetchBackgroundPlugin.cs"));
            var collapsed = Regex.Replace(fetcher, @"\s+", " ");

            Assert.Contains("new CommunicationIdFilter(ProxyMessageIds.CaExportRequestId)", fetcher);
            Assert.Contains("ProxyCaExportResponder.FindDeploymentAuthorityCertificates", fetcher);
            Assert.Contains("ProxyMessageIds.CaExportResponseId, ProxyFetchWireCodec.ToWire(response)), requester, null, null);", collapsed);
        }

        [Fact]
        public void TheRequesterSideRegistersTheAnswerFilterAndTransmitsThroughTheCodec()
        {
            var client = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "ProxyFetchClient.cs"));

            Assert.Contains("new CommunicationIdFilter(ProxyMessageIds.CaExportResponseId)", client);
            Assert.Contains("ProxyFetchWireCodec.CaExportResponseFromWire(message.Data)", client);
            Assert.Contains("ProxyFetchWireCodec.ToWire(new ProxyCaExportRequestPayload", client);
        }

        // ---------------------------------------------------------------- fixtures

        /// <summary>A deployment-CA-shaped self-signed RSA certificate (same shape the mint path builds).</summary>
        private static X509Certificate2 MintAuthorityCertificate(
            DateTimeOffset notBefore, DateTimeOffset notAfter, int paddingBytes = 0)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName("CN=" + ProxyLoopbackTls.AuthorityCommonName),
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            if (paddingBytes > 0)
            {
                var padding = new byte[paddingBytes];
                for (var i = 0; i < padding.Length; i++)
                {
                    padding[i] = (byte)(i % 251);
                }

                request.CertificateExtensions.Add(new X509Extension(new Oid("1.3.6.1.4.1.99999.1"), padding, false));
            }

            return request.CreateSelfSigned(notBefore, notAfter);
        }

        /// <summary>The public part of a certificate, the way a re-imported copy sits in the store.</summary>
        private static X509Certificate2 ClonePublic(X509Certificate2 certificate)
        {
            return X509CertificateLoader.LoadCertificate(certificate.RawData);
        }

        /// <summary>
        /// A copy carrying the private key, the way the minting path's authority copy sits in
        /// the store (PFX roundtrip - the same recipe the modern backend's creation path uses).
        /// </summary>
        private static X509Certificate2 WithPrivateKey(X509Certificate2 certificate)
        {
            var password = Guid.NewGuid().ToString("N");
            var portable = certificate.Export(X509ContentType.Pkcs12, password);
            return X509CertificateLoader.LoadPkcs12(portable, password);
        }
    }
}