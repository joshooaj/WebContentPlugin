using System.ComponentModel;
using System.Diagnostics;
using VideoOS.Platform.Client;

namespace WebContent.Client
{
    /// <summary>
    /// Interaction logic for WebContentPropertiesUserControl.xaml
    /// </summary>
    public partial class WebContentPropertiesUserControl : PropertiesWpfUserControl
    {
        private WebContentViewItemManager _viewItemManager;

        public WebContentPropertiesUserControl()
        {
            InitializeComponent();
        }

        public WebContentPropertiesUserControl(WebContentViewItemManager viewItemManager)
        {
            InitializeComponent();
            _viewItemManager = viewItemManager;
            DataContext = viewItemManager.Settings;
        }

        private void ClearBrowsingDataButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _viewItemManager.ClearBrowsingData();
        }

        private void PurchaseLicense_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            Process.Start(Strings.PurchaseLicenseUrl);
        }
    }
}
