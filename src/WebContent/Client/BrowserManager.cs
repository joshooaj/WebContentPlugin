using Microsoft.Web.WebView2.Wpf;
using System;
using System.Threading.Tasks;
using VideoOS.Platform;

namespace WebContent.Client
{
    public static class BrowserManager
    {
        private static readonly BrowserRegistry<BrowserInstance> _registry = new BrowserRegistry<BrowserInstance>();

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
            LoopbackProxyListener listener = null;
            string proxyArguments = null;
            string proxyUserDataFolder = null;
            if (proxyProfile != null)
            {
                listener = new LoopbackProxyListener(proxyProfile.CanonicalTargets, FetchThroughMessaging);
                listener.Start();
                proxyArguments = ProxyBrowserArguments.For(listener.Port);
                proxyUserDataFolder = ProxiedUserDataFolder(id);
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(BrowserManager),
                    $"Proxied browser {id}: loopback listener on 127.0.0.1:{listener.Port}, {proxyProfile.CanonicalTargets.Count} allowlisted target(s).");
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
                browser.ProxyBrowserArguments = proxyArguments;
                browser.ProxyUserDataFolder = proxyUserDataFolder;
                return browser;
            }
            catch (Exception)
            {
                listener?.Stop();
                throw;
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
                    browser.ProxyListener?.UpdateTargets(proxyProfile.CanonicalTargets);
                    return false;
                }

                var listener = new LoopbackProxyListener(proxyProfile.CanonicalTargets, FetchThroughMessaging);
                listener.Start();
                browser.ProxyListener = listener;
                browser.ProxyBrowserArguments = ProxyBrowserArguments.For(listener.Port);
                browser.ProxyUserDataFolder = ProxiedUserDataFolder(browser.Id);
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(BrowserManager),
                    $"Proxied browser {browser.Id} rewired to loopback listener 127.0.0.1:{listener.Port} (proxy enabled since the browser was created).");
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
            stopped?.Stop();
            EnvironmentManager.Instance.Log(
                false,
                nameof(BrowserManager),
                $"Browser {browser.Id} rewired to direct routing (proxy disabled since the browser was created).");
            return true;
        }

        private static string ProxiedUserDataFolder(Guid id)
        {
            return System.IO.Path.Combine(BrowserProfileIdentity.GetUserDataFolder(), "proxied-" + id);
        }

        /// <summary>
        /// The listener's fetch seam: proxy-fetch over classic MIP messaging to the Event Server
        /// fetcher (#53). An unconnected channel is an explicit fetch failure, never a direct hit.
        /// </summary>
        private static Task<ProxyFetchResult> FetchThroughMessaging(string url)
        {
            var client = ProxyFetchClient.Active;
            if (client == null || !client.IsReady)
            {
                return Task.FromResult(new ProxyFetchResult
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = "The Smart Client is not connected to the Event Server proxy fetcher.",
                });
            }

            return client.FetchAsync(url);
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
