using Microsoft.Web.WebView2.Wpf;
using System;
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
            try
            {
                return _registry.GetOrCreate(id, guid => CreateBrowser(guid, initialAddress, isIsolated));
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(BrowserManager), ex);
            }
            return null;
        }

        private static BrowserInstance CreateBrowser(Guid id, Uri initialAddress, bool isIsolated)
        {
            var instance = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = BrowserProfileIdentity.GetUserDataFolder(),
                    ProfileName = isIsolated ? id.ToString() : string.Empty,
                },
                Source = initialAddress,
            };

            return new BrowserInstance(instance, id);
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
