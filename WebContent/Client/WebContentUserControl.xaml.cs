using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.Messaging;

namespace WebContent.Client
{
    /// <summary>
    /// Interaction logic for WebContentUserControl.xaml
    /// </summary>
    public partial class WebContentUserControl : ViewItemWpfUserControl
    {
        private WebContentViewModel _viewModel;

        public WebContentUserControl()
        {
            InitializeComponent();
            _viewModel = DataContext as WebContentViewModel;

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                var binding = new Binding("BrowserVisibility")
                {
                    Source = _viewModel
                };
                _viewModel.Browser.WebView.SetBinding(VisibilityProperty, binding);
                Panel.Children.Add(_viewModel.Browser.WebView);                
            }
        }
        public WebContentUserControl(WebContentViewItemManager viewItemManager)
        {
            // FYI DataContext is null until InitializeComponent() is called
            InitializeComponent();
            _viewModel = DataContext as WebContentViewModel;
            _viewModel.ViewItemManager = viewItemManager;
            //PreviewMouseDown += WebContentUserControl_PreviewMouseDown;
        }

        public override void Init()
        {
            _viewModel.Init();
            if (Panel.Children.Count == 0)
            {
                var binding = new Binding("BrowserVisibility")
                {
                    Source = _viewModel
                };
                _viewModel.Browser.WebView.SetBinding(VisibilityProperty, binding);
                Panel.Children.Add(_viewModel.Browser.WebView);
            }
        }

        public override void Close()
        {
            if (_viewModel.ViewItemManager?.Settings.RememberLastAddress ?? false)
            {
                _viewModel.ViewItemManager.Settings.LastAddress = _viewModel.Address;
            }
            _viewModel.Dispose();
            Panel.Children.Clear();
        }

        private void WebContentUserControl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            
            // I don't know why I originally was only firing the click events in setup mode
            //if (ClientControl.Instance.WorkSpaceState != WorkSpaceState.Setup) return;

            switch (e.ClickCount)
            {
                case 1:
                    FireClickEvent();
                    break;
                case 2:
                    FireDoubleClickEvent();
                    break;
            }
        }

        

        private object ModeChanged(Message message, FQID destination, FQID sender)
        {
            if (message.Data is WorkSpaceState state) SetViewItemDisplay(state);
            return null;
        }

        private void SetViewItemDisplay(WorkSpaceState state)
        {
            switch (state)
            {
                case WorkSpaceState.Normal:
                    Toolbar.Visibility = _viewModel.ToolbarVisibility;
                    if (Panel.Children.Count > 0) Panel.Children[0].Visibility = Visibility.Visible;
                    break;
                case WorkSpaceState.Setup:
                    Toolbar.Visibility = Visibility.Visible;
                    if (Panel.Children.Count > 0) Panel.Children[0].Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void addressBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var address = addressBar.Text;
                if (Keyboard.Modifiers == ModifierKeys.Control && address.IndexOf(".") < 0)
                {
                    address += ".com";
                }
                _viewModel.Browser.Address = address;
                e.Handled = true;
            }
        }

        private void ViewItemWpfUserControl_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 1)
            {
                FireClickEvent();
            }
            else
            {
                FireDoubleClickEvent();
            }
        }
    }
}
