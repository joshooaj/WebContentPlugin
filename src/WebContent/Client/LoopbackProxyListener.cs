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

        /// <summary>
        /// Ticket #69: the one browser-view session this listener fronts. Its id rides every fetch
        /// and tunnel this browser issues, and the Event Server groups block reports, traffic, and
        /// live status under it; opened on Start, closed on Stop.
        /// </summary>
        private readonly ProxyViewSessionContext _session;
        private readonly ProxyBlockReporter _blocks;

        /// <summary>
        /// Ticket #83: the per-browser-view blocked-resource tally this listener reports
        /// through. It forwards every refusal to <see cref="_blocks"/> exactly as #69 shipped
        /// it and counts locally so the view's setup-mode indicator needs no ES roundtrip.
        /// Owned by the browser (stable across listener rewires), attached here for the life
        /// of this listener.
        /// </summary>
        private readonly ProxyBlockTally _tally;

        /// <summary>
        /// #72: the browser view's deployment-CA trust posture this listener feeds on every leaf
        /// mint (and re-checks after <see cref="InvalidateLeafCache"/>). Owned by the browser,
        /// like the tally; the Smart Client banner renders it.
        /// </summary>
        private readonly ProxyCaTrustState _caTrust;
        private IReadOnlyList<string> _canonicalTargets;
        private IReadOnlyList<string> _vmsFloorHosts;
        private TcpListener _listener;
        private volatile bool _stopping;

        internal int Port { get; private set; }

        internal ProxyViewSessionContext Session => _session;

        /// <summary>
        /// #88: the live gate state the view-layer https refusal consults - the exact entries the
        /// listener itself matches, swapped live by <see cref="UpdateTargets"/>. The view and the
        /// listener consume ONE <see cref="ProxyPolicy"/> matcher over ONE entry set, so a request
        /// the view refuses is a request the listener would have refused (and vice versa).
        /// </summary>
        internal IReadOnlyList<string> CanonicalTargets => _canonicalTargets;

        internal IReadOnlyList<string> VmsFloorHosts => _vmsFloorHosts;

        internal LoopbackProxyListener(
            IReadOnlyList<string> canonicalTargets,
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch,
            ProxyViewSessionContext session = null,
            ProxyBlockTally tally = null,
            IEnumerable<string> vmsFloorHosts = null,
            ProxyCaTrustState caTrust = null)
        {
            _canonicalTargets = canonicalTargets ?? new string[0];
            _vmsFloorHosts = vmsFloorHosts == null ? new string[0] : (IReadOnlyList<string>)new List<string>(vmsFloorHosts);
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
            _caTrust = caTrust;
            _session = session ?? new ProxyViewSessionContext
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ClientMachine = Environment.MachineName,
            };
            _blocks = new ProxyBlockReporter(
                _session,
                payload =>
                {
                    var client = ProxyFetchClient.Active;
                    if (client != null)
                    {
                        client.SendBlockReport(payload);
                    }
                },
                message => EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), message));
            _tally = tally ?? new ProxyBlockTally(message => EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), message));
            _tally.SetForwarder(_blocks);
            _tls = new ProxyLoopbackTls(RequestLeafAsync, message => EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), message), _caTrust);

            // #80: a browser view whose mint just found the deployment CA untrusted on this
            // machine files exactly one CertUntrusted block report per posture flip. The Event
            // Server dedups it per machine (ten untrusted views on one workstation count once),
            // and the Management Client aggregates those machines for the exporting admin.
            // The report bypasses the #83 tally on purpose: the tally counts URL refusals for
            // the user-facing indicator, and the CA banner already owns this condition's local
            // surfacing - double counting would mislabel a trust problem as a blocked resource.
            if (_caTrust != null)
            {
                _caTrust.Changed += OnProxyCaTrustChanged;
            }
        }

        /// <summary>
        /// #80: the flip-to-untrusted report. Fires on the mint's socket thread; every fault is
        /// already swallowed inside <see cref="ProxyBlockReporter"/> and the state holder, and a
        /// posture that is not the confirmed-untrusted condition (fixed, cleared, unknown
        /// authority) sends nothing. The origin is the mint target that produced the verdict -
        /// the https origin behind the browser's certificate error.
        /// </summary>
        private void OnProxyCaTrustChanged()
        {
            if (_caTrust == null || !_caTrust.IsUntrustedConfirmed)
            {
                return;
            }

            var mintOrigin = _caTrust.LastMintOrigin;
            if (string.IsNullOrEmpty(mintOrigin))
            {
                // No flip-to-untrusted path exists without a mint naming an origin; belt-and-
                // braces keeps a synthetic entry out of the admin's blocked ring anyway.
                return;
            }

            if (!Uri.TryCreate("https://" + mintOrigin, UriKind.Absolute, out var origin))
            {
                return;
            }

            _blocks.Report(origin, ProxyBlockReasons.CertUntrusted);
        }

        /// <summary>#72: drop the cached leaves so the next proxied https tunnel re-mints and
        /// re-checks the deployment CA trust posture (called when a fix made the CA trusted).</summary>
        internal void InvalidateLeafCache()
        {
            _tls.InvalidateLeafCache();
        }

        /// <summary>Swap the allowlist gate live (an admin target edit needs no browser restart).
        /// #73: the wildcard floor travels with the entries - both come from the profile.</summary>
        internal void UpdateTargets(IReadOnlyList<string> canonicalTargets, IEnumerable<string> vmsFloorHosts = null)
        {
            _canonicalTargets = canonicalTargets ?? new string[0];
            _vmsFloorHosts = vmsFloorHosts == null ? new string[0] : (IReadOnlyList<string>)new List<string>(vmsFloorHosts);
        }

        /// <summary>Bind 127.0.0.1:0 and start accepting. The assigned port is available after.</summary>
        internal void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);

            // #69: announce the session (best effort - an unconnected channel surfaces later as
            // ServerUnavailable fetches, and the Event Server's idle backstop never sees this session).
            ProxySessionRegistry.Active.Open(_session);
            var client = ProxyFetchClient.Active;
            if (client != null)
            {
                client.SendSessionEvent(new ProxySessionEventPayload
                {
                    SessionId = _session.SessionId,
                    Kind = ProxySessionEventKinds.Open,
                    WorkspaceItemId = _session.WorkspaceItemId,
                    WorkspaceFingerprint = _session.WorkspaceFingerprint,
                    ClientMachine = _session.ClientMachine,
                });
            }
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

            // #69: close the session (send before unregistering; while the channel lives the
            // Event Server prunes its aggregates immediately instead of waiting for the sweep).
            ProxySessionRegistry.Active.Close(_session.SessionId);
            var client = ProxyFetchClient.Active;
            if (client != null)
            {
                client.SendSessionEvent(new ProxySessionEventPayload
                {
                    SessionId = _session.SessionId,
                    Kind = ProxySessionEventKinds.Close,
                    WorkspaceItemId = _session.WorkspaceItemId,
                    WorkspaceFingerprint = _session.WorkspaceFingerprint,
                });
            }

            // #83: the tally outlives this listener when the browser is rewired to a new one;
            // detach so a stopped listener's ES reporter is never a forward target.
            _tally.SetForwarder(null);

            // #80: same ownership split as the tally - the trust state outlives the listener, so
            // unhook the flip report here or a rewired browser would report through every dead
            // listener that ever fronted it.
            if (_caTrust != null)
            {
                _caTrust.Changed -= OnProxyCaTrustChanged;
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
        private Task<ProxyTunnelOpenOutcome> OpenTunnelAsync(ProxyForwardedRequest request)
        {
            // #69: stamp the session so the Event Server attributes the tunnel's traffic; the
            // field rides to the open payload in ProxyFetchClient.OpenTunnelAsync.
            request.SessionId = _session.SessionId;

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

        /// <summary>
        /// #69: stamp the session on every fetch query before it crosses the messaging seam, so
        /// the Event Server counts the served request toward this session's live status. #65: the
        /// workspace item id rides along for the same crossing - rule-trigger rows are evaluated
        /// against the workspace the issuing view belongs to.
        /// </summary>
        private Task<ProxyFetchResult> FetchWithSession(ProxyFetchQuery query)
        {
            query.SessionId = _session.SessionId;
            query.WorkspaceItemId = _session.WorkspaceItemId;
            return _fetch(query);
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
                            FetchWithSession,
                            message => EnvironmentManager.Instance.Log(true, nameof(LoopbackProxyListener), $"Proxy listener on port {Port}: {message}"),
                            _tls.AcceptTunnelAsync,
                            OpenTunnelAsync,
                            _tally,
                            _vmsFloorHosts);
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
