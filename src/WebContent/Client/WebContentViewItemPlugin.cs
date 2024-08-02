using System;
using System.Collections.Concurrent;
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
                    _iconSource = new VideoOSIconBitmapSource { BitmapSource = Resources.WebContentIcon.ToBitmapSource() };
                }
                return _iconSource;
            }
        }

        public override void Init()
        {
            // setup
        }

        public override void Close()
        {
            foreach (var browser in BrowsersToDispose.Values)
            {
                browser.ClearBrowsingData();
            }
        }

        public override ViewItemManager GenerateViewItemManager()
        {
            return new WebContentViewItemManager();
        }
    }
}