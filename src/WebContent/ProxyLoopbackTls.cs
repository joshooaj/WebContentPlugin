using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using WebContent.Client;

namespace WebContent
{
    /// <summary>
    /// Asks the Event Server to mint a loopback leaf for one origin, carrying the locally
    /// generated SubjectPublicKeyInfo (ADR-0003). Implemented over MIP messaging by
    /// <see cref="ProxyFetchClient.RequestLeafAsync"/>; injectable for tests.
    /// </summary>
    internal delegate Task<ProxyLeafResponsePayload> ProxyLeafMintChannel(string host, int port, byte[] subjectPublicKeyInfoDer);

    /// <summary>
    /// Ticket #56 (ADR-0003): owns the certificates that let the loopback listener terminate a
    /// browser's CONNECT tunnel for an allowlisted https target.
    ///
    /// Shape: the signing authority lives on the Event Server (its private key never reaches this
    /// machine); per origin this process generates an exchange key pair, sends only the public
    /// half over the mint channel, and binds the returned certificate DER to the local key. The
    /// chain the browser validates is leaf -> deployment CA, whose certificate the operator has
    /// imported into the machine's trusted-root store.
    ///
    /// Leaves are cached per (host, port) for the browser session; key containers and certificates
    /// are disposed with the manager. When a mint succeeds but the deployment CA is missing from
    /// the trust stores, the tunnels still serve - the browser shows its own certificate error
    /// (fail-closed) and the log names the one-time elevated import that fixes it.
    /// </summary>
    internal sealed class ProxyLoopbackTls : IDisposable
    {
        /// <summary>Common name of the deployment CA; matches what the Event Server provisions.</summary>
        internal const string AuthorityCommonName = "XProtect WebContent Plugin Loopback Proxy";

        private readonly ProxyLeafMintChannel _mint;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly Dictionary<string, X509Certificate2> _leaves =
            new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);
        private readonly List<IDisposable> _leafOwners = new List<IDisposable>();
        private readonly HashSet<string> _authorityTrustReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        internal ProxyLoopbackTls(ProxyLeafMintChannel mint, Action<string> log)
        {
            _mint = mint ?? throw new ArgumentNullException(nameof(mint));
            _log = log ?? delegate { };
        }

        /// <summary>
        /// The leaf presented for tunnels to this origin. Cached per host:port (host part
        /// case-insensitive): a browser reconnecting reuses the certificate instead of growing
        /// the process or re-asking the Event Server.
        /// </summary>
        internal async Task<X509Certificate2> GetOrCreateLeafCertificateAsync(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Leaf host must be non-empty.", nameof(host));
            }

            var cacheKey = host + ":" + port;
            lock (_gate)
            {
                ThrowIfDisposed();

                X509Certificate2 cached;
                if (_leaves.TryGetValue(cacheKey, out cached))
                {
                    return cached;
                }
            }

            // Mint outside the lock: the exchange is a network roundtrip, and a duplicate mint
            // under a race is harmless (the first insertion wins).
            var key = ProxyLoopbackClientKey.Create();
            ProxyLeafResponsePayload response;
            try
            {
                response = await _mint(host, port, key.SubjectPublicKeyInfoDer).ConfigureAwait(false);
            }
            catch
            {
                key.Dispose();
                throw;
            }

            if (response == null || !response.Success || string.IsNullOrEmpty(response.LeafDerBase64))
            {
                key.Dispose();
                throw new InvalidOperationException(
                    "The Event Server refused the loopback leaf for " + cacheKey +
                    " (code=" + (response == null ? "null" : response.ErrorCode) +
                    "): " + (response == null ? "no response" : response.ErrorMessage));
            }

            X509Certificate2 leaf;
            try
            {
                leaf = key.AttachLeaf(Convert.FromBase64String(response.LeafDerBase64));
            }
            catch
            {
                key.Dispose();
                throw;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    leaf.Dispose();
                    key.Dispose();
                    throw new ObjectDisposedException(nameof(ProxyLoopbackTls));
                }

                X509Certificate2 existing;
                if (_leaves.TryGetValue(cacheKey, out existing))
                {
                    leaf.Dispose();
                    key.Dispose();
                    return existing;
                }

                _leaves[cacheKey] = leaf;
                _leafOwners.Add(new ProxyLoopbackLeafOwner(leaf, key));
            }

            _log("WebContent proxy: Event Server minted loopback TLS leaf for " + cacheKey);
            ReportAuthorityTrustState(response.AuthorityThumbprint);
            return leaf;
        }

        /// <summary>
        /// Wraps an established CONNECT tunnel in a server-side TLS stream using the leaf for the
        /// tunnel origin's host. <paramref name="seed"/> carries the bytes already pulled off the
        /// socket while framing the CONNECT request (the first fragment of the browser's ClientHello
        /// when it pipelined behind the CONNECT); they are replayed into the TLS read.
        /// On success the returned stream owns the tunnel's TLS traffic; the raw stream stays owned
        /// by the caller (ProxyForwardConnection closes it).
        /// </summary>
        internal async Task<Stream> AcceptTunnelAsync(Stream tunnel, byte[] seed, Uri origin)
        {
            if (tunnel == null)
            {
                throw new ArgumentNullException(nameof(tunnel));
            }

            if (origin == null || !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Tunnel origin must be an https URI.", nameof(origin));
            }

            var certificate = await GetOrCreateLeafCertificateAsync(origin.IdnHost, origin.Port).ConfigureAwait(false);

            var source = seed != null && seed.Length > 0
                ? (Stream)new PrefixedStream(seed, tunnel)
                : tunnel;

            var ssl = new SslStream(source, true);
            try
            {
                await ssl.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, false)
                    .ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose();
                throw;
            }

            _log("WebContent proxy: tunnel TLS established for " + origin.IdnHost);
            return ssl;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _leaves.Clear();

                foreach (var owner in _leafOwners)
                {
                    try { owner.Dispose(); } catch { }
                }

                _leafOwners.Clear();
            }
        }

        /// <summary>
        /// Log the machine's trust posture for this deployment CA exactly once per thumbprint:
        /// either confirmation, or the exact one-time elevated step the operator still owes.
        /// Reading the root stores never needs elevation; only the import does.
        /// </summary>
        private void ReportAuthorityTrustState(string authorityThumbprint)
        {
            if (string.IsNullOrEmpty(authorityThumbprint))
            {
                return;
            }

            lock (_gate)
            {
                if (!_authorityTrustReported.Add(authorityThumbprint))
                {
                    return;
                }
            }

            if (ProxyLoopbackTrust.IsRootTrusted(authorityThumbprint, _log))
            {
                _log("WebContent proxy: deployment CA " + authorityThumbprint + " is present in this machine's trusted roots.");
                return;
            }

            _log(
                "WebContent proxy: deployment CA " + authorityThumbprint +
                " is NOT in this machine's trusted roots. Proxied https targets will show a browser certificate error until an administrator imports the CA certificate exported by the Event Server: " +
                "Import-Certificate -FilePath <ca.cer> -CertStoreLocation Cert:\\LocalMachine\\Root");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ProxyLoopbackTls));
            }
        }

        /// <summary>
        /// Feeds the bytes the CONNECT framing already consumed before anything else is read from
        /// the underlying stream. Writes go straight through; the underlying stream stays owned by
        /// the caller.
        /// </summary>
        private sealed class PrefixedStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly Stream _inner;
            private int _offset;

            internal PrefixedStream(byte[] prefix, Stream inner)
            {
                _prefix = prefix;
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = _prefix.Length - _offset;
                if (remaining <= 0)
                {
                    return _inner.Read(buffer, offset, count);
                }

                var take = Math.Min(remaining, count);
                Buffer.BlockCopy(_prefix, _offset, buffer, offset, take);
                _offset += take;
                return take;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                // The raw tunnel stream is the caller's (ProxyForwardConnection disposes it);
                // only the prefix buffer is ours, and GC handles it.
                if (disposing)
                {
                    // SslStream disposes this wrapper; deliberately do not dispose _inner.
                }

                base.Dispose(disposing);
            }
        }
    }
}
