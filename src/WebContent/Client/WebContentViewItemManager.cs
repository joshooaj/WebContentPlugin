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

        public override void PropertiesLoaded()
        {
            base.PropertiesLoaded();
            SeedWorkspaceProperties();
        }

        /// <summary>
        /// Workspace-provisioned view items cannot receive admin settings through the
        /// InsertViewItemPlugin property dictionary (GetProperty does not read it), so the
        /// workspace plugin publishes the definition to the session registry keyed by the view
        /// item's layout FQID and instance ids, and the view item seeds its own persisted
        /// property store from it. Standalone (manually placed) view items have no registry
        /// entry and keep their own configuration. The seeding runs from PropertiesLoaded (the
        /// current platform hook) and Init (legacy hook) and is idempotent.
        /// </summary>
        private void SeedWorkspaceProperties()
        {
            try
            {
                if (!TryResolveWorkspaceDefinition(out var definition, out var matchedKey))
                {
                    return;
                }

                var seed = WorkspaceInstanceProperties.ComputeSeed(GetProperty, definition);
                if (seed.Count == 0)
                {
                    EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), $"WebContent workspace seed: view item {matchedKey} already current (HomeAddress={TryHome(definition)}).");
                    return;
                }

                foreach (var entry in seed)
                {
                    SetProperty(entry.Key, entry.Value);
                }

                SaveProperties();
                EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), $"WebContent workspace seed: applied {seed.Count} value(s) via key {matchedKey} (HomeAddress={TryHome(definition)}).");
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler($"{nameof(WebContentViewItemManager)}.SeedWorkspaceProperties", ex);
            }
        }

        private bool TryResolveWorkspaceDefinition(out System.Collections.Generic.IReadOnlyDictionary<string, string> definition, out string matchedKey)
        {
            definition = null;
            matchedKey = null;

            var fqidId = FQID?.ObjectId ?? Guid.Empty;
            if (fqidId != Guid.Empty && WorkspaceInstanceProperties.TryGet(fqidId, out definition))
            {
                matchedKey = $"FQID {fqidId}";
                return true;
            }

            var instanceId = Settings.InstanceId;
            if (instanceId != Guid.Empty && WorkspaceInstanceProperties.TryGet(instanceId, out definition))
            {
                matchedKey = $"InstanceId {instanceId}";
                return true;
            }

            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), $"WebContent workspace seed: no registry entry for FQID {fqidId} / InstanceId {instanceId}; treating the view item as standalone.");
            return false;
        }

        private static string TryHome(System.Collections.Generic.IReadOnlyDictionary<string, string> definition)
        {
            return definition != null && definition.TryGetValue(nameof(WebContentSettings.HomeAddress), out var home) ? home : "(none)";
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
            SeedWorkspaceProperties();
            return new WebContentPropertiesUserControl(this);
        }

        public override ViewItemWpfUserControl GenerateViewItemWpfUserControl()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentViewItemManager), "Generating view item UserControl.");
            // Smart Client 2025 R3 never calls the obsolete Init() and only calls
            // PropertiesLoaded() for layout-deserialized items, but it always calls this
            // method before the view reads any settings. Seeding is idempotent.
            SeedWorkspaceProperties();
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
