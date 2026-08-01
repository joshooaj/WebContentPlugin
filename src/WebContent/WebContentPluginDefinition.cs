using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using VideoOS.Platform;
using VideoOS.Platform.Admin;
using VideoOS.Platform.Client;
using VideoOS.Platform.License;
using WebContent.Admin;
using WebContent.Client;

namespace WebContent
{
    public class WebContentPluginDefinition : PluginDefinition
    {
        internal static List<Task> Tasks = new List<Task>();

        internal static Guid PluginId = new Guid(Resources.PluginId);
        
        internal static Guid WorkspaceKind = new Guid("ba41d989-23e5-4095-ab69-0ebade554aed");

        public override Guid Id => PluginId;

        public override string Name => Resources.PluginName;

        public override Image Icon => Resources.CompanyLogo;

        public override string Manufacturer => Resources.PluginCompany;

        private string _versionString;
        public override string VersionString
        {
            get
            {
                if (_versionString == null)
                {
                    _versionString = FileVersionInfo.GetVersionInfo(GetType().Assembly.Location).ProductVersion;
                }

                return _versionString;
            }
        }

        public override AdminPlacementHint AdminPlacementHint => AdminPlacementHint.Default;

        public override List<ViewItemPlugin> ViewItemPlugins { get; } = new List<ViewItemPlugin>();

        public override Collection<LicenseInformation> PluginLicenseRequest => SiteLicenseHandler.GetLicenseRequest();

        public override bool IncludeInExport => true;

        public List<ItemNode> RootItemChildNodes { get; private set; } = new List<ItemNode>();

        public override ExportManager GenerateExportManager(ExportParameters exportParameters) => new WebContentExportManager(exportParameters);

        public override string SharedNodeName => Resources.PluginCompany;

        public override Guid SharedNodeId => new Guid(Resources.SharedNodeId);

        private List<ItemNode> _itemNodes;

        public override List<ItemNode> ItemNodes
        {
            get
            {
                if (_itemNodes != null) return _itemNodes;
                var securityActions = new List<SecurityAction>
                {
                    new SecurityAction("GENERIC_READ", "View Web Content Workspace"),
                    new SecurityAction("GENERIC_WRITE", "Manage Web Content Workspace"),
                };
                _itemNodes = new List<ItemNode>
                {
                    new ItemNode(
                        WorkspaceKind,
                        Guid.Empty,
                        "Web Content Workspace", Icon,
                        "Web Content Workspaces", Icon,
                        Category.Text, true,
                        ItemsAllowed.Many,
                        new WorkspaceItemManager(WorkspaceKind),
                        null,
                        securityActions)
                };
                return _itemNodes;
            }
        }

        public override void Init()
        {
            switch (EnvironmentManager.Instance.EnvironmentType)    
            {
                case EnvironmentType.Administration:
                    break;
                case EnvironmentType.SmartClient:
                    ViewItemPlugins.Add(new WebContentViewItemPlugin());
                    break;
                case EnvironmentType.Standalone:
                    break;
                case EnvironmentType.Service:
                    break;
                case EnvironmentType.ManagementServer:
                    break;
                default:
                    break;
            }
        }

        public override async void Close()
        {
            ViewItemPlugins.Clear();
            foreach (var task in Tasks)
            {
                try
                {
                    await task;
                }
                catch (Exception ex)
                {
                    EnvironmentManager.Instance.ExceptionHandler(nameof(Close), ex);
                }
            }
        }
    }
}
