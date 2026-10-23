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
            SeedWorkspaceProperties();
        }

        /// <summary>
        /// Workspace-provisioned view items cannot receive admin settings through the
        /// InsertViewItemPlugin property dictionary (GetProperty does not read it), so the
        /// workspace plugin publishes the definition to the session registry and the view item
        /// seeds its own persisted property store from it. Standalone (manually placed) view
        /// items have no registry entry and keep their own configuration.
        /// </summary>
        private void SeedWorkspaceProperties()
        {
            try
            {
                if (!WorkspaceInstanceProperties.TryGet(Settings.InstanceId, out var definition))
                {
                    return;
                }

                var seed = WorkspaceInstanceProperties.ComputeSeed(GetProperty, definition);
                if (seed.Count == 0)
                {
                    return;
                }

                foreach (var entry in seed)
                {
                    SetProperty(entry.Key, entry.Value);
                }

                SaveProperties();
                EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), $"Seeded {seed.Count} workspace-defined property value(s) for view item {Settings.InstanceId}.");
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler($"{nameof(WebContentViewItemManager)}.SeedWorkspaceProperties", ex);
            }
        }

        public override void Close()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), $"Closing ViewItemManager for plugin {Resources.PluginName}.");
            var instanceId = Settings.InstanceId;
            Settings.PropertyChanged -= WebContentViewItemManager_PropertyChangedEvent;
            Settings.Dispose();

            // The view item position is gone permanently, so its browser can never be
            // re-attached. Reclaim it so the shared registry does not grow across a long
            // session of repeated view add/remove cycles.
            BrowserManager.ReclaimBrowser(instanceId);
            WebContentViewItemPlugin.BrowsersToDispose.TryRemove(instanceId, out _);

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
