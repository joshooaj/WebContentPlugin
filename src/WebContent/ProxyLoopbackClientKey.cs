using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WebContent
{
    /// <summary>
    /// Ticket #56 (ADR-0003): the client-side key behind one loopback leaf certificate. The key
    /// pair is generated where the TLS listener runs (the Smart Client machine) and only the
    /// SubjectPublicKeyInfo is handed to the Event Server for signing - the private half never
    /// leaves this process. <see cref="AttachLeaf"/> binds the minted certificate DER to the
    /// local key in a form Schannel accepts for AuthenticateAsServer: a persisted CAPI exchange
    /// container on .NET Framework, a PFX roundtrip on modern .NET (both recipes are the ones
    /// the certificate probes proved; ephemeral in-memory keys fail server auth).
    /// </summary>
    internal sealed class ProxyLoopbackClientKey : IDisposable
    {
#if NETFRAMEWORK
        private const int KeyBits = 2048;
        private const string EnhancedProviderName = "Microsoft Enhanced RSA and AES Cryptographic Provider";

        private readonly RSACryptoServiceProvider _key;

        internal static ProxyLoopbackClientKey Create()
        {
            // AT_KEYEXCHANGE on the enhanced provider: Schannel rejects signature-usage CAPI keys
            // for server authentication. The container outlives nothing - PersistKeyInCsp=false
            // deletes it when this object is disposed, matching the per-session leaf lifetime.
            var parameters = new CspParameters
            {
                KeyContainerName = "WCProxyLeaf-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                ProviderName = EnhancedProviderName,
                ProviderType = 24,   // PROV_RSA_AES
                KeyNumber = (int)KeyNumber.Exchange,
            };
            var key = new RSACryptoServiceProvider(KeyBits, parameters);
            key.PersistKeyInCsp = false;
            return new ProxyLoopbackClientKey(key, Der.SubjectPublicKeyInfo(key));
        }

        private ProxyLoopbackClientKey(RSACryptoServiceProvider key, byte[] subjectPublicKeyInfoDer)
        {
            _key = key;
            SubjectPublicKeyInfoDer = subjectPublicKeyInfoDer;
        }

        internal X509Certificate2 AttachLeaf(byte[] leafCertificateDer)
        {
            var certificate = new X509Certificate2(leafCertificateDer ?? throw new ArgumentNullException(nameof(leafCertificateDer)));
            certificate.PrivateKey = _key;   // Framework-only attachment; keeps _key alive via the owner bundle
            return certificate;
        }

        public void Dispose()
        {
            try { _key.Dispose(); } catch { }
        }
#else
        private readonly RSA _key;

        internal static ProxyLoopbackClientKey Create()
        {
            var key = RSA.Create(2048);
            return new ProxyLoopbackClientKey(key, key.ExportSubjectPublicKeyInfo());
        }

        private ProxyLoopbackClientKey(RSA key, byte[] subjectPublicKeyInfoDer)
        {
            _key = key;
            SubjectPublicKeyInfoDer = subjectPublicKeyInfoDer;
        }

        internal X509Certificate2 AttachLeaf(byte[] leafCertificateDer)
        {
            var certificate = X509CertificateLoader.LoadCertificate(
                leafCertificateDer ?? throw new ArgumentNullException(nameof(leafCertificateDer)));
            var withKey = certificate.CopyWithPrivateKey(_key);

            // Schannel refuses ephemeral CNG keys for server authentication; the PFX roundtrip
            // persists the key material into a form it accepts (same recipe as the minting side).
            var password = Guid.NewGuid().ToString("N");
            var portable = withKey.Export(X509ContentType.Pkcs12, password);
            var usable = X509CertificateLoader.LoadPkcs12(portable, password);
            withKey.Dispose();
            certificate.Dispose();
            return usable;
        }

        public void Dispose()
        {
            try { _key.Dispose(); } catch { }
        }
#endif

        /// <summary>The public half safe to send to the Event Server for minting.</summary>
        internal byte[] SubjectPublicKeyInfoDer { get; }
    }
}
