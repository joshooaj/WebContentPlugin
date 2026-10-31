using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using VideoOS.Platform;

namespace WebContent.Client
{
    /// <summary>
    /// Loopback-only forwarding proxy listener for one proxied workspace browser profile (#54):
    /// bound to 127.0.0.1 with an OS-assigned (ephemeral) port, it is the address the browser
    /// profile's --proxy-server argument points at. Every request head is handled by the pure
    /// <see cref="ProxyForwardConnection"/> flow — parse, policy, fetch, render — so the only
    /// socket-coupled code here is accept/read/write, verified live (verification-matrix SC-1).
    ///
    /// Started when a proxied browser is created and stopped when that browser is disposed;
    /// WebView2 environment recreation reuses the same listener and port.
    ///
    /// Ticket #56 (ADR-0002): while the listener serves, it also terminates allowlisted https
    /// CONNECT tunnels itself — a <see cref="ProxyLoopbackTls"/> session CA plus per-host leaves
    /// wrap each tunnel in TLS at loopback, and the decrypted requests continue through the same
    /// fetch pipeline. The session CA is trusted in the user's root store only for the listener's
    /// lifetime (and only when https targets exist); a listener without https targets never mints
    /// a CA at all.
    /// </summary>
    internal sealed class LoopbackProxyListener
    {
        private readonly Func<ProxyFetchQuery, Task<ProxyFetchResult>> _fetch;
        private readonly ProxyLoopbackTls _tls;
        private IReadOnlyList<string> _canonicalTargets;
        private TcpListener _listener;
        private volatile bool _stopping;
        private bool _trustInstalled;

        internal int Port { get; private set; }

        internal LoopbackProxyListener(IReadOnlyList<string> canonicalTargets, Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch)
        {
            _canonicalTargets = canonicalTargets ?? new string[0];
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
            _tls = new ProxyLoopbackTls(message => EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), message));
        }

        /// <summary>Swap the allowlist gate live (an admin target edit needs no browser restart).</summary>
        internal void UpdateTargets(IReadOnlyList<string> canonicalTargets)
        {
            _canonicalTargets = canonicalTargets ?? new string[0];
            ReconcileTrust();
        }

        /// <summary>Bind 127.0.0.1:0 and start accepting. The assigned port is available after.</summary>
        internal void Start()
        {
            ReconcileTrust();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        internal void Stop()
        {
            _stopping = true;
            try
            {
                _listener?.Stop();
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), $"Proxy listener on port {Port} stopped with: {exception.Message}");
            }

            // The session CA never outlives the listener: untrust first (browser sessions on it are
            // over), then drop the leaves and CA keys.
            if (_trustInstalled)
            {
                _trustInstalled = false;
                ProxyLoopbackTrust.Remove(_tls.AuthorityCertificate);
            }

            _tls.Dispose();
        }

        /// <summary>
        /// The user must trust the session CA exactly while https targets could be tunnelled:
        /// install when any https target is allowlisted, remove when none is. Idempotent.
        /// </summary>
        private void ReconcileTrust()
        {
            var needsTrust = false;
            foreach (var target in _canonicalTargets)
            {
                if (target != null && target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    needsTrust = true;
                    break;
                }
            }

            if (needsTrust && !_trustInstalled)
            {
                _trustInstalled = ProxyLoopbackTrust.Install(_tls.AuthorityCertificate);
            }
            else if (!needsTrust && _trustInstalled)
            {
                _trustInstalled = false;
                ProxyLoopbackTrust.Remove(_tls.AuthorityCertificate);
            }
        }

        private async Task AcceptLoopAsync()
        {
            var listener = _listener;
            while (!_stopping)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    // Stop() disposes the accept socket: expected teardown, not a fault.
                    if (!_stopping)
                    {
                        EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), $"Proxy listener on port {Port} accept failed.");
                    }

                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    using (var stream = client.GetStream())
                    {
                        await ProxyForwardConnection.HandleAsync(
                            stream,
                            stream,
                            _canonicalTargets,
                            _fetch,
                            message => EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), $"Proxy listener on port {Port}: {message}"),
                            _tls.AcceptTunnelAsync);
                    }
                }
                catch (Exception exception)
                {
                    EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), $"Proxy connection on port {Port} aborted: {exception.Message}");
                }
            }
        }
    }
}
