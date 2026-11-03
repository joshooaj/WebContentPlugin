using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.Web.WebView2.Wpf;

using VideoOS.Platform;
using VideoOS.Platform.Client;
using VideoOS.Platform.Messaging;
using VideoOS.Platform.UI.Controls;

using WebContent;

namespace WebContent.Client
{
    /// <summary>
    /// Interaction logic for WebContentUserControl.xaml
    /// </summary>
    public partial class WebContentUserControl : ViewItemWpfUserControl
    {
        private static readonly object ActiveControlsSync = new object();
        private static readonly Dictionary<Guid, List<WeakReference<WebContentUserControl>>> ActiveControlsByBrowserId = new Dictionary<Guid, List<WeakReference<WebContentUserControl>>>();

        private readonly AddressBarInteraction _addressBarInteraction = new AddressBarInteraction();
        private WebContentViewModel _viewModel;
        private bool _isClosed;

        /// <summary>
        /// #83: the blocked-resource tally of the browser behind this control (per view
        /// instance). The tally fires its Changed event from the listener's socket threads;
        /// the indicator work is marshalled to this control's dispatcher.
        /// </summary>
        private ProxyBlockTally _blockTally;
        private bool _blockedPanelOpen;
        private int _blockRefreshQueued;

        public override bool ShowToolbar => false;
        public override bool Selectable => false;
        public WebContentUserControl()
        {
            InitializeComponent();
            WireUp();

            if (DesignerProperties.GetIsInDesignMode(this))
            {
                var binding = new Binding("BrowserVisibility")
                {
                    Source = _viewModel
                };
                _viewModel.Browser.WebView.SetBinding(VisibilityProperty, binding);
                AddWebViewToPanel(_viewModel.Browser.WebView);
            }
        }
        public WebContentUserControl(WebContentViewItemManager viewItemManager)
        {
            // FYI DataContext is null until InitializeComponent() is called
            InitializeComponent();
            WireUp();
            _viewModel.ViewItemManager = viewItemManager;
        }

        private void WireUp()
        {
            LocalizeToolbar();
            addressBar.TextChanged += OnAddressBarTextChanged;
            _viewModel = DataContext as WebContentViewModel;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        // x:Static resolves static members by reflection at BAML load time and only sees
        // public ones; Strings is internal, so the localized toolbar texts are applied here.
        private void LocalizeToolbar()
        {
            NavBack.ToolTip = Strings.AddressBarBackToolTip;
            NavForward.ToolTip = Strings.AddressBarForwardToolTip;
            NavReload.ToolTip = Strings.AddressBarReloadToolTip;
            NavHome.ToolTip = Strings.AddressBarHomeToolTip;
            NavPrint.ToolTip = Strings.AddressBarPrintToolTip;
            addressBar.PlaceholderText = Strings.AddressBarPlaceholder;
            blockedIndicator.ToolTip = Strings.BlockedResourcesToolTip;
        }

        private void OnAddressBarTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _addressBarInteraction.ClearWarning();
            ClearAddressFeedback();
        }

        public override void Init()
        {
            EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl), $"Initializing view item {_viewModel.Id}.");
            try
            {
                _isClosed = false;
                var browser = _viewModel.Browser;
                if (browser == null)
                {
                    EnvironmentManager.Instance.Log(true, nameof(WebContentUserControl), $"Failed to resolve browser for view item {_viewModel.Id}.");
                    return;
                }

                var needsRecreation = browser.NeedsRecreation;
                if (needsRecreation)
                {
                    var address = _viewModel.ResolveRestoreAddress() ?? _viewModel.Home;

                    EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl), $"Browser needs recreation for {_viewModel.Id}. Address: {address}");
                    browser.RecreateWebView(
                        WebContentConfiguration.CreateNavigationUri(address, _viewModel.Home),
                        _viewModel.ViewItemManager.Settings.IsolateBrowserInstances);
                }

                _viewModel.Init();
                RegisterActiveControl(browser.Id);
                BindBlockTally(browser);

                if (browser.WebView == null)
                {
                    EnvironmentManager.Instance.Log(true, nameof(WebContentUserControl), $"Browser {_viewModel.Id} does not have an available WebView after initialization.");
                    return;
                }

                var binding = new Binding("BrowserVisibility")
                {
                    Source = _viewModel
                };
                browser.WebView.SetBinding(VisibilityProperty, binding);

                if (!Panel.Children.Contains(browser.WebView) || needsRecreation)
                {
                    AddWebViewToPanel(browser.WebView);
                }
                EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl), $"View item {_viewModel.Id} initialized successfully.");
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(Init), ex);
            }
        }

        private void AddWebViewToPanel(WebView2 webView)
        {
            if (webView == null)
            {
                return;
            }

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
                UnbindBlockTally();
                if (_viewModel != null)
                {
                    _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                }
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

                // Record the browser's real address only; the ViewModel.Address fallback
                // (Strings.HomeAddress) must not poison the session store.
                if (_viewModel.ViewItemManager != null)
                {
                    _viewModel.ViewItemManager.Settings.RecordSessionAddress(browser?.Address);
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
            if (e.Key != Key.Enter)
            {
                return;
            }

            switch (_addressBarInteraction.HandleEnter(addressBar.Text, e.IsRepeat, out var uri))
            {
                case AddressEntryOutcome.Accepted:
                    ClearAddressFeedback();
                    _viewModel.Browser.Address = uri.AbsoluteUri;
                    break;
                case AddressEntryOutcome.Rejected:
                    EnvironmentManager.Instance.Log(true, nameof(WebContentUserControl), $"Rejected invalid address input for view item {_viewModel?.Id}: '{addressBar.Text}'.");
                    ShowInvalidAddressFeedback();
                    break;
                case AddressEntryOutcome.IgnoredRepeat:
                    break;
            }
            e.Handled = true;
        }

        private void ShowInvalidAddressFeedback()
        {
            addressBar.State = VideoOSTextBoxBase.States.Error;
            addressWarning.Content = _addressBarInteraction.WarningMessage;
            addressWarning.Visibility = Visibility.Visible;
        }

        private void ClearAddressFeedback()
        {
            if (addressBar.State != VideoOSTextBoxBase.States.Normal)
            {
                addressBar.State = VideoOSTextBoxBase.States.Normal;
            }

            if (addressWarning.Visibility != Visibility.Collapsed)
            {
                addressWarning.Content = null;
                addressWarning.Visibility = Visibility.Collapsed;
            }
        }

        #region Blocked resources (#83)

        /// <summary>
        /// Attach this control to the browser's blocked-resource tally. The tally is owned by
        /// the browser instance, so it survives listener rewires and WebView recreation; only a
        /// different browser (or none) needs a re-subscription.
        /// </summary>
        private void BindBlockTally(BrowserInstance browser)
        {
            var tally = browser?.ProxyBlocks;
            if (!ReferenceEquals(_blockTally, tally))
            {
                UnbindBlockTally();
                _blockTally = tally;
                if (_blockTally != null)
                {
                    _blockTally.Changed += BlockTally_Changed;
                }
            }

            UpdateBlockedIndicator();
        }

        private void UnbindBlockTally()
        {
            if (_blockTally != null)
            {
                _blockTally.Changed -= BlockTally_Changed;
                _blockTally = null;
            }
        }

        private void BlockTally_Changed()
        {
            // Fires on listener socket threads; coalesce bursts into one dispatcher pass.
            if (_isClosed)
            {
                return;
            }

            if (System.Threading.Interlocked.Exchange(ref _blockRefreshQueued, 1) == 1)
            {
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _blockRefreshQueued, 0);
                    if (!_isClosed)
                    {
                        UpdateBlockedIndicator();
                    }
                }), DispatcherPriority.Background);
            }
            catch (Exception)
            {
                // Dispatcher shutdown mid-teardown: the control is going away anyway.
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WebContentViewModel.IsSetupState))
            {
                UpdateBlockedIndicator();
            }
        }

        /// <summary>
        /// Setup-mode only: a refusal count above zero shows "N resources blocked" in the
        /// toolbar; zero blocks, or any runtime-mode state, hides it entirely (ordinary users
        /// see nothing beyond the top-level error pages).
        /// </summary>
        private void UpdateBlockedIndicator()
        {
            if (_isClosed)
            {
                return;
            }

            var total = _blockTally?.TotalBlocks ?? 0;
            if (_blockTally != null && _viewModel?.IsSetupState == true && total > 0)
            {
                blockedIndicatorText.Text = total == 1
                    ? Strings.BlockedResourcesIndicatorOne
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture, Strings.BlockedResourcesIndicatorMany, total);
                blockedIndicator.Visibility = Visibility.Visible;

                if (_blockedPanelOpen)
                {
                    RebuildBlockedResourceRows(_blockTally);
                }
            }
            else
            {
                blockedIndicator.Visibility = Visibility.Collapsed;
                blockedResourcesPanel.Visibility = Visibility.Collapsed;
                _blockedPanelOpen = false;
            }
        }

        private void blockedIndicator_Click(object sender, RoutedEventArgs e)
        {
            // No Focus() calls: the indicator must not pull keyboard focus out of the browser
            // beyond ordinary toolbar chrome (Smart Client focus etiquette) - in setup mode the
            // browser is hidden anyway, and selection/copy in the list works without it.
            _blockedPanelOpen = !_blockedPanelOpen;
            if (_blockedPanelOpen && _blockTally != null)
            {
                RebuildBlockedResourceRows(_blockTally);
                blockedResourcesPanel.Visibility = Visibility.Visible;
            }
            else
            {
                blockedResourcesPanel.Visibility = Visibility.Collapsed;
                _blockedPanelOpen = _blockedPanelOpen && _blockTally != null;
            }
        }

        private void RebuildBlockedResourceRows(ProxyBlockTally tally)
        {
            blockedResourcesRows.Children.Clear();
            blockedResourcesRows.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Strings.BlockedResourcesHeader,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 2, 4),
            });

            foreach (var entry in tally.Snapshot())
            {
                var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

                // Read-only TextBox, not a TextBlock: the canonical origin stays selectable and
                // copyable for an admin pasting it into the allowlist editor.
                var origin = new System.Windows.Controls.TextBox
                {
                    Text = entry.Origin,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsTabStop = true,
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(origin, "blockedResourceOrigin");
                System.Windows.Automation.AutomationProperties.SetHelpText(origin, entry.Origin);

                var reason = new System.Windows.Controls.TextBlock
                {
                    Text = entry.ReasonLabel,
                    Foreground = System.Windows.Media.Brushes.LightGray,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(reason, "blockedResourceReason");

                var count = new System.Windows.Controls.TextBlock
                {
                    Text = entry.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Foreground = System.Windows.Media.Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(count, "blockedResourceCount");

                System.Windows.Controls.Grid.SetColumn(origin, 0);
                System.Windows.Controls.Grid.SetColumn(reason, 1);
                System.Windows.Controls.Grid.SetColumn(count, 2);
                row.Children.Add(origin);
                row.Children.Add(reason);
                row.Children.Add(count);
                blockedResourcesRows.Children.Add(row);
            }
        }

        #endregion

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

            // Resolve the tracked browser only. This re-attach path must not resurrect a
            // browser that was reclaimed after the owning view item was removed.
            if (!BrowserManager.TryGetBrowser(_viewModel.Id, out var browser) || browser?.WebView == null)
            {
                return;
            }

            if (Panel.Children.Contains(browser.WebView))
            {
                return;
            }

            AddWebViewToPanel(browser.WebView);
        }
    }
}
