using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WebContent
{
    /// <summary>
    /// Certificate minting backend for <see cref="ProxyLoopbackTls"/>. Two implementations behind
    /// one internal shape, selected by target framework:
    ///
    /// * .NET Framework 4.7.2 (the plugin, where the browser actually runs): no System.Security
    ///   certificate creation API exists (no CertificateRequest), so the certificates are built as
    ///   raw DER and signed through a persisted CAPI key container; the key is attached to the
    ///   parsed X509Certificate2 through the PrivateKey property (Framework-only). This recipe was
    ///   proven by probe: the minted leaf completes a real SslStream server handshake, which the
    ///   ephemeral-key in-memory certificates fail ("the platform does not support ephemeral keys").
    ///
    /// * modern .NET (where the test project compiles this file): CertificateRequest, with the same
    ///   PFX roundtrip the production Smart Client code uses so the private key is usable by
    ///   Schannel for server authentication.
    ///
    /// The CA deliberately carries NO extended key usage: an EKU on a certificate authority makes
    /// Windows chain validation of its leaves fail with NotValidForUsage.
    /// </summary>
#if NETFRAMEWORK
    internal sealed class Authority : IDisposable
    {
        private const int KeyBits = 2048;

        // PROV_RSA_AES: the modern enhanced provider; SHA-256 signing needs it.
        private const int EnhancedProviderType = 24;
        private const string EnhancedProviderName = "Microsoft Enhanced RSA and AES Cryptographic Provider";

        private readonly RSACryptoServiceProvider _authorityKey;
        private readonly X509Certificate2 _authorityCertificate;
        private readonly byte[] _authorityNameDer;
        private readonly byte[] _authoritySpkiDer;

        internal static Authority Create(Action<string> log)
        {
            return new Authority();
        }

        private Authority()
        {
            _authorityKey = NewPersistedProvider("WCProxyCA-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            _authorityNameDer = Der.Name(ProxyLoopbackTls.AuthorityCommonName);
            _authoritySpkiDer = Der.SubjectPublicKeyInfo(_authorityKey);

            var notBefore = DateTime.UtcNow.AddDays(-1);
            var notAfter = notBefore.AddYears(10);

            var tbs = Der.Sequence(
                Der.Tagged(0xA0, Der.Integer(2)),                       // version v3
                Der.Integer(RandomPositiveBytes(8)),                    // serial
                Der.Sha256RsaAlgorithmIdentifier(),
                _authorityNameDer,                                      // issuer = subject (self-signed)
                Der.Validity(notBefore, notAfter),
                _authorityNameDer,
                _authoritySpkiDer,
                Der.Extensions(
                    Der.BasicConstraints(true),
                    Der.AuthorityKeyUsage(),
                    Der.SubjectKeyIdentifier(_authoritySpkiDer)));      // no EKU by design

            var certificateDer = Sign(tbs, _authorityKey);
            _authorityCertificate = new X509Certificate2(certificateDer);
            _authorityCertificate.PrivateKey = _authorityKey;
        }

        internal X509Certificate2 Certificate => _authorityCertificate;

        internal ProxyLoopbackLeafOwner CreateLeaf(string host)
        {
            // Per-host persisted container: Schannel needs a provider-backed key for server
            // authentication, and the container goes away when the owner is disposed.
            var key = NewPersistedProvider(
                "WCProxyLeaf-" + ShortHash(host) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var certificateDer = MintLeafForPublicKey(host, Der.SubjectPublicKeyInfo(key));
                var certificate = new X509Certificate2(certificateDer);
                certificate.PrivateKey = key;
                return new ProxyLoopbackLeafOwner(certificate, key);
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }

        /// <summary>
        /// ADR-0003: signs a leaf for this host around a caller-supplied SubjectPublicKeyInfo —
        /// the Event Server mint path, where the private half lives on the requesting client.
        /// Validation here is structural (the Framework has no SPKI parser); an unusable public
        /// key surfaces as ArgumentException, which the issuer maps to a BadRequest refusal.
        /// </summary>
        internal byte[] MintLeafForPublicKey(string host, byte[] subjectPublicKeyInfoDer)
        {
            if (subjectPublicKeyInfoDer == null ||
                subjectPublicKeyInfoDer.Length < 50 ||
                subjectPublicKeyInfoDer.Length > 4096 ||
                subjectPublicKeyInfoDer[0] != 0x30)
            {
                throw new ArgumentException("The client public key is not a usable SubjectPublicKeyInfo.", nameof(subjectPublicKeyInfoDer));
            }

            var subjectDer = Der.Name(host);
            var notBefore = DateTime.UtcNow.AddDays(-1);
            var notAfter = notBefore.AddYears(2);

            var tbs = Der.Sequence(
                Der.Tagged(0xA0, Der.Integer(2)),
                Der.Integer(RandomPositiveBytes(8)),
                Der.Sha256RsaAlgorithmIdentifier(),
                _authorityNameDer,                                      // issuer = the deployment CA
                Der.Validity(notBefore, notAfter),
                subjectDer,
                subjectPublicKeyInfoDer,
                Der.Extensions(
                    Der.BasicConstraints(false),
                    Der.LeafKeyUsage(),
                    Der.ServerAuthEku(),
                    Der.SubjectKeyIdentifier(subjectPublicKeyInfoDer),
                    Der.SubjectAlternativeName(host)));

            return Sign(tbs, _authorityKey);
        }

        public void Dispose()
        {
            try { _authorityCertificate.Dispose(); } catch { }
            try { _authorityKey.Dispose(); } catch { }
        }

        private static byte[] Sign(byte[] tbs, RSACryptoServiceProvider key)
        {
            // SignHash(hash, oid) treats the input as the digest and frames the PKCS#1 DigestInfo
            // itself - the resulting signature is exactly sha256RSA over tbs.
            var signature = key.SignHash(DerSha256(tbs), CryptoConfig.MapNameToOID("SHA256"));
            return Der.Sequence(
                tbs,
                Der.Sha256RsaAlgorithmIdentifier(),
                Der.BitStringBytes(signature));
        }

        private static RSACryptoServiceProvider NewPersistedProvider(string containerName)
        {
            var parameters = new CspParameters
            {
                KeyContainerName = containerName,
                ProviderName = EnhancedProviderName,
                ProviderType = EnhancedProviderType,
                KeyNumber = (int)KeyNumber.Exchange
            };
            var provider = new RSACryptoServiceProvider(KeyBits, parameters);

            // Persisted in a named container while the certificate uses it (Schannel needs a
            // provider-backed key), but the container is deleted when this object is disposed.
            provider.PersistKeyInCsp = false;
            return provider;
        }

        private static byte[] RandomPositiveBytes(int count)
        {
            var bytes = new byte[count];
            using (var generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            bytes[0] &= 0x7F; // keeps the INTEGER positive without a pad byte
            return bytes;
        }

        private static string ShortHash(string host)
        {
            using (var hash = SHA1.Create())
            {
                var digest = hash.ComputeHash(Encoding.UTF8.GetBytes(host.ToLowerInvariant()));
                var builder = new StringBuilder(10);
                for (var i = 0; i < 5; i++)
                {
                    builder.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        private static byte[] DerSha256(byte[] data)
        {
            using (var hash = SHA256.Create())
            {
                return hash.ComputeHash(data);
            }
        }
    }

    /// <summary>Hand-built DER for the Framework minting path (no CertificateRequest on 4.7.2).</summary>
    internal static class Der
    {
        internal static byte[] Sequence(params byte[][] parts) => Tag(0x30, Concat(parts));
        internal static byte[] Set(params byte[][] parts) => Tag(0x31, Concat(parts));
        internal static byte[] Tagged(byte tag, params byte[][] parts) => Tag(tag, Concat(parts));

        internal static byte[] Tag(byte tag, byte[] value)
        {
            var length = Length(value.Length);
            var result = new byte[1 + length.Length + value.Length];
            result[0] = tag;
            Buffer.BlockCopy(length, 0, result, 1, length.Length);
            Buffer.BlockCopy(value, 0, result, 1 + length.Length, value.Length);
            return result;
        }

        internal static byte[] Integer(int value)
        {
            var bytes = new List<byte>();
            do
            {
                bytes.Insert(0, (byte)(value & 0xFF));
                value >>= 8;
            }
            while (value > 0);

            if ((bytes[0] & 0x80) != 0)
            {
                bytes.Insert(0, 0);
            }

            return Tag(0x02, bytes.ToArray());
        }

        internal static byte[] Integer(byte[] magnitude)
        {
            var start = 0;
            while (start < magnitude.Length - 1 && magnitude[start] == 0)
            {
                start++;
            }

            var value = new byte[magnitude.Length - start];
            Buffer.BlockCopy(magnitude, start, value, 0, value.Length);
            if ((value[0] & 0x80) != 0)
            {
                var padded = new byte[value.Length + 1];
                Buffer.BlockCopy(value, 0, padded, 1, value.Length);
                value = padded;
            }

            return Tag(0x02, value);
        }

        internal static byte[] Boolean(bool value) => Tag(0x01, new[] { (byte)(value ? 0xFF : 0x00) });
        internal static byte[] Null() => new byte[] { 0x05, 0x00 };
        internal static byte[] OctetString(byte[] value) => Tag(0x04, value);
        internal static byte[] BitStringBytes(byte[] data) => Tag(0x03, Concat(new[] { new byte[] { 0 }, data }));
        internal static byte[] BitString(byte unusedBits, byte data) => Tag(0x03, new[] { unusedBits, data });

        internal static byte[] ObjectIdentifier(string dotted) => Tag(0x06, ObjectIdentifierValue(dotted));

        internal static byte[] UtcTime(DateTime utc)
        {
            var text = utc.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";
            return Tag(0x17, Encoding.ASCII.GetBytes(text));
        }

        internal static byte[] Name(string commonName) =>
            Sequence(Set(Sequence(ObjectIdentifier("2.5.4.3"), Tag(0x0C, Encoding.UTF8.GetBytes(commonName)))));

        internal static byte[] Validity(DateTime notBefore, DateTime notAfter) =>
            Sequence(UtcTime(notBefore), UtcTime(notAfter));

        internal static byte[] Sha256RsaAlgorithmIdentifier() =>
            Sequence(ObjectIdentifier("1.2.840.113549.1.1.11"), Null());

        internal static byte[] SubjectPublicKeyInfo(RSA key)
        {
            var parameters = key.ExportParameters(false);
            var rsaPublicKey = Sequence(Integer(parameters.Modulus), Integer(parameters.Exponent));
            return Sequence(
                Sequence(ObjectIdentifier("1.2.840.113549.1.1.1"), Null()),
                BitStringBytes(rsaPublicKey));
        }

        internal static byte[] Extensions(params byte[][] extensions) => Tagged(0xA3, Sequence(extensions));

        internal static byte[] BasicConstraints(bool isCertificateAuthority) =>
            Extension("2.5.29.19", true, isCertificateAuthority ? Sequence(Boolean(true)) : Sequence());

        // keyCertSign | cRLSign: DER bit string, bits 5 and 6, one pad bit.
        internal static byte[] AuthorityKeyUsage() => Extension("2.5.29.15", true, BitString(1, 0x06));

        // digitalSignature | keyEncipherment: DER bits 0 and 2, five pad bits.
        internal static byte[] LeafKeyUsage() => Extension("2.5.29.15", false, BitString(5, 0xA0));

        internal static byte[] ServerAuthEku() =>
            Extension("2.5.29.37", true, Sequence(ObjectIdentifier("1.3.6.1.5.5.7.3.1")));

        internal static byte[] SubjectKeyIdentifier(byte[] subjectPublicKeyInfo)
        {
            using (var hash = SHA1.Create())
            {
                return Extension("2.5.29.14", false, OctetString(hash.ComputeHash(subjectPublicKeyInfo)));
            }
        }

        internal static byte[] SubjectAlternativeName(string host)
        {
            IPAddress address;
            byte[] entry = IPAddress.TryParse(host, out address)
                ? Tag(0x87, address.GetAddressBytes())
                : Tag(0x82, Encoding.ASCII.GetBytes(host));
            return Extension("2.5.29.17", false, Sequence(entry));
        }

        private static byte[] Extension(string oid, bool critical, byte[] value)
        {
            var parts = critical
                ? new[] { ObjectIdentifier(oid), Boolean(true), OctetString(value) }
                : new[] { ObjectIdentifier(oid), OctetString(value) };
            return Sequence(parts);
        }

        private static byte[] ObjectIdentifierValue(string dotted)
        {
            var parts = dotted.Split('.');
            var bytes = new List<byte>();
            bytes.Add((byte)(int.Parse(parts[0], CultureInfo.InvariantCulture) * 40 +
                             int.Parse(parts[1], CultureInfo.InvariantCulture)));
            for (var i = 2; i < parts.Length; i++)
            {
                var value = int.Parse(parts[i], CultureInfo.InvariantCulture);
                var group = new List<byte>();
                do
                {
                    group.Insert(0, (byte)(value & 0x7F));
                    value >>= 7;
                }
                while (value > 0);

                // Sub-OID continuation bit: set on every byte except the last.
                for (var j = 0; j < group.Count - 1; j++)
                {
                    group[j] |= 0x80;
                }

                bytes.AddRange(group);
            }

            return bytes.ToArray();
        }

        private static byte[] Length(int length)
        {
            if (length < 0x80)
            {
                return new[] { (byte)length };
            }

            var bytes = new List<byte>();
            do
            {
                bytes.Insert(0, (byte)(length & 0xFF));
                length >>= 8;
            }
            while (length > 0);

            var result = new byte[bytes.Count + 1];
            result[0] = (byte)(0x80 | bytes.Count);
            for (var i = 0; i < bytes.Count; i++)
            {
                result[i + 1] = bytes[i];
            }

            return result;
        }

        private static byte[] Concat(byte[][] parts)
        {
            var total = 0;
            foreach (var part in parts)
            {
                total += part.Length;
            }

            var result = new byte[total];
            var offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }
    }
#else
    internal sealed class Authority : IDisposable
    {
        private readonly RSA _authorityKey;
        private readonly X509Certificate2 _authorityCertificate;

        internal static Authority Create(Action<string> log)
        {
            return new Authority();
        }

        private Authority()
        {
            _authorityKey = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName("CN=" + ProxyLoopbackTls.AuthorityCommonName),
                _authorityKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            // no EKU by design

            _authorityCertificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(3650));
        }

        internal X509Certificate2 Certificate => _authorityCertificate;

        internal ProxyLoopbackLeafOwner CreateLeaf(string host)
        {
            var key = RSA.Create(2048);
            var certificateDer = MintLeafForPublicKey(host, key.ExportSubjectPublicKeyInfo());
            var certificate = X509CertificateLoader.LoadCertificate(certificateDer);

            // The request key is not attached automatically when the issuer's own key is not
            // copyable, so attach it here; either way the result must hold a private key.
            var withKey = certificate.HasPrivateKey ? certificate : certificate.CopyWithPrivateKey(key);

            // Schannel refuses ephemeral CNG keys for server authentication; the PFX roundtrip
            // lands the key in a persisted form it accepts (same recipe as production).
            var password = Guid.NewGuid().ToString("N");
            var portable = withKey.Export(X509ContentType.Pkcs12, password);
            var usable = X509CertificateLoader.LoadPkcs12(portable, password);
            if (!ReferenceEquals(withKey, certificate))
            {
                withKey.Dispose();
            }

            return new ProxyLoopbackLeafOwner(usable, key);
        }

        /// <summary>
        /// ADR-0003: signs a leaf for this host around a caller-supplied SubjectPublicKeyInfo —
        /// the Event Server mint path, where the private half lives on the requesting client. An
        /// unusable public key surfaces as ArgumentException, which the issuer maps to a
        /// BadRequest refusal.
        /// </summary>
        internal byte[] MintLeafForPublicKey(string host, byte[] subjectPublicKeyInfoDer)
        {
            var publicKey = RSA.Create();
            try
            {
                publicKey.ImportSubjectPublicKeyInfo(subjectPublicKeyInfoDer ?? Array.Empty<byte>(), out _);
            }
            catch (Exception exception)
            {
                publicKey.Dispose();
                throw new ArgumentException("The client public key is not a usable RSA SubjectPublicKeyInfo.", nameof(subjectPublicKeyInfoDer), exception);
            }

            try
            {
                var request = new CertificateRequest(
                    new X500DistinguishedName("CN=" + host),
                    publicKey,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    false));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    true));

                var builder = new SubjectAlternativeNameBuilder();
                IPAddress address;
                if (IPAddress.TryParse(host, out address))
                {
                    builder.AddIpAddress(address);
                }
                else
                {
                    builder.AddDnsName(host);
                }

                request.CertificateExtensions.Add(builder.Build(false));

                using (var certificate = request.Create(
                    _authorityCertificate,
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(730),
                    RandomNumberGenerator.GetBytes(8)))
                {
                    return certificate.RawData;
                }
            }
            finally
            {
                publicKey.Dispose();
            }
        }

        public void Dispose()
        {
            try { _authorityCertificate.Dispose(); } catch { }
            try { _authorityKey.Dispose(); } catch { }
        }
    }
#endif

    /// <summary>
    /// Ownership bundle for one minted leaf: the usable certificate plus the key object that must
    /// outlive it (the Framework PrivateKey attachment references the provider handle/container).
    /// </summary>
    internal sealed class ProxyLoopbackLeafOwner : IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly RSA _key;

        internal ProxyLoopbackLeafOwner(X509Certificate2 certificate, RSA key)
        {
            _certificate = certificate;
            _key = key;
        }

        internal X509Certificate2 Certificate => _certificate;

        public void Dispose()
        {
            try { _certificate.Dispose(); } catch { }
            try { _key.Dispose(); } catch { }
        }
    }
}
