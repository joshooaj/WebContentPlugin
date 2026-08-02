using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
        private static readonly object ActiveControlsSync = new object();
        private static readonly Dictionary<Guid, List<WeakReference<WebContentUserControl>>> ActiveControlsByBrowserId = new Dictionary<Guid, List<WeakReference<WebContentUserControl>>>();

        private WebContentViewModel _viewModel;
        private bool _isClosed;
        public override bool ShowToolbar => false;
        public override bool Selectable => false;
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
                AddWebViewToPanel();
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
            EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl), $"Initializing view item {_viewModel.Id}.");
            try
            {
                _isClosed = false;
                _viewModel.Init();
                RegisterActiveControl(_viewModel.Browser.Id);
                
                var needsRecreation = _viewModel.Browser.NeedsRecreation;
                if (needsRecreation)
                {
                    var address = _viewModel.ViewItemManager.Settings.RememberLastAddress && !string.IsNullOrWhiteSpace(_viewModel.ViewItemManager.Settings.LastAddress)
                        ? _viewModel.ViewItemManager.Settings.LastAddress
                        : _viewModel.Home;
                    EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl), $"Browser needs recreation for {_viewModel.Id}. Address: {address}");
                    _viewModel.Browser.RecreateWebView(new Uri(address), _viewModel.ViewItemManager.Settings.IsolateBrowserInstances);
                }
                
                var binding = new Binding("BrowserVisibility")
                {
                    Source = _viewModel
                };
                _viewModel.Browser.WebView.SetBinding(VisibilityProperty, binding);

                if (!Panel.Children.Contains(_viewModel.Browser.WebView) || needsRecreation)
                {
                    AddWebViewToPanel();
                }
                EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl), $"View item {_viewModel.Id} initialized successfully.");
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(Init), ex);
            }
        }

        private void AddWebViewToPanel()
        {
            var webView = _viewModel.Browser.WebView;
            EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl), $"Adding WebView to panel for browser {_viewModel.Browser.Id}.");
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(webView);
            if (parent != null)
            {
                if (parent is System.Windows.Controls.Panel parentPanel)
                {
                    parentPanel.Children.Remove(webView);
                }
                else if (parent is System.Windows.Controls.ContentControl contentControl)
                {
                    contentControl.Content = null;
                }
            }
            Panel.Children.Add(webView);
        }

        public override void Close()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl), $"Closing view item {_viewModel?.Id}.");
            try
            {
                _isClosed = true;
                var browser = _viewModel?.Browser;
                var browserId = browser?.Id ?? Guid.Empty;
                var allowBackground = _viewModel?.ViewItemManager?.Settings.AllowBackgroundBrowser ?? false;
                var webView = browser?.WebView;
                var shouldReattach = allowBackground && webView != null;
                var isWebViewOwnedByThisControl = webView != null && ReferenceEquals(VisualTreeHelper.GetParent(webView), Panel);

                if (_viewModel.ViewItemManager?.Settings.RememberLastAddress ?? false)
                {
                    _viewModel.ViewItemManager.Settings.LastAddress = _viewModel.Address;
                }

                UnregisterActiveControl(browserId);
                _viewModel.Dispose();
                Panel.Children.Clear();

                if (shouldReattach && isWebViewOwnedByThisControl)
                {
                    ReattachBrowserToActiveControl(browserId);
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(Close), ex);
            }
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

        private void RegisterActiveControl(Guid browserId)
        {
            if (browserId == Guid.Empty)
            {
                return;
            }

            lock (ActiveControlsSync)
            {
                if (!ActiveControlsByBrowserId.TryGetValue(browserId, out var controls))
                {
                    controls = new List<WeakReference<WebContentUserControl>>();
                    ActiveControlsByBrowserId[browserId] = controls;
                }

                CleanupActiveControls(controls, null);
                controls.Add(new WeakReference<WebContentUserControl>(this));
            }
        }

        private void UnregisterActiveControl(Guid browserId)
        {
            if (browserId == Guid.Empty)
            {
                return;
            }

            lock (ActiveControlsSync)
            {
                if (!ActiveControlsByBrowserId.TryGetValue(browserId, out var controls))
                {
                    return;
                }

                CleanupActiveControls(controls, this);

                if (controls.Count == 0)
                {
                    ActiveControlsByBrowserId.Remove(browserId);
                }
            }
        }

        private void ReattachBrowserToActiveControl(Guid browserId)
        {
            var targetControl = GetReattachTarget(browserId);
            if (targetControl == null)
            {
                return;
            }

            targetControl.Dispatcher.BeginInvoke(new Action(targetControl.AddWebViewToPanelIfNeeded), DispatcherPriority.Background);
        }

        private static void CleanupActiveControls(List<WeakReference<WebContentUserControl>> controls, WebContentUserControl controlToRemove)
        {
            for (var i = controls.Count - 1; i >= 0; i--)
            {
                if (!controls[i].TryGetTarget(out var control) || control == null || control._isClosed || ReferenceEquals(control, controlToRemove))
                {
                    controls.RemoveAt(i);
                }
            }
        }

        private WebContentUserControl GetReattachTarget(Guid browserId)
        {
            if (browserId == Guid.Empty)
            {
                return null;
            }

            lock (ActiveControlsSync)
            {
                if (!ActiveControlsByBrowserId.TryGetValue(browserId, out var controls))
                {
                    return null;
                }

                CleanupActiveControls(controls, this);

                for (var i = controls.Count - 1; i >= 0; i--)
                {
                    if (controls[i].TryGetTarget(out var control) && control != null && !control._isClosed)
                    {
                        return control;
                    }
                }

                if (controls.Count == 0)
                {
                    ActiveControlsByBrowserId.Remove(browserId);
                }

                return null;
            }
        }

        private void AddWebViewToPanelIfNeeded()
        {
            if (_isClosed || _viewModel == null)
            {
                return;
            }

            var browser = _viewModel.Browser;
            if (browser?.WebView == null)
            {
                return;
            }

            if (Panel.Children.Contains(browser.WebView))
            {
                return;
            }

            AddWebViewToPanel();
        }
    }
}
