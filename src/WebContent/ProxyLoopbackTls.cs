using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Ticket #56 (ADR-0002): mints and owns the certificates that let the loopback listener
    /// terminate a browser's CONNECT tunnel for an allowlisted https target.
    ///
    /// Shape: one ephemeral session CA (the authority) plus per-host leaf certificates signed by
    /// it. The authority is installed into the user's trusted-root store for the lifetime of the
    /// proxied browser session (that install lives with the listener/browser lifecycle, not here),
    /// so the chain Chromium sees is leaf -> authority, validated with full chain semantics.
    ///
    /// Everything is in-memory and per-process: nothing about these certificates outlives the
    /// session, and a fresh authority is minted every session. Leaf private keys are kept in a
    /// form Schannel accepts for server authentication (persisted CAPI containers on .NET
    /// Framework; a PFX roundtrip on modern .NET) - an ephemeral in-memory key fails
    /// AuthenticateAsServer with "the platform does not support ephemeral keys".
    /// </summary>
    internal sealed class ProxyLoopbackTls : IDisposable
    {
        /// <summary>Common name of the session authority; pinned by tests and by the trust-store install.</summary>
        internal const string AuthorityCommonName = "XProtect WebContent Plugin Loopback Proxy";

        private static readonly Oid ServerAuthEku = new Oid("1.3.6.1.5.5.7.3.1");

        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly Dictionary<string, X509Certificate2> _leaves =
            new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);
        private readonly List<IDisposable> _leafOwners = new List<IDisposable>();

        private Authority _authority;
        private bool _disposed;

        internal ProxyLoopbackTls(Action<string> log)
        {
            _log = log ?? delegate { };
        }

        /// <summary>The session CA. Created on first use; callers never sign with it directly.</summary>
        internal X509Certificate2 AuthorityCertificate
        {
            get
            {
                lock (_gate)
                {
                    return EnsureAuthority().Certificate;
                }
            }
        }

        /// <summary>
        /// The leaf presented for tunnels to this host. Cached per host (case-insensitive): a browser
        /// reconnecting to the same target reuses the certificate instead of growing the process.
        /// </summary>
        internal X509Certificate2 GetOrCreateLeafCertificate(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Leaf host must be non-empty.", nameof(host));
            }

            lock (_gate)
            {
                ThrowIfDisposed();

                X509Certificate2 leaf;
                if (_leaves.TryGetValue(host, out leaf))
                {
                    return leaf;
                }

                var owner = EnsureAuthority().CreateLeaf(host);
                _leaves[host] = owner.Certificate;
                _leafOwners.Add(owner);
                _log("WebContent proxy: minted loopback TLS leaf for " + host);
                return owner.Certificate;
            }
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

            var certificate = GetOrCreateLeafCertificate(origin.IdnHost);

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

                if (_authority != null)
                {
                    try { _authority.Dispose(); } catch { }
                    _authority = null;
                }
            }
        }

        private Authority EnsureAuthority()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ProxyLoopbackTls));
            }

            return _authority ?? (_authority = Authority.Create(_log));
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
