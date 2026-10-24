using System;
using System.Collections.Generic;
using System.Windows.Forms;
using VideoOS.Platform;
using VideoOS.Platform.Admin;
using VideoOS.Platform.Messaging;
using WebContent;
using Message = VideoOS.Platform.Messaging.Message;

namespace WebContent.Admin
{
    public class WorkspaceItemManager : ItemManager
    {
        private readonly Guid _kind;
        private WorkspaceUserControl _userControl;
        private object _messageRegistrationObject;

        public WorkspaceItemManager(Guid kind) : base()
        {
            _kind = kind;
        }

        public override void Init()
        {
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), "Initializing WorkspaceItemManager.");
            EnvironmentManager.Instance.EnableConfigurationChangedService = true;
        }

        public override void Close()
        {
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), "Closing WorkspaceItemManager.");
            if (_messageRegistrationObject != null)
            {
                EnvironmentManager.Instance.UnRegisterReceiver(_messageRegistrationObject);
                _messageRegistrationObject = null;
            }
        }

        private void RefreshTreeView()
        {
            EnvironmentManager.Instance.SendMessage(
                new Message(MessageId.System.ApplicationRefreshTreeViewCommand) { Data = _kind });
        }

        public static List<Item> GetWorkspaces()
        {
            return TryGetWorkspaces(out var workspaces, out _) ? workspaces : new List<Item>();
        }

        public static bool TryGetWorkspaces(out List<Item> workspaces, out Exception error)
        {
            workspaces = new List<Item>();
            error = null;

            try
            {
                var configuration = Configuration.Instance;
                if (configuration == null)
                {
                    error = new InvalidOperationException("Configuration service is not available.");
                    return false;
                }

                var result = configuration.GetItemConfigurations(WebContentPluginDefinition.PluginId, null, WebContentPluginDefinition.WorkspaceKind);
                if (result == null)
                {
                    error = new InvalidOperationException("Workspace configuration is temporarily unavailable.");
                    return false;
                }

                workspaces = result;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        public static Item GetWorkspace(Guid workspaceId)
        {
            return Configuration.Instance.GetItemConfiguration(WebContentPluginDefinition.PluginId, WebContentPluginDefinition.WorkspaceKind, workspaceId);
        }

        public override UserControl GenerateDetailUserControl()
        {
            _userControl = new WorkspaceUserControl();
            _userControl.ConfigurationChangedByUser += ConfigurationChangedByUserHandler;
            return _userControl;
        }

        public override ItemNodeUserControl GenerateOverviewUserControl()
        {
            return new VideoOS.Platform.UI.HelpUserControl(ResourceLoader.CompanyLogo, "Web Content Workspace", "Create dedicated workspaces for hosting web content in Smart Client.");
        }

        public override void ClearUserControl()
        {
            _userControl?.ClearUserControl();
        }

        public override void ReleaseUserControl()
        {
            _userControl = null;
        }

        public override void FillUserControl(Item item)
        {
            if (_userControl == null) return;
            
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Filling UserControl for workspace item: {item.Name} (FQID: {item.FQID}).");
            CurrentItem = item;
            var properties = item?.Properties;

            _userControl.WorkspaceName = item.Name;

            var workspaceConfig = WorkspaceConfiguration.FromProperties(properties);
            if (workspaceConfig != null)
            {
                var settings = workspaceConfig.GetFirstWebContentSettings();
                if (settings != null)
                {
                    _userControl.HomeAddress = WebContentConfiguration.NormalizeHomeAddress(settings.HomeAddress);
                    _userControl.RememberLastAddress = settings.RememberLastAddress;
                    _userControl.HideToolbar = settings.HideToolbar;
                    _userControl.HideAddress = settings.HideToolbar ? false : settings.HideAddress;
                    _userControl.ZoomLevel = WebContentConfiguration.ClampZoomLevel(settings.ZoomLevel);
                    _userControl.ClearBrowsingDataOnExit = settings.ClearBrowsingDataOnExit;
                    _userControl.AllowBackgroundBrowser = settings.AllowBackgroundBrowser;
                    _userControl.AllowAudioNotifications = settings.AllowAudioNotifications;
                    _userControl.MuteAudio = settings.MuteAudio;
                    _userControl.IsolateBrowserInstances = settings.IsolateBrowserInstances;
                    _userControl.EnableDevTools = settings.EnableDevTools;
                    _userControl.FillContent();
                    return;
                }
            }

            _userControl.HomeAddress = WebContentConfiguration.NormalizeHomeAddress(
                WebContentConfiguration.GetString(properties, Strings.HomeAddress, "Address", "HomeAddress"));
            _userControl.RememberLastAddress = WebContentConfiguration.GetBoolean(properties, false, "RememberLastAddress");
            _userControl.HideToolbar = WebContentConfiguration.GetBoolean(properties, false, "HideToolbar");
            _userControl.HideAddress = WebContentConfiguration.GetBoolean(properties, false, "HideAddress") && !_userControl.HideToolbar;
            _userControl.ZoomLevel = WebContentConfiguration.GetZoomLevel(properties, WebContentConfiguration.DefaultZoomLevel, "ZoomLevel");
            _userControl.ClearBrowsingDataOnExit = WebContentConfiguration.GetBoolean(properties, false, "ClearBrowsingDataOnExit");
            _userControl.AllowBackgroundBrowser = WebContentConfiguration.GetBoolean(properties, true, "AllowBackgroundBrowser");
            _userControl.AllowAudioNotifications = WebContentConfiguration.GetBoolean(properties, true, "AllowAudioNotifications");
            _userControl.MuteAudio = WebContentConfiguration.GetBoolean(properties, false, "MuteAudio");
            _userControl.IsolateBrowserInstances = WebContentConfiguration.GetBoolean(properties, false, "IsolateBrowserInstances");
            _userControl.EnableDevTools = WebContentConfiguration.GetBoolean(properties, false, "EnableDevTools");
            _userControl.FillContent();
        }

        public override string GetItemName()
        {
            return _userControl?.WorkspaceName ?? string.Empty;
        }

        public override void SetItemName(string name)
        {
            if (_userControl != null)
            {
                _userControl.WorkspaceName = name;
            }
        }

        public override bool ValidateAndSaveUserControl()
        {
            if (CurrentItem == null) return false;
            
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Saving workspace configuration for item: {CurrentItem.Name} (FQID: {CurrentItem.FQID}).");
            CurrentItem.Name = _userControl?.WorkspaceName ?? CurrentItem.Name;

            var rawHomeAddress = _userControl?.HomeAddress;
            if (string.IsNullOrWhiteSpace(rawHomeAddress))
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): home address is empty.");
                MessageBox.Show(Strings.MissingHomeAddressMessage, Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!WebContentConfiguration.TryCreateNavigationUri(rawHomeAddress, out var homeAddressUri))
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspaceItemManager), $"Rejected save for workspace '{CurrentItem.Name}' (FQID: {CurrentItem.FQID}): invalid home address '{rawHomeAddress}'.");
                MessageBox.Show(string.Format(Strings.InvalidAddressMessage, rawHomeAddress), Strings.InvalidAddressTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            var homeAddress = homeAddressUri.AbsoluteUri;
            var hideToolbar = _userControl?.HideToolbar ?? false;
            var hideAddress = !hideToolbar && (_userControl?.HideAddress ?? false);

            SetOrRemoveProperty(CurrentItem, "Address", homeAddress);
            SetOrRemoveProperty(CurrentItem, "HomeAddress", homeAddress);
            SetOrRemoveProperty(CurrentItem, "RememberLastAddress", WebContentConfiguration.FormatBoolean(_userControl?.RememberLastAddress ?? false));
            SetOrRemoveProperty(CurrentItem, "HideToolbar", WebContentConfiguration.FormatBoolean(hideToolbar));
            SetOrRemoveProperty(CurrentItem, "HideAddress", WebContentConfiguration.FormatBoolean(hideAddress));
            SetOrRemoveProperty(CurrentItem, "ZoomLevel", WebContentConfiguration.FormatZoomLevel(_userControl?.ZoomLevel ?? WebContentConfiguration.DefaultZoomLevel));
            SetOrRemoveProperty(CurrentItem, "ClearBrowsingDataOnExit", WebContentConfiguration.FormatBoolean(_userControl?.ClearBrowsingDataOnExit ?? false));
            SetOrRemoveProperty(CurrentItem, "AllowBackgroundBrowser", WebContentConfiguration.FormatBoolean(_userControl?.AllowBackgroundBrowser ?? true));
            SetOrRemoveProperty(CurrentItem, "AllowAudioNotifications", WebContentConfiguration.FormatBoolean(_userControl?.AllowAudioNotifications ?? true));
            SetOrRemoveProperty(CurrentItem, "MuteAudio", WebContentConfiguration.FormatBoolean(_userControl?.MuteAudio ?? false));
            SetOrRemoveProperty(CurrentItem, "IsolateBrowserInstances", WebContentConfiguration.FormatBoolean(_userControl?.IsolateBrowserInstances ?? false));
            SetOrRemoveProperty(CurrentItem, "EnableDevTools", WebContentConfiguration.FormatBoolean(_userControl?.EnableDevTools ?? false));

            var config = WorkspaceConfiguration.CreateDefault(homeAddress);
            if (config.Items != null && config.Items.Count > 0)
            {
                var settings = config.Items[0].Settings;
                settings.HideToolbar = hideToolbar;
                settings.HideAddress = hideAddress;
                settings.ZoomLevel = WebContentConfiguration.ClampZoomLevel(_userControl?.ZoomLevel ?? WebContentConfiguration.DefaultZoomLevel);
                settings.RememberLastAddress = _userControl?.RememberLastAddress ?? false;
                settings.ClearBrowsingDataOnExit = _userControl?.ClearBrowsingDataOnExit ?? false;
                settings.AllowBackgroundBrowser = _userControl?.AllowBackgroundBrowser ?? true;
                settings.AllowAudioNotifications = _userControl?.AllowAudioNotifications ?? true;
                settings.MuteAudio = _userControl?.MuteAudio ?? false;
                settings.IsolateBrowserInstances = _userControl?.IsolateBrowserInstances ?? false;
                settings.EnableDevTools = _userControl?.EnableDevTools ?? false;
            }

            SetOrRemoveProperty(CurrentItem, WorkspaceConfiguration.WorkspaceConfigKey, config.Serialize());

            Configuration.Instance.SaveItemConfiguration(WebContentPluginDefinition.PluginId, CurrentItem);
            RefreshTreeView();
            return true;
        }

        private void SetOrRemoveProperty(Item item, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (item.Properties.ContainsKey(key))
                    item.Properties.Remove(key);
            }
            else
            {
                item.Properties[key] = value;
            }
        }

        public override Item CreateItem(Item parentItem, FQID suggestedFQID)
        {
            var fqid = new FQID(Configuration.Instance.ServerFQID.ServerId)
            {
                ObjectId = Guid.NewGuid(),
                Kind = WebContentPluginDefinition.WorkspaceKind
            };

            CurrentItem = new Item(fqid, "Enter a name");
            var defaultConfig = WorkspaceConfiguration.CreateDefault(string.Empty);
            SetOrRemoveProperty(CurrentItem, WorkspaceConfiguration.WorkspaceConfigKey, defaultConfig.Serialize());
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Creating new workspace item. FQID: {fqid}.");
            Configuration.Instance.SaveItemConfiguration(WebContentPluginDefinition.PluginId, CurrentItem);
            RefreshTreeView();

            return CurrentItem;
        }

        public override void DeleteItem(Item item)
        {
            if (item == null) return;
            EnvironmentManager.Instance.Log(false, nameof(WorkspaceItemManager), $"Deleting workspace item: {item.Name} (FQID: {item.FQID}).");
            Configuration.Instance.DeleteItemConfiguration(WebContentPluginDefinition.PluginId, item);
            RefreshTreeView();
        }

        public override List<Item> GetItems()
        {
            return Configuration.Instance.GetItemConfigurations(WebContentPluginDefinition.PluginId, null, WebContentPluginDefinition.WorkspaceKind);
        }

        public override List<Item> GetItems(Item parentItem)
        {
            return Configuration.Instance.GetItemConfigurations(WebContentPluginDefinition.PluginId, parentItem, WebContentPluginDefinition.WorkspaceKind);
        }

        public override Item GetItem(FQID fqid)
        {
            return Configuration.Instance.GetItemConfiguration(WebContentPluginDefinition.PluginId, _kind, fqid.ObjectId);
        }
    }
}
