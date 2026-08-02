using System;
using VideoOS.Platform;
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

        public override void Init()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), $"Initializing ViewItemManager for plugin {Resources.PluginName}.");
            base.Init();
        }

        public override void Close()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), $"Closing ViewItemManager for plugin {Resources.PluginName}.");
            Settings.PropertyChanged -= WebContentViewItemManager_PropertyChangedEvent;
            Settings.Dispose();
            base.Close();
        }

        public void ClearBrowsingData()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), "ClearBrowsingData requested.");
            ClearBrowsingDataRequest?.Invoke(this, EventArgs.Empty);
        }

        public override PropertiesWpfUserControl GeneratePropertiesWpfUserControl()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), "Generating properties UserControl.");
            return new WebContentPropertiesUserControl(this);
        }

        public override ViewItemWpfUserControl GenerateViewItemWpfUserControl()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), "Generating view item UserControl.");
            return new WebContentUserControl(this);
        }

        private void WebContentViewItemManager_PropertyChangedEvent(object sender, System.EventArgs e)
        {
            try
            {
                SaveProperties();
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler($"{nameof(WebContentViewItemManager)}.SaveProperties", ex);
            }
        }
    }
}
