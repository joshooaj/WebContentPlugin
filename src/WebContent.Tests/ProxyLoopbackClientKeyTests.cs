using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #56 (ADR-0003): the client-side half of a loopback leaf - a locally generated key
    /// whose public half alone is exportable for minting, and whose AttachLeaf lands the minted
    /// certificate with a private key Schannel accepts for server authentication.
    /// </summary>
    public class ProxyLoopbackClientKeyTests
    {
        private static readonly Action<string> Sink = _ => { };

        [Fact]
        public void TheLocalKeyExportsAPublicKeyThatReparses()
        {
            using (var key = ProxyLoopbackClientKey.Create())
            {
                Assert.NotNull(key.SubjectPublicKeyInfoDer);
                Assert.True(key.SubjectPublicKeyInfoDer.Length > 50);

                using (var reparsed = RSA.Create())
                {
                    reparsed.ImportSubjectPublicKeyInfo(key.SubjectPublicKeyInfoDer, out _);
                    var modulus = reparsed.ExportParameters(false).Modulus;
                    Assert.NotNull(modulus);
                }
            }
        }

        [Fact]
        public void TheExportedPublicKeyHoldsNoPrivateHalf()
        {
            // The SPKI is what crosses the wire; it must be public-only by construction.
            using (var key = ProxyLoopbackClientKey.Create())
            using (var reparsed = RSA.Create())
            {
                reparsed.ImportSubjectPublicKeyInfo(key.SubjectPublicKeyInfoDer, out _);
                Assert.ThrowsAny<CryptographicException>(() => reparsed.ExportRSAPrivateKey());
            }
        }

        [Fact]
        public void AttachLeafProducesAUsableCertificateWithTheLocalPrivateHalf()
        {
            using (var authority = Authority.Create(Sink))
            using (var key = ProxyLoopbackClientKey.Create())
            {
                var leafDer = authority.MintLeafForPublicKey("cam1.test", key.SubjectPublicKeyInfoDer);

                using (var leaf = key.AttachLeaf(leafDer))
                {
                    Assert.True(leaf.HasPrivateKey);
                    Assert.Equal("CN=cam1.test", leaf.Subject);
                    using (var rsa = leaf.GetRSAPrivateKey())
                    {
                        Assert.NotNull(rsa);
                    }
                }
            }
        }

        [Fact]
        public void AttachLeafRejectsGarbageDerWithoutThrowingAwayTheKey()
        {
            using (var key = ProxyLoopbackClientKey.Create())
            {
                Assert.ThrowsAny<Exception>(() => key.AttachLeaf(new byte[] { 1, 2, 3 }));

                // A failed attach must not poison the key: a later good attach still works.
                using (var authority = Authority.Create(Sink))
                using (var leaf = key.AttachLeaf(authority.MintLeafForPublicKey("cam1.test", key.SubjectPublicKeyInfoDer)))
                {
                    Assert.True(leaf.HasPrivateKey);
                }
            }
        }
    }
}
