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

        public static void DestroyBrowser(BrowserInstance browser)
        {
            if (_browsers.TryRemove(browser.Id, out BrowserInstance b))
            {
                b.Dispose();
            }
        }
    }
}
