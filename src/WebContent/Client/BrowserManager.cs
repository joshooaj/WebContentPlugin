using System;
using System.Net.Sockets;
using System.Threading.Tasks;

using Microsoft.Web.WebView2.Wpf;

using VideoOS.Platform;

namespace WebContent.Client
{
    public static class BrowserManager
    {
        private static readonly BrowserRegistry<BrowserInstance> _registry = new BrowserRegistry<BrowserInstance>();

        /// <summary>
        /// #95 (ADR-0004 point 3): every proxied browser creation/re-enable consults the boundary's
        /// listener claim BEFORE a loopback listener is bound - the first process inside a boundary
        /// owns the slot and binds the port its claim publishes, later processes join that port with
        /// the identical --proxy-server argument instead of colliding on the shared profile (#87).
        /// </summary>
        private static readonly ProxyListenerLeaseProvider _leases = new ProxyListenerLeaseProvider();

        public static BrowserInstance GetBrowser(Guid id)
        {
            return GetBrowser(id, new Uri(string.Empty), false);
        }

        public static BrowserInstance GetBrowser(Guid id, Uri initialAddress, bool isIsolated)
        {
            return GetBrowser(id, initialAddress, isIsolated, null);
        }

        /// <summary>
        /// Ticket #54: when <paramref name="proxyProfile"/> is present the browser profile gets a
        /// dedicated loopback forwarding listener and the --proxy-server argument pointed at it,
        /// and isolation is forced so the argument cannot leak into another workspace's browser.
        /// If the listener cannot start, no browser is created at all — an unproxied browser for
        /// a proxied workspace would silently defeat the "never direct" rule.
        /// </summary>
        internal static BrowserInstance GetBrowser(Guid id, Uri initialAddress, bool isIsolated, ProxyWorkspaceProfile proxyProfile)
        {
            try
            {
                return _registry.GetOrCreate(id, guid => CreateBrowser(guid, initialAddress, isIsolated, proxyProfile));
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(BrowserManager), ex);
            }
            return null;
        }

        private static BrowserInstance CreateBrowser(Guid id, Uri initialAddress, bool isIsolated, ProxyWorkspaceProfile proxyProfile)
        {
            // #83: one blocked-resource tally per browser view instance, owned by the browser so
            // it survives a listener rewire; the current listener forwards through it. Stays
            // empty for a non-proxied browser - there is nothing client-side to refuse.
            var blocks = new ProxyBlockTally(message => EnvironmentManager.Instance.Log(true, nameof(BrowserManager), message));

            // #72: one deployment-CA trust posture per proxied browser, likewise browser-owned
            // so the banner survives a listener rewire; every listener's leaf mint republishes
            // it. A non-proxied browser keeps none - there is no CA behind it.
            var caTrust = proxyProfile == null ? null : new ProxyCaTrustState();

            LoopbackProxyListener listener = null;
            string proxyArguments = null;
            string proxyUserDataFolder = null;
            string proxyClaimPath = null;
            if (proxyProfile != null)
            {
                // #95: consult the boundary's listener claim BEFORE any listener is bound. One
                // process owns (and publishes) the port; every other process inside the boundary
                // joins it with the identical argument, which is exactly what makes WebView2
                // ATTACH to the owner's browser process instead of failing options-match (#87).
                proxyUserDataFolder = ProxiedUserDataFolder(id);
                var claimPath = BrowserProfileIdentity.GetListenerClaimPath(id);
                var lease = _leases.Acquire(claimPath, id, ProxyBrowserArguments.PortIndependentFingerprint());
                foreach (var audit in lease.AuditLines)
                {
                    EnvironmentManager.Instance.Log(true, nameof(BrowserManager), audit);
                }

                if (lease.Role == ProxyListenerLeaseRole.Joined)
                {
                    // No own listener, and so no own #69 session or block-report forwarder: every
                    // proxied fetch rides the OWNER's listener and Event Server channel, one
                    // diagnostics session per proxied profile (ADR-0004 point 4, accepted).
                    proxyArguments = ProxyBrowserArguments.For(lease.Port);
                    EnvironmentManager.Instance.Log(
                        false,
                        nameof(BrowserManager),
                        $"Proxied browser {id} joined the boundary owner's loopback listener on 127.0.0.1:{lease.Port} (claim '{claimPath}'); this process binds no listener and reports no own session, {proxyProfile.CanonicalTargets.Count} allowlisted target(s), boundary profile '{proxyUserDataFolder}'.");
                }
                else
                {
                    // #69: one diagnostics session per proxied browser, attributed to the workspace item
                    // the profile was resolved for. Block reports, session lifecycle, and traffic
                    // attribution all hang off its id; the Event Server backstops it with an idle sweep.
                    listener = new LoopbackProxyListener(
                        proxyProfile.CanonicalTargets,
                        FetchThroughMessaging,
                        new ProxyViewSessionContext
                        {
                            SessionId = Guid.NewGuid().ToString("N"),
                            WorkspaceItemId = proxyProfile.WorkspaceItemId,
                            WorkspaceFingerprint = proxyProfile.WorkspaceFingerprint,
                            ClientMachine = Environment.MachineName,
                        },
                        blocks,
                        proxyProfile.VmsFloorHosts,
                        caTrust);
                    proxyClaimPath = StartListenerOnLeasedPort(listener, lease, claimPath, id);
                    proxyArguments = ProxyBrowserArguments.For(listener.Port);
                    EnvironmentManager.Instance.Log(
                        false,
                        nameof(BrowserManager),
                        $"Proxied browser {id}: loopback listener on 127.0.0.1:{listener.Port} ({(proxyClaimPath != null ? "claim owner" : "ephemeral fallback - no claim")}), {proxyProfile.CanonicalTargets.Count} allowlisted target(s), boundary profile '{proxyUserDataFolder}'.");
                }
            }

            try
            {
                var instance = new WebView2
                {
                    CreationProperties = new CoreWebView2CreationProperties
                    {
                        // A proxied profile gets its own user-data folder: WebView2 shares one
                        // browser process per user-data folder, and additional browser arguments
                        // must match across every WebView in it — a proxied profile cannot share
                        // the default folder with unprefixed browsers, nor two proxied profiles
                        // with different listener ports (#54).
                        UserDataFolder = proxyUserDataFolder ?? BrowserProfileIdentity.GetUserDataFolder(),
                        ProfileName = ShouldIsolateProfile(isIsolated, proxyArguments != null) ? id.ToString() : string.Empty,
                        AdditionalBrowserArguments = proxyArguments,
                    },
                    Source = initialAddress,
                };

                var browser = new BrowserInstance(instance, id);
                browser.ProxyListener = listener;
                browser.ProxyBlocks = blocks;
                browser.ProxyCaTrust = caTrust;
                browser.ProxyBrowserArguments = proxyArguments;
                browser.ProxyUserDataFolder = proxyUserDataFolder;
                browser.ProxyClaimPath = proxyClaimPath;
                browser.ProxyViewTargets = proxyProfile?.CanonicalTargets;
                browser.ProxyViewFloorHosts = proxyProfile?.VmsFloorHosts;
                return browser;
            }
            catch (Exception)
            {
                listener?.Stop();
                if (proxyClaimPath != null)
                {
                    // A browser that was never created must not leave a claim naming a port
                    // nobody will serve.
                    _leases.Release(proxyClaimPath);
                }
                throw;
            }
        }

        /// <summary>
        /// Starts the owner's listener on the EXACT port its claim published - the joiner bakes
        /// that port into its identical argument, so a bind elsewhere would strand every joiner.
        /// The reservation is a two-step race against processes outside the boundary (any local
        /// process may grab the freed port between probe and bind); on a refused bind the claim
        /// is released and the listener falls back to today's ephemeral posture, so a published
        /// claim never describes a port that was never bound. Returns the claim path to track
        /// for teardown, or null when this browser runs unclaimed.
        /// </summary>
        private static string StartListenerOnLeasedPort(LoopbackProxyListener listener, ProxyListenerLease lease, string claimPath, Guid id)
        {
            if (lease.Role == ProxyListenerLeaseRole.Owner)
            {
                try
                {
                    listener.Start(lease.Port);
                    return claimPath;
                }
                catch (SocketException ex)
                {
                    EnvironmentManager.Instance.Log(
                        true,
                        nameof(BrowserManager),
                        $"Proxied browser {id}: the claimed loopback port {lease.Port} refused to bind ({ex.Message}); releasing the claim and binding an ephemeral port instead - a claim must never name a port nobody serves.");
                    _leases.Release(claimPath);
                }
            }

            listener.Start();
            return null;
        }

        /// <summary>
        /// Drops this browser's claim slot when the browser owned one (teardown paths). A joined
        /// browser owns nothing to release; a browser whose slot was already taken over keeps the
        /// takeover intact (<see cref="ProxyListenerLeaseProvider.ReleaseOwnClaimFile"/>).
        /// </summary>
        private static void ReleaseOwnClaim(BrowserInstance browser)
        {
            var claimPath = browser.ProxyClaimPath;
            browser.ProxyClaimPath = null;
            if (claimPath != null)
            {
                _leases.Release(claimPath);
            }
        }

        /// <summary>
        /// One rule for both creation and recreation: a proxied browser is always profile-isolated,
        /// so its --proxy-server argument cannot leak into another workspace's browser.
        /// </summary>
        internal static bool ShouldIsolateProfile(bool isolateSetting, bool proxied)
        {
            return isolateSetting || proxied;
        }

        /// <summary>
        /// Reconcile an existing (background-retained) browser with the workspace's current proxy
        /// config: an admin toggling the proxy on/off mid-session must not leave the reopened view
        /// on the other configuration's routing ("never direct"). Starts/stops the listener and
        /// switches the profile arguments; returns true when the WebView must be recreated to pick
        /// the change up. A target-list edit alone needs no recreation — the listener gate is live.
        /// </summary>
        internal static bool ApplyProxyProfile(BrowserInstance browser, ProxyWorkspaceProfile proxyProfile)
        {
            if (browser == null)
            {
                return false;
            }

            if (proxyProfile != null)
            {
                if (browser.ProxyBrowserArguments != null)
                {
                    browser.ProxyListener?.UpdateTargets(proxyProfile.CanonicalTargets, proxyProfile.VmsFloorHosts);
                    // #95: a JOINED browser has no listener of its own to update; keep its view-side
                    // #88 gate copies current so the gate follows a target edit without a re-selection.
                    browser.ProxyViewTargets = proxyProfile.CanonicalTargets;
                    browser.ProxyViewFloorHosts = proxyProfile.VmsFloorHosts;
                    return false;
                }

                // #83: a re-enabled proxy starts a fresh tally - stale refusals from before the
                // proxy was off must not haunt the new listener's chrome.
                var tally = browser.ProxyBlocks ?? new ProxyBlockTally(message => EnvironmentManager.Instance.Log(true, nameof(BrowserManager), message));
                browser.ProxyBlocks = tally;
                tally.Reset();

                // #72: the trust posture is browser-owned and republished by the new listener's
                // first mint, so a stale "untrusted" record from before the proxy was off never
                // outlives the browser; a stale banner is re-checked (and hidden on a flip) the
                // same way. Create one when the browser was built unproxied.
                var caTrust = browser.ProxyCaTrust ?? new ProxyCaTrustState();
                browser.ProxyCaTrust = caTrust;

                // #95: a proxy toggled back on mid-session consults the SAME claim as creation -
                // if another process owns this boundary's listener right now, join it instead of
                // binding a second one onto the shared profile.
                browser.ProxyUserDataFolder = ProxiedUserDataFolder(browser.Id);
                var claimPath = BrowserProfileIdentity.GetListenerClaimPath(browser.Id);
                var lease = _leases.Acquire(claimPath, browser.Id, ProxyBrowserArguments.PortIndependentFingerprint());
                foreach (var audit in lease.AuditLines)
                {
                    EnvironmentManager.Instance.Log(true, nameof(BrowserManager), audit);
                }

                if (lease.Role == ProxyListenerLeaseRole.Joined)
                {
                    browser.ProxyListener = null;
                    browser.ProxyClaimPath = null;
                    browser.ProxyBrowserArguments = ProxyBrowserArguments.For(lease.Port);
                    browser.ProxyViewTargets = proxyProfile.CanonicalTargets;
                    browser.ProxyViewFloorHosts = proxyProfile.VmsFloorHosts;
                    EnvironmentManager.Instance.Log(
                        false,
                        nameof(BrowserManager),
                        $"Proxied browser {browser.Id} rewired to join the boundary owner's loopback listener 127.0.0.1:{lease.Port} (proxy enabled since the browser was created), boundary profile '{browser.ProxyUserDataFolder}'.");
                    return true;
                }

                var listener = new LoopbackProxyListener(
                    proxyProfile.CanonicalTargets,
                    FetchThroughMessaging,
                    new ProxyViewSessionContext
                    {
                        SessionId = Guid.NewGuid().ToString("N"),
                        WorkspaceItemId = proxyProfile.WorkspaceItemId,
                        ClientMachine = Environment.MachineName,
                    },
                    tally,
                    proxyProfile.VmsFloorHosts,
                    caTrust);
                browser.ProxyClaimPath = StartListenerOnLeasedPort(listener, lease, claimPath, browser.Id);
                browser.ProxyListener = listener;
                browser.ProxyBrowserArguments = ProxyBrowserArguments.For(listener.Port);
                browser.ProxyViewTargets = proxyProfile.CanonicalTargets;
                browser.ProxyViewFloorHosts = proxyProfile.VmsFloorHosts;
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(BrowserManager),
                    $"Proxied browser {browser.Id} rewired to loopback listener 127.0.0.1:{listener.Port} (proxy enabled since the browser was created, {(browser.ProxyClaimPath != null ? "claim owner" : "ephemeral fallback - no claim")}), boundary profile '{browser.ProxyUserDataFolder}'.");
                return true;
            }

            if (browser.ProxyBrowserArguments == null)
            {
                return false;
            }

            var stopped = browser.ProxyListener;
            browser.ProxyListener = null;
            browser.ProxyBrowserArguments = null;
            browser.ProxyUserDataFolder = null;
            browser.ProxyViewTargets = null;
            browser.ProxyViewFloorHosts = null;
            stopped?.Stop();
            // #95: a stopped owner must not leave its claim naming a dead port for another process
            // to join (or stall on); a joined browser owns nothing and releases nothing.
            ReleaseOwnClaim(browser);
            browser.ProxyBlocks?.Reset();
            // #72: no loopback listener, no deployment CA behind this view - the banner can
            // never honestly claim "the proxy certificate is not trusted" for a direct browser.
            browser.ProxyCaTrust?.Clear();
            EnvironmentManager.Instance.Log(
                false,
                nameof(BrowserManager),
                $"Browser {browser.Id} rewired to direct routing (proxy disabled since the browser was created).");
            return true;
        }

        /// <summary>
        /// ADR-0004 (#92): the proxied profile folder is keyed to the FULL proxied boundary
        /// (Windows user + XProtect identity + VMS server), not just the user leg — two Smart
        /// Clients logged into different VMS servers never share a proxied profile or fetch
        /// channel, even when the identity value itself collides. The shape is
        /// <c>&lt;per-user root&gt;\server-&lt;server key&gt;\proxied-&lt;browser id&gt;</c>;
        /// the non-proxied profile key stays untouched.
        /// </summary>
        private static string ProxiedUserDataFolder(Guid id)
        {
            return BrowserProfileIdentity.GetProxiedUserDataFolder(id);
        }

        /// <summary>
        /// The listener's fetch seam: proxy-fetch over classic MIP messaging to the Event Server
        /// fetcher (#53; method/headers/body since #55). An unconnected channel is an explicit
        /// fetch failure, never a direct hit.
        /// </summary>
        private static Task<ProxyFetchResult> FetchThroughMessaging(ProxyFetchQuery query)
        {
            var client = ProxyFetchClient.Active;
            if (client == null || !client.IsReady)
            {
                // #58: the server-side proxy component is unreachable - a distinct code so the
                // view names the missing Event Server component instead of a generic gateway fault.
                return Task.FromResult(new ProxyFetchResult
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.ServerUnavailable,
                    ErrorMessage = "The Smart Client is not connected to the VMS server proxy component: the Web Content fetcher on the XProtect Event Server is absent, or this session's messaging channel to the Event Server is down (restart the Smart Client after an Event Server restart).",
                });
            }

            return client.FetchAsync(query);
        }

        /// <summary>
        /// Stops every live loopback proxy listener (plugin teardown), so no listener outlives the
        /// plugin even when browsers are dropped without an explicit reclaim.
        /// </summary>
        public static void StopAllProxyListeners()
        {
            foreach (var browser in _registry.Snapshot())
            {
                try
                {
                    browser?.ProxyListener?.Stop();
                    // #95: with the listener stopped the published port is dead; release the
                    // claim now instead of leaving joiners to probe a corpse.
                    ReleaseOwnClaim(browser);
                }
                catch (Exception ex)
                {
                    EnvironmentManager.Instance.ExceptionHandler(nameof(StopAllProxyListeners), ex);
                }
            }
        }

        /// <summary>
        /// Returns the tracked browser for <paramref name="id"/> without creating a new
        /// one. Used by re-attach paths that must not resurrect a reclaimed browser.
        /// </summary>
        public static bool TryGetBrowser(Guid id, out BrowserInstance browser)
        {
            return _registry.TryGet(id, out browser);
        }

        public static bool IsTracked(Guid id)
        {
            return _registry.IsTracked(id);
        }

        public static int AddReference(Guid id)
        {
            return _registry.AddReference(id);
        }

        public static int ReleaseReference(Guid id)
        {
            return _registry.ReleaseReference(id, browser => browser?.Dispose());
        }

        public static int GetReferenceCount(Guid id)
        {
            return _registry.GetReferenceCount(id);
        }

        /// <summary>
        /// Permanently removes a browser from the shared registry, disposing it. If the
        /// browser is still referenced, removal is deferred until the last reference is
        /// released. Used when the owning view item is removed so that the registry does
        /// not grow across a long session.
        /// </summary>
        public static void ReclaimBrowser(Guid id)
        {
            _registry.Reclaim(id, browser => browser?.Dispose());
        }

        /// <summary>
        /// Removes the tracking entry for a browser without disposing it. Callers are
        /// expected to have already disposed their own WebView via <see cref="BrowserInstance.Close"/>.
        /// </summary>
        public static void RemoveBrowserTracking(Guid id)
        {
            _registry.RemoveTracking(id);
        }
    }
}
