using System;
using System.Collections.Concurrent;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.UI.Controls;

namespace WebContent.Client
{

    internal class WebContentViewItemPlugin : ViewItemPlugin
    {
        public static ConcurrentDictionary<Guid, BrowserInstance> BrowsersToDispose = new ConcurrentDictionary<Guid, BrowserInstance>();

        public override Guid Id => new Guid(Resources.ViewItemPluginId);

        public override bool IsPrintEnabled => false;

        public override string Name => Resources.PluginName;

        private VideoOSIconSourceBase _iconSource;
        public override VideoOSIconSourceBase IconSource
        {
            get
            {
                if (_iconSource == null)
                {
                    _iconSource = new VideoOSIconBitmapSource { BitmapSource = ResourceLoader.WebContentIconSource };
                }
                return _iconSource;
            }
        }

        public override void Init()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemPlugin), $"Initializing plugin {Name} (Id: {Id}).");
        }

        public override void Close()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemPlugin), $"Closing plugin. {BrowsersToDispose.Count} browser(s) pending disposal.");
            foreach (var browser in BrowsersToDispose.Values)
            {
                try
                {
                    EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemPlugin), $"Clearing browsing data for browser {browser.Id} on plugin close.");
                    browser.ClearBrowsingData();
                }
                catch (Exception ex)
                {
                    EnvironmentManager.Instance.ExceptionHandler($"{nameof(WebContentViewItemPlugin)}.Close", ex);
                }
            }
        }

        public override ViewItemManager GenerateViewItemManager()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemPlugin), "Generating ViewItemManager.");
            return new WebContentViewItemManager();
        }
    }
}