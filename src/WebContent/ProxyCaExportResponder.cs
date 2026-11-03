using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

namespace WebContent
{
    /// <summary>
    /// Ticket #75: bounds for the deployment CA export exchange. A self-signed 2048-bit RSA CA
    /// certificate DER is ~0.8-1 KB (4096-bit: ~1.3 KB); the caps below are an order of
    /// magnitude above that and exist to refuse absurd payloads, not to shape normal ones. The
    /// messaging headroom is proven, not guessed: a fetch chunk frame already rides this channel
    /// as a single JSON string of ~87 K characters (64 KiB of base64 body,
    /// <see cref="ProxyFetchLimits.DefaultChunkSizeBytes"/>) on every live proxied page load, so
    /// a ~1.3 K success frame needs no chunking - <c>ProxyCaExportTests</c> asserts a production
    /// CA's real frame against the cap (the ticket's AC).
    /// </summary>
    internal static class ProxyCaExportLimits
    {
        /// <summary>Sanity ceiling on one X.509 certificate DER; production CAs are ~1 KB.</summary>
        internal const int MaxAuthorityDerBytes = 8 * 1024;

        /// <summary>
        /// Ceiling on the whole success frame as transmitted (base64 DER plus the small
        /// metadata fields): under a fifth of the proven single-frame chunk size above.
        /// </summary>
        internal const int MaxResponseWireChars = 16 * 1024;
    }

    /// <summary>
    /// Ticket #75: the Event Server half of the deployment CA public-certificate export. Reads —
    /// never creates, and never touches a private key — the same authority the leaf issuer loads:
    /// the fixed-CN certificate in this machine's LocalMachine\Personal store, whatever created
    /// it (the MSI custom action, the legacy provisioning script, or the lazy first-use
    /// mint). Export is public material only, so unlike the minting path it needs no store write
    /// access and no key usability: an admin with a read-only store still gets the certificate.
    ///
    /// Postures the Management Client renders distinctly: no CA at all and an only-expired CA
    /// both answer NoAuthority with actionable messages (never an empty success — an empty .cer
    /// is worse than a refusal), and a store fault answers Error instead of throwing on the
    /// messaging receive thread.
    /// </summary>
    internal sealed class ProxyCaExportResponder
    {
        private readonly Func<IList<X509Certificate2>> _certificateFinder;
        private readonly Action<string> _log;

        /// <param name="certificateFinder">
        /// Supplies the candidate certificates (every LocalMachine\Personal copy carrying the
        /// fixed deployment authority CN). The responder takes ownership: every certificate it
        /// is handed is disposed once the payload fields are read.
        /// </param>
        internal ProxyCaExportResponder(Func<IList<X509Certificate2>> certificateFinder, Action<string> log)
        {
            _certificateFinder = certificateFinder ?? throw new ArgumentNullException(nameof(certificateFinder));
            _log = log ?? delegate { };
        }

        /// <summary>
        /// Resolve and answer. Never throws: every failure becomes a failure payload with a
        /// machine-readable code (BadRequest / NoAuthority / Error).
        /// </summary>
        internal ProxyCaExportResponsePayload Respond(ProxyCaExportRequestPayload request)
        {
            if (request == null || string.IsNullOrEmpty(request.CorrelationId))
            {
                return Failure(null, ProxyFetchErrorCodes.BadRequest,
                    "The proxy certificate export request carried no usable payload.");
            }

            IList<X509Certificate2> candidates;
            try
            {
                candidates = _certificateFinder();
            }
            catch (Exception exception)
            {
                _log($"Proxy authority certificate export could not read the LocalMachine Personal store: {exception.Message}");
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.Error,
                    "The Event Server could not read the LocalMachine\\Personal certificate store: " + exception.Message);
            }

            try
            {
                return Resolve(request, candidates);
            }
            catch (Exception exception)
            {
                // Nothing below is expected to throw; anything escaping still answers as a
                // failure frame rather than leaving the Management Client to time out.
                _log($"Proxy authority certificate export failed: {exception.Message}");
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.Error,
                    "The proxy authority certificate could not be exported: " + exception.Message);
            }
            finally
            {
                if (candidates != null)
                {
                    foreach (var candidate in candidates)
                    {
                        if (candidate != null)
                        {
                            try { candidate.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        private ProxyCaExportResponsePayload Resolve(ProxyCaExportRequestPayload request, IList<X509Certificate2> candidates)
        {
            // Pick the copy the minting path would use: unexpired, preferring one whose private
            // key lives here (the actual authority); a read-only public copy still exports fine,
            // because the exchange carries public material only.
            X509Certificate2 chosen = null;
            var sawExpiredCopy = false;
            var now = DateTime.Now;
            if (candidates != null)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (candidate.NotAfter <= now)
                    {
                        sawExpiredCopy = true;
                        continue;
                    }

                    if (chosen == null || (!chosen.HasPrivateKey && candidate.HasPrivateKey))
                    {
                        chosen = candidate;
                    }
                }
            }

            if (chosen == null)
            {
                if (sawExpiredCopy)
                {
                    _log("Proxy authority certificate export refused: every LocalMachine\\Personal copy has expired.");
                    return Failure(request.CorrelationId, ProxyFetchErrorCodes.NoAuthority,
                        "The proxy authority certificate exists in LocalMachine\\Personal but has expired. Re-provision it on the Event Server machine and update the client machines' trusted roots (see the plugin README).");
                }

                _log("Proxy authority certificate export refused: no certificate with the authority name exists in LocalMachine\\Personal yet.");
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.NoAuthority,
                    "The proxy authority certificate does not exist yet on this Event Server. It is created the first time a proxied https target is used, or you can provision it once with scripts/provision-event-server-authority.ps1.");
            }

            var der = chosen.RawData;
            if (der == null || der.Length == 0)
            {
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.Error,
                    "The proxy authority certificate carried no DER.");
            }

            if (der.Length > ProxyCaExportLimits.MaxAuthorityDerBytes)
            {
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.Error,
                    $"The proxy authority certificate DER is implausibly large ({der.Length} bytes, cap {ProxyCaExportLimits.MaxAuthorityDerBytes}); refusing to transmit it.");
            }

            _log($"Exported the deployment proxy authority certificate (thumbprint {chosen.Thumbprint}, valid to {chosen.NotAfter:u}).");
            return new ProxyCaExportResponsePayload
            {
                CorrelationId = request.CorrelationId,
                Success = true,
                AuthorityDerBase64 = Convert.ToBase64String(der),
                AuthorityThumbprint = chosen.Thumbprint,
                NotBeforeUtc = chosen.NotBefore.ToUniversalTime(),
                NotAfterUtc = chosen.NotAfter.ToUniversalTime(),
            };
        }

        private static ProxyCaExportResponsePayload Failure(string correlationId, string errorCode, string message)
        {
            return new ProxyCaExportResponsePayload
            {
                CorrelationId = correlationId,
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = message,
            };
        }

        /// <summary>
        /// The production store read: every LocalMachine\Personal certificate carrying the fixed
        /// deployment authority CN. Read-only and OpenExistingOnly — export never provisions, so
        /// it never asks for store write access (that is the minting path's business, and the
        /// NETWORK SERVICE account could not get it anyway). The CN grammar is pinned by
        /// ProxyCaExportTests source guards against drifting from <see cref="ProxyLoopbackTls.AuthorityCommonName"/>.
        /// </summary>
        internal static IList<X509Certificate2> FindDeploymentAuthorityCertificates()
        {
            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = new List<X509Certificate2>();
                foreach (var certificate in store.Certificates.Find(
                    X509FindType.FindBySubjectName, ProxyLoopbackTls.AuthorityCommonName, false))
                {
                    found.Add(certificate);
                }

                return found;
            }
        }
    }
}
