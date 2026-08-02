using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using VideoOS.Platform;
using VideoOS.Platform.Admin;
using VideoOS.Platform.Messaging;
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
            return Configuration.Instance.GetItemConfigurations(WebContentPluginDefinition.PluginId, null, WebContentPluginDefinition.WorkspaceKind)
                ?? new List<Item>();
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
            _userControl.WorkspaceName = item.Name;
            _userControl.HomeAddress = GetProperty(item, "Address", string.Empty);
            _userControl.RememberLastAddress = GetProperty(item, "RememberLastAddress", false);
            _userControl.HideToolbar = GetProperty(item, "HideToolbar", false);
            _userControl.HideAddress = GetProperty(item, "HideAddress", false);
            _userControl.ZoomLevel = double.TryParse(GetProperty(item, "ZoomLevel", "100"), out var z) ? z : 100;
            _userControl.ClearBrowsingDataOnExit = GetProperty(item, "ClearBrowsingDataOnExit", false);
            _userControl.AllowBackgroundBrowser = GetProperty(item, "AllowBackgroundBrowser", true);
            _userControl.AllowAudioNotifications = GetProperty(item, "AllowAudioNotifications", true);
            _userControl.MuteAudio = GetProperty(item, "MuteAudio", false);
            _userControl.IsolateBrowserInstances = GetProperty(item, "IsolateBrowserInstances", false);
            _userControl.EnableDevTools = GetProperty(item, "EnableDevTools", false);
            _userControl.FillContent();
        }

        private string GetProperty(Item item, string key, string defaultValue)
        {
            return item.Properties.ContainsKey(key) ? item.Properties[key] : defaultValue;
        }

        private bool GetProperty(Item item, string key, bool defaultValue)
        {
            if (item.Properties.ContainsKey(key) && bool.TryParse(item.Properties[key], out var value))
                return value;
            return defaultValue;
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
            SetOrRemoveProperty(CurrentItem, "Address", _userControl?.HomeAddress ?? string.Empty);
            SetOrRemoveProperty(CurrentItem, "RememberLastAddress", _userControl.RememberLastAddress.ToString());
            SetOrRemoveProperty(CurrentItem, "HideToolbar", _userControl.HideToolbar.ToString());
            SetOrRemoveProperty(CurrentItem, "HideAddress", _userControl.HideAddress.ToString());
            SetOrRemoveProperty(CurrentItem, "ZoomLevel", _userControl.ZoomLevel.ToString("F0"));
            SetOrRemoveProperty(CurrentItem, "ClearBrowsingDataOnExit", _userControl.ClearBrowsingDataOnExit.ToString());
            SetOrRemoveProperty(CurrentItem, "AllowBackgroundBrowser", _userControl.AllowBackgroundBrowser.ToString());
            SetOrRemoveProperty(CurrentItem, "AllowAudioNotifications", _userControl.AllowAudioNotifications.ToString());
            SetOrRemoveProperty(CurrentItem, "MuteAudio", _userControl.MuteAudio.ToString());
            SetOrRemoveProperty(CurrentItem, "IsolateBrowserInstances", _userControl.IsolateBrowserInstances.ToString());
            SetOrRemoveProperty(CurrentItem, "EnableDevTools", _userControl.EnableDevTools.ToString());
            
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
