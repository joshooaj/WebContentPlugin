using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using WebContent.TrustHelper;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>#72: the elevated trust-fix helper's decisions, unit-tested through the same
    /// core the real exe runs (Program.cs is deliberately not linked). Everything that could pop
    /// a UAC or crypt32 dialog sits AFTER these checks, so a refusal ending in a non-zero exit
    /// here proves the guardrail without ever touching a store. The positive store write is
    /// verified live from the deployed exe instead.</summary>
    public class ProxyTrustHelperTests
    {
        private const string ThumbprintHex = "0123456789ABCDEF0123456789ABCDEF00112233";

        /// <summary>A throwaway self-signed certificate to exercise the real validation path
        /// (its DER parses, its thumbprint is real) without any store side effects.</summary>
        private static X509Certificate2 CreateSelfSigned(string subject)
        {
            using (var rsa = System.Security.Cryptography.RSA.Create(2048))
            {
                var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                    subject, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                return request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
            }
        }

        private static string WriteDer(byte[] der)
        {
            var path = Path.Combine(Path.GetTempPath(), "wc-trusthelper-test-" + Guid.NewGuid().ToString("N") + ".der");
            File.WriteAllBytes(path, der);
            return path;
        }

        [Fact]
        public void BadArguments_Usage()
        {
            foreach (var args in new[]
            {
                new string[0],
                new[] { @"C:\some\leaf.der" },
                new[] { @"C:\some\leaf.der", "not-a-thumbprint" },
                new[] { @"C:\some\leaf.der", "0123456789ABCDEF0123456789ABCDEF0123456789ABC" },
                new[] { @"C:\some\leaf.der", ThumbprintHex, "extra" },
                new[] { "", ThumbprintHex },
            })
            {
                var parsed = TrustHelperCore.TryParseArguments(args, out _, out _, out var error);

                Assert.False(parsed);
                Assert.False(string.IsNullOrEmpty(error));
            }
        }

        [Fact]
        public void GoodArguments_Parse()
        {
            var parsed = TrustHelperCore.TryParseArguments(new[] { @"C:\some\leaf.der", "0123456789abcdef0123456789abcdef00112233" }, out var path, out var thumbprint, out var error);

            Assert.True(parsed);
            Assert.Null(error);
            Assert.Equal(@"C:\some\leaf.der", path);
            Assert.Equal(ThumbprintHex, thumbprint);
        }

        [Fact]
        public void MissingFile_ExitUnreadable_StoreNeverTouched()
        {
            var calls = 0;
            var result = TrustHelperCore.Run(@"C:\nonexistent\leaf.der", ThumbprintHex, c => { calls++; return 0; }, new StringWriter());

            Assert.Equal(TrustHelperCore.ExitFileUnreadable, result);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void GarbageBytes_ExitRejected_StoreNeverTouched()
        {
            var path = WriteDer(Encoding.ASCII.GetBytes("this is not a DER certificate"));
            try
            {
                var calls = 0;
                var result = TrustHelperCore.Run(path, ThumbprintHex, c => { calls++; return 0; }, new StringWriter());

                Assert.Equal(TrustHelperCore.ExitCertificateRejected, result);
                Assert.Equal(0, calls);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void OversizedFile_ExitRejected_BeforeParsing()
        {
            var path = WriteDer(new byte[TrustHelperCore.MaxAuthorityDerBytes + 1]);
            try
            {
                var calls = 0;
                var result = TrustHelperCore.Run(path, ThumbprintHex, c => { calls++; return 0; }, new StringWriter());

                Assert.Equal(TrustHelperCore.ExitCertificateRejected, result);
                Assert.Equal(0, calls);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void EmptyFile_ExitRejected()
        {
            var path = WriteDer(new byte[0]);
            try
            {
                var result = TrustHelperCore.Run(path, ThumbprintHex, c => 0, new StringWriter());

                Assert.Equal(TrustHelperCore.ExitCertificateRejected, result);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ThumbprintMismatch_StoreNeverTouched()
        {
            // A REAL certificate whose thumbprint does NOT equal the claimed one: the helper must
            // refuse before any store work - this is the anti-confusion check that keeps the tool
            // from importing "a" CA when the caller meant THIS CA.
            using (var cert = CreateSelfSigned("CN=WebContent trust-helper mismatch test"))
            {
                var path = WriteDer(cert.RawData);
                try
                {
                    var calls = 0;
                    var result = TrustHelperCore.Run(path, ThumbprintHex, c => { calls++; return 0; }, new StringWriter());

                    Assert.Equal(TrustHelperCore.ExitCertificateRejected, result);
                    Assert.Equal(0, calls);
                }
                finally
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void MatchedThumbprint_ImportCalledOnceWithTheVerifiedCertificate()
        {
            using (var cert = CreateSelfSigned("CN=WebContent trust-helper match test"))
            {
                var path = WriteDer(cert.RawData);
                try
                {
                    string importedThumbprint = null;
                    var calls = 0;
                    var result = TrustHelperCore.Run(path, cert.Thumbprint, c => { calls++; importedThumbprint = c.Thumbprint; return TrustHelperCore.ExitOk; }, new StringWriter());

                    Assert.Equal(TrustHelperCore.ExitOk, result);
                    Assert.Equal(1, calls);
                    Assert.Equal(cert.Thumbprint, importedThumbprint);
                }
                finally
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void StoreExitCode_PropagatesUnchanged()
        {
            // The core never reinterprets the injected store write's verdict (4 = the store could
            // not be opened - the real machine's non-elevated symptom; the plugin re-checks the
            // stores itself and never trusts this channel blindly).
            using (var cert = CreateSelfSigned("CN=WebContent trust-helper store test"))
            {
                var path = WriteDer(cert.RawData);
                try
                {
                    var result = TrustHelperCore.Run(path, cert.Thumbprint, c => TrustHelperCore.ExitStoreUnavailable, new StringWriter());

                    Assert.Equal(TrustHelperCore.ExitStoreUnavailable, result);
                }
                finally
                {
                    File.Delete(path);
                }
            }
        }
    }
}
