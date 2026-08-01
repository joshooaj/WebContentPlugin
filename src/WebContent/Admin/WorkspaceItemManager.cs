using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using VideoOS.Platform;
using VideoOS.Platform.Admin;

namespace WebContent.Admin
{
    public class WorkspaceItemManager : ItemManager
    {
        private readonly Guid _kind;
        private WorkspaceUserControl _userControl;

        public WorkspaceItemManager(Guid kind) : base()
        {
            _kind = kind;
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
            return _userControl;
        }

        public override ItemNodeUserControl GenerateOverviewUserControl()
        {
            return new VideoOS.Platform.UI.HelpUserControl(Resources.CompanyLogo, "Web Content Workspace", "Create dedicated workspaces for hosting web content in Smart Client.");
        }

        public override void ClearUserControl()
        {
            _userControl?.ClearUserControl();
        }

        public override void FillUserControl(Item item)
        {
            if (_userControl == null) return;
            
            CurrentItem = item;
            _userControl.WorkspaceName = item.Name;
            if (item.Properties.ContainsKey("Address"))
            {
                _userControl.Address = item.Properties["Address"];
            }
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
            
            CurrentItem.Name = _userControl?.WorkspaceName ?? CurrentItem.Name;
            CurrentItem.Properties["Address"] = _userControl?.Address ?? string.Empty;
            Configuration.Instance.SaveItemConfiguration(WebContentPluginDefinition.PluginId, CurrentItem);
            return true;
        }

        public override Item CreateItem(Item parentItem, FQID suggestedFQID)
        {
            var fqid = new FQID(Configuration.Instance.ServerFQID.ServerId)
            {
                ObjectId = Guid.NewGuid(),
                Kind = WebContentPluginDefinition.WorkspaceKind
            };

            CurrentItem = new Item(fqid, "Enter a name");
            Configuration.Instance.SaveItemConfiguration(WebContentPluginDefinition.PluginId, CurrentItem);

            return CurrentItem;
        }

        public override void DeleteItem(Item item)
        {
            if (item == null) return;
            Configuration.Instance.DeleteItemConfiguration(WebContentPluginDefinition.PluginId, item);
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
