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
        public override Guid Id => new Guid(Resources.PluginId);

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

        public override AdminPlacementHint AdminPlacementHint => AdminPlacementHint.Hidden;

        public override List<ViewItemPlugin> ViewItemPlugins { get; } = new List<ViewItemPlugin>();

        public override List<ItemNode> ItemNodes { get; } = new List<ItemNode>();

        public override Collection<LicenseInformation> PluginLicenseRequest => SiteLicenseHandler.GetLicenseRequest();

        public override bool IncludeInExport => true;

        public List<ItemNode> RootItemChildNodes { get; private set; } = new List<ItemNode>();

        public override ExportManager GenerateExportManager(ExportParameters exportParameters) => new WebContentExportManager(exportParameters);

        public override string SharedNodeName => Resources.PluginCompany;

        public override Guid SharedNodeId => new Guid(Resources.SharedNodeId);

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
