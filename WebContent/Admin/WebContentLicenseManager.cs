using VideoOS.Platform.Admin;

namespace WebContent.Admin
{
    internal class WebContentLicenseManager : ItemManager
    {
        public WebContentLicenseManager()
        {
            
        }

        public override void Init()
        {
            
        }

        public override void Close()
        {
            
        }

        public override System.Windows.Forms.UserControl GenerateDetailUserControl()
        {
            return null;
        }

        public override ItemNodeUserControl GenerateOverviewUserControl()
        {
            return null;
        }

        public override string GetItemName()
        {
            return Resources.PluginName;
        }

        public override void SetItemName(string name)
        {
            
        }

        public override bool IsContextMenuValid(string command)
        {
            return false;
        }
    }
}