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
    /// Ticket #56 (ADR-0002/ADR-0003): while the listener serves, allowlisted https CONNECT
    /// tunnels terminate at <see cref="ProxyLoopbackTls"/>, which asks the Event Server to sign
    /// locally generated keys and wraps each tunnel in TLS at loopback; the decrypted requests
    /// continue through the same fetch pipeline. The listener itself never touches a certificate
    /// store - trust of the deployment CA is a machine-level deployment concern, not session state.
    /// </summary>
    internal sealed class LoopbackProxyListener
    {
        private readonly Func<ProxyFetchQuery, Task<ProxyFetchResult>> _fetch;
        private readonly ProxyLoopbackTls _tls;
        private IReadOnlyList<string> _canonicalTargets;
        private TcpListener _listener;
        private volatile bool _stopping;

        internal int Port { get; private set; }

        internal LoopbackProxyListener(IReadOnlyList<string> canonicalTargets, Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch)
        {
            _canonicalTargets = canonicalTargets ?? new string[0];
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
            _tls = new ProxyLoopbackTls(RequestLeafAsync, message => EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), message));
        }

        /// <summary>Swap the allowlist gate live (an admin target edit needs no browser restart).</summary>
        internal void UpdateTargets(IReadOnlyList<string> canonicalTargets)
        {
            _canonicalTargets = canonicalTargets ?? new string[0];
        }

        /// <summary>Bind 127.0.0.1:0 and start accepting. The assigned port is available after.</summary>
        internal void Start()
        {
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

            // Leaf certificates and their local key containers die with the listener; the
            // deployment CA is shared machine state and deliberately untouched here.
            _tls.Dispose();
        }

        /// <summary>
        /// The ADR-0003 mint channel: ask the Event Server (over the same messaging client the
        /// fetches use) to sign this locally generated public key for the given origin.
        /// </summary>
        private static Task<ProxyLeafResponsePayload> RequestLeafAsync(string host, int port, byte[] subjectPublicKeyInfoDer)
        {
            var client = ProxyFetchClient.Active;
            if (client == null)
            {
                return Task.FromResult(new ProxyLeafResponsePayload
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = "The proxy fetch client is not connected to the Event Server.",
                });
            }

            return client.RequestLeafAsync(host, port, subjectPublicKeyInfoDer);
        }

        /// <summary>
        /// Ticket #57: the websocket-tunnel channel over the same messaging client the fetches
        /// use. Without a live client the upgrade fails as a plain 502 - never smuggled into
        /// the HTTP fetch path.
        /// </summary>
        private static Task<ProxyTunnelOpenOutcome> OpenTunnelAsync(ProxyForwardedRequest request)
        {
            var client = ProxyFetchClient.Active;
            if (client == null)
            {
                return Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = "The proxy fetch client is not connected to the Event Server.",
                });
            }

            return client.OpenTunnelAsync(request);
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
                            _tls.AcceptTunnelAsync,
                            OpenTunnelAsync);
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