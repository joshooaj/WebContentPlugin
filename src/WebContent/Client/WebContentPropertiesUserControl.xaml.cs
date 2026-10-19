using System.ComponentModel;
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
    }
}
