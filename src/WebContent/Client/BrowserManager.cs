using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Concurrent;
using System.IO;
using VideoOS.Platform;

namespace WebContent.Client
{
    public static class BrowserManager
    {
        private static readonly ConcurrentDictionary<Guid, BrowserInstance> _browsers = new ConcurrentDictionary<Guid, BrowserInstance>();
        private static readonly ConcurrentDictionary<Guid, int> _browserReferenceCounts = new ConcurrentDictionary<Guid, int>();
        private static string _userDataFolder;

        static BrowserManager()
        {
            _userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartClient.WebContentData");
            if (!Directory.Exists(_userDataFolder))
            {
                Directory.CreateDirectory(_userDataFolder);
            }
        }

        public static BrowserInstance GetBrowser(Guid id)
        {
            return GetBrowser(id, new Uri(string.Empty), false);
        }

        public static BrowserInstance GetBrowser(Guid id, Uri initialAddress, bool isIsolated)
        {
            try
            {
                if (_browsers.TryGetValue(id, out BrowserInstance b)) return b;

                var instance = new WebView2
                {
                    CreationProperties = new CoreWebView2CreationProperties
                    {
                        UserDataFolder = _userDataFolder,
                        ProfileName = isIsolated ? id.ToString() : string.Empty,
                    },
                    Source = initialAddress,
                };

                if (_browsers.TryAdd(id, new BrowserInstance(instance, id)))
                {
                    return _browsers[id];
                }
                else
                {
                    EnvironmentManager.Instance.Log(true, nameof(GetBrowser), $"Failed to add new {nameof(BrowserInstance)} for view item with id {id}.");
                    instance.Dispose();
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(BrowserManager), ex);
            }
            return null;
        }

        public static int AddReference(Guid id)
        {
            return _browserReferenceCounts.AddOrUpdate(id, 1, (key, count) => count + 1);
        }

        public static int ReleaseReference(Guid id)
        {
            while (true)
            {
                if (!_browserReferenceCounts.TryGetValue(id, out var count))
                {
                    return 0;
                }

                if (count <= 1)
                {
                    if (_browserReferenceCounts.TryRemove(id, out _))
                    {
                        return 0;
                    }

                    continue;
                }

                if (_browserReferenceCounts.TryUpdate(id, count - 1, count))
                {
                    return count - 1;
                }
            }
        }

        public static int GetReferenceCount(Guid id)
        {
            return _browserReferenceCounts.TryGetValue(id, out var count) ? count : 0;
        }

        public static void DestroyBrowser(BrowserInstance browser)
        {
            RemoveBrowserTracking(browser.Id);
            browser.Dispose();
        }

        /// <summary>
        /// Removes the tracking entry for a browser without disposing it. Callers are
        /// expected to have already disposed their own WebView via <see cref="BrowserInstance.Close"/>.
        /// This keeps <see cref="_browsers"/> from growing unboundedly across many
        /// view navigation and closure cycles in a long-lived session.
        /// </summary>
        public static void RemoveBrowserTracking(Guid id)
        {
            _browsers.TryRemove(id, out _);
            _browserReferenceCounts.TryRemove(id, out _);
        }
    }
}
