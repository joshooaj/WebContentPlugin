using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace WebContent.TrustHelper
{
    /// <summary>
    /// Ticket #72: the auditable core of the elevated trust helper. Split from <c>Main</c> so the
    /// test project links it and pins every refusal - the whole point of the helper is that it is
    /// too small and too strict to do anything but import THIS thumbprint's DER, so that shape
    /// must be unit-provable without touching a trust store.
    ///
    /// Posture: exactly one certificate, supplied as a DER file path argument plus the expected
    /// thumbprint; the file is size-capped before parsing; the parsed certificate's OWN
    /// thumbprint must match the expected value before any store opens; the store operation is
    /// injected so tests never write machine trust state. Exit codes are the caller's only
    /// result channel (the plugin re-verifies the root stores itself - the helper's success is
    /// never trusted blindly).
    /// </summary>
    internal static class TrustHelperCore
    {
        /// <summary>Same ceiling the CA export contract enforces (#75): a deployment CA DER is
        /// ~1 KB; anything implausibly large is refused before parsing.</summary>
        internal const int MaxAuthorityDerBytes = 8 * 1024;

        internal const int ExitOk = 0;
        internal const int ExitUsage = 1;
        internal const int ExitCertificateRejected = 2;
        internal const int ExitFileUnreadable = 3;
        internal const int ExitStoreUnavailable = 4;
        internal const int ExitImportFailed = 5;

        /// <summary>
        /// Two positional arguments: the CA DER file and the expected SHA-1 thumbprint (40 hex
        /// chars, the shape X509Certificate2.Thumbprint prints). Nothing else is accepted - no
        /// store selector, no "add anyway", no wildcard.
        /// </summary>
        internal static bool TryParseArguments(string[] args, out string derFilePath, out string expectedThumbprint, out string error)
        {
            derFilePath = null;
            expectedThumbprint = null;
            error = null;

            if (args == null || args.Length != 2)
            {
                error = "usage: WebContentTrustHelper.exe <certificate-der-file> <expected-thumbprint>";
                return false;
            }

            if (string.IsNullOrWhiteSpace(args[0]))
            {
                error = "the certificate DER file path must be non-empty.";
                return false;
            }

            var thumbprint = (args[1] ?? string.Empty).Trim();
            if (thumbprint.Length != 40 || !IsHexString(thumbprint))
            {
                error = "the expected thumbprint must be exactly 40 hex characters (the SHA-1 thumbprint of the certificate to import).";
                return false;
            }

            derFilePath = args[0];
            expectedThumbprint = thumbprint.ToUpperInvariant();
            return true;
        }

        /// <summary>
        /// Validate first, touch a store second, import exactly one certificate. Returns a
        /// process exit code; <paramref name="importToRootStore"/> is called ONLY after the
        /// parsed certificate's own thumbprint equals the expected one, and its return value is
        /// the process result (tests inject a fake; production injects the real store write).
        /// </summary>
        internal static int Run(string derFilePath, string expectedThumbprint, Func<X509Certificate2, int> importToRootStore, TextWriter output)
        {
            if (importToRootStore == null) throw new ArgumentNullException(nameof(importToRootStore));

            byte[] der;
            try
            {
                der = File.ReadAllBytes(derFilePath);
            }
            catch (Exception exception)
            {
                output.WriteLine($"cannot read the certificate file: {exception.Message}");
                return ExitFileUnreadable;
            }

            if (der.Length == 0 || der.Length > MaxAuthorityDerBytes)
            {
                output.WriteLine($"refused: the file is {der.Length} DER bytes; a deployment CA must be between 1 and {MaxAuthorityDerBytes} bytes.");
                return ExitCertificateRejected;
            }

            X509Certificate2 certificate;
            try
            {
                certificate = LoadCertificate(der);
            }
            catch (Exception exception)
            {
                output.WriteLine($"refused: the file is not a parsable X.509 certificate: {exception.Message}");
                return ExitCertificateRejected;
            }

            using (certificate)
            {
                if (!string.Equals(certificate.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine(
                        "refused: the certificate's thumbprint " + certificate.Thumbprint +
                        " does not match the expected " + expectedThumbprint +
                        " (subject '" + certificate.Subject + "'); nothing was imported.");
                    return ExitCertificateRejected;
                }

                output.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "certificate verified: subject '{0}', thumbprint {1}, valid to {2:u}.",
                    certificate.Subject,
                    certificate.Thumbprint,
                    certificate.NotAfter));

                return importToRootStore(certificate);
            }
        }

        private static X509Certificate2 LoadCertificate(byte[] der)
        {
#if NETFRAMEWORK
            return new X509Certificate2(der);
#else
            return X509CertificateLoader.LoadCertificate(der);
#endif
        }

        private static bool IsHexString(string value)
        {
            foreach (var character in value)
            {
                var isHex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
