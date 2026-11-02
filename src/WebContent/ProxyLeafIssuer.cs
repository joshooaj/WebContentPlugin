using System;

namespace WebContent
{
    /// <summary>
    /// Ticket #56 (ADR-0003): the Event Server half of the loopback-leaf mint. A Smart Client
    /// whose loopback listener is terminating an allowlisted https CONNECT asks the Event Server
    /// to sign a leaf certificate for that origin; the request carries only the client-generated
    /// SubjectPublicKeyInfo, so the leaf's private half never leaves the client machine.
    ///
    /// The workspace allowlist IS the minting policy: an origin is mintable exactly when the same
    /// union snapshot that gates fetches allowlists it (fail-closed before its first successful
    /// resolve, like the fetcher). The authority is the long-lived deployment CA held in the
    /// Event Server machine's LocalMachine\Personal store; when it cannot be loaded or created
    /// (store permissions, a service account that is not LocalSystem), the answer is the
    /// NoAuthority error, never an exception on the messaging thread.
    /// </summary>
    internal sealed class ProxyLeafIssuer
    {
        private readonly ProxyServerAllowlist _allowlist;
        private readonly Func<Authority> _authorityProvider;
        private readonly Action<string> _log;

        /// <param name="allowlist">The same union-gated allowlist that gates fetches.</param>
        /// <param name="authorityProvider">Supplies the deployment authority; throws when it is
        /// unavailable, which is answered as NoAuthority rather than propagating.</param>
        internal ProxyLeafIssuer(ProxyServerAllowlist allowlist, Func<Authority> authorityProvider, Action<string> log)
        {
            _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
            _authorityProvider = authorityProvider ?? throw new ArgumentNullException(nameof(authorityProvider));
            _log = log ?? delegate { };
        }

        /// <summary>
        /// Validate and mint. Never throws: every rejection becomes a failure payload with a
        /// machine-readable code (BadRequest / NotAllowed / NoAuthority / Error).
        /// </summary>
        internal ProxyLeafResponsePayload Issue(ProxyLeafRequestPayload request)
        {
            if (request == null || string.IsNullOrEmpty(request.CorrelationId))
            {
                return Failure(null, ProxyFetchErrorCodes.BadRequest, "The leaf request carried no usable payload.");
            }

            // Host/Port bounds: the CONNECT origin as the browser resolved it. Port must be a real
            // TCP port; a blank host would mint a certificate for nothing.
            if (string.IsNullOrWhiteSpace(request.Host) || request.Port <= 0 || request.Port > 65535)
            {
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.BadRequest,
                    "The leaf request origin (host and port) is malformed.");
            }

            byte[] spki;
            if (string.IsNullOrEmpty(request.SpkiBase64))
            {
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.BadRequest,
                    "The leaf request carried no client public key.");
            }

            try
            {
                spki = Convert.FromBase64String(request.SpkiBase64);
            }
            catch (FormatException)
            {
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.BadRequest,
                    "The leaf request carried a public key that is not valid base64.");
            }

            // The minting gate: exactly the origins some proxied workspace allowlists, matched on
            // scheme+host+port like every fetch. The authority signs for the origin the allowlist
            // blessed, so a crafted host can never acquire a trusted certificate for elsewhere.
            Uri origin;
            if (!Uri.TryCreate($"https://{request.Host}:{request.Port}/", UriKind.Absolute, out origin))
            {
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.BadRequest,
                    "The leaf request origin (host and port) is malformed.");
            }

            string refuseReason;
            if (!_allowlist.IsAllowed(origin, out refuseReason))
            {
                _log($"Leaf mint refused for {request.Host}:{request.Port} - {refuseReason}");
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.NotAllowed, refuseReason);
            }

            Authority authority;
            try
            {
                authority = _authorityProvider();
            }
            catch (Exception exception)
            {
                _log($"Loopback proxy authority unavailable: {exception.Message}");
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.NoAuthority,
                    "The Event Server cannot load or create the proxy certificate authority.");
            }

            if (authority == null)
            {
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.NoAuthority,
                    "The Event Server has no proxy certificate authority.");
            }

            try
            {
                var leafDer = authority.MintLeafForPublicKey(request.Host, spki);
                var authorityDer = authority.Certificate.RawData;
                _log($"Minted loopback proxy leaf for {request.Host}:{request.Port}.");
                return new ProxyLeafResponsePayload
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    LeafDerBase64 = Convert.ToBase64String(leafDer),
                    AuthorityDerBase64 = Convert.ToBase64String(authorityDer),
                    AuthorityThumbprint = authority.Certificate.Thumbprint,
                };
            }
            catch (ArgumentException argumentException)
            {
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.BadRequest, argumentException.Message);
            }
            catch (Exception exception)
            {
                _log($"Loopback proxy leaf mint failed for {request.Host}:{request.Port}: {exception.Message}");
                return Failure(request.CorrelationId, ProxyFetchErrorCodes.Error,
                    "The Event Server could not mint the loopback proxy leaf: " + exception.Message);
            }
        }

        private static ProxyLeafResponsePayload Failure(string correlationId, string errorCode, string message)
        {
            return new ProxyLeafResponsePayload
            {
                CorrelationId = correlationId,
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = message,
            };
        }
    }
}