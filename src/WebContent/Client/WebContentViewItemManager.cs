using System;
using VideoOS.Platform.Client;

namespace WebContent.Client
{
    public class WebContentViewItemManager : ViewItemManager
    {
        public WebContentSettings Settings { get; }

        public event EventHandler ClearBrowsingDataRequest;
        public WebContentViewItemManager() : base(Resources.PluginName)
        {
            Settings = new WebContentSettings(this);
            Settings.PropertyChanged += WebContentViewItemManager_PropertyChangedEvent;
        }

        public void ClearBrowsingData()
        {
            ClearBrowsingDataRequest?.Invoke(this, EventArgs.Empty);
        }

        public override PropertiesWpfUserControl GeneratePropertiesWpfUserControl()
        {
            return new WebContentPropertiesUserControl(this);
        }

        public override ViewItemWpfUserControl GenerateViewItemWpfUserControl()
        {
            return new WebContentUserControl(this);
        }

        private void WebContentViewItemManager_PropertyChangedEvent(object sender, System.EventArgs e)
        {
            SaveProperties();
        }
    }
}