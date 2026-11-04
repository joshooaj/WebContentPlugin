using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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

        /// <summary>
        /// #72: the deployment-CA trust posture of the browser behind this control (proxied views
        /// only). Fires its Changed event from the listener's mint threads; banner work is
        /// marshalled to this control's dispatcher exactly like the #83 tally.
        /// </summary>
        private ProxyCaTrustState _caTrust;
        private int _caRefreshQueued;
        private int _caFixRunning;

        /// <summary>
        /// #72: banner dismissals for the lifetime of this plugin-host process - dismissal is per
        /// session, so a fresh Smart Client session shows the notice again while the condition
        /// persists (acceptance). Keyed by CA thumbprint so a rotated deployment CA is a new,
        /// undismissed notice.
        /// </summary>
        private static readonly HashSet<string> CaBannerDismissedThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CaBannerDismissedSync = new object();

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
            proxyCaBannerText.Text = Strings.ProxyCaBannerText;
            proxyCaFixButtonText.Text = Strings.ProxyCaFixButtonLabel;
            proxyCaFixButton.ToolTip = Strings.ProxyCaFixButtonToolTip;
            proxyCaDismissButton.ToolTip = Strings.ProxyCaDismissButtonToolTip;
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
                BindCaTrust(browser);

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

                // #87 last, so the ownership overlay's WebView collapse lands after the fresh
                // BrowserVisibility binding from this Init (a browser that already failed init
                // shows the notice immediately on view (re-)selection, before any event fires).
                BindInitFailure(browser);

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
                UnbindCaTrust();
                UnbindInitFailure();
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
                UpdateCaBanner();
                UpdateProfileErrorOverlay();
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
                // The VideoOS button is icon-only, so the label is its toolbar sibling; both
                // toggle in lockstep (the first live SC-7 run proved a label inside the button
                // template renders nothing).
                blockedIndicatorText.Visibility = Visibility.Visible;
                blockedIndicator.Visibility = Visibility.Visible;

                if (_blockedPanelOpen)
                {
                    RebuildBlockedResourceRows(_blockTally);
                }
            }
            else
            {
                blockedIndicator.Visibility = Visibility.Collapsed;
                blockedIndicatorText.Visibility = Visibility.Collapsed;
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

        #region Untrusted deployment-CA banner (#72)

        /// <summary>Bundled elevated import helper; lives next to the plugin assembly in the drop.</summary>
        private const string TrustHelperExecutableName = "WebContentTrustHelper.exe";

        /// <summary>ShellExecute elevation the user declined at the Windows consent prompt.</summary>
        private const int ErrorCancelled = 1223;

        /// <summary>
        /// Attach this control to the browser's deployment-CA trust posture. Like the #83 tally
        /// it is browser-owned, so it survives listener rewires and WebView recreation; only a
        /// different browser (or none) needs a re-subscription.
        /// </summary>
        private void BindCaTrust(BrowserInstance browser)
        {
            var trust = browser?.ProxyCaTrust;
            if (!ReferenceEquals(_caTrust, trust))
            {
                UnbindCaTrust();
                _caTrust = trust;
                if (_caTrust != null)
                {
                    _caTrust.Changed += CaTrust_Changed;
                }
            }

            UpdateCaBanner();
        }

        private void UnbindCaTrust()
        {
            if (_caTrust != null)
            {
                _caTrust.Changed -= CaTrust_Changed;
                _caTrust = null;
            }
        }

        private void CaTrust_Changed()
        {
            // Fires on the listener's mint threads (and on the UI thread from the fix/dismissal
            // paths); coalesce bursts into one dispatcher pass exactly like the #83 tally.
            if (_isClosed)
            {
                return;
            }

            if (System.Threading.Interlocked.Exchange(ref _caRefreshQueued, 1) == 1)
            {
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _caRefreshQueued, 0);
                    if (!_isClosed)
                    {
                        UpdateCaBanner();
                    }
                }), DispatcherPriority.Background);
            }
            catch (Exception)
            {
                // Dispatcher shutdown mid-teardown: the control is going away anyway.
            }
        }

        private static bool IsCaBannerDismissed(string thumbprint)
        {
            lock (CaBannerDismissedSync)
            {
                return CaBannerDismissedThumbprints.Contains(thumbprint);
            }
        }

        /// <summary>
        /// The banner shows for EVERY user (setup or runtime) of a proxied view whose minted
        /// leaves chain to a deployment CA this machine does not trust - the browser itself
        /// shows its own certificate error behind this strip, and this names the fix instead of
        /// leaving a bare Chromium page. Ordinary users get information + per-session dismissal
        /// only; the elevated "Fix certificate trust" button is setup-mode-only. Trusted,
        /// unknown (nothing minted yet), or dismissed this session: collapsed.
        /// </summary>
        private void UpdateCaBanner()
        {
            if (_isClosed)
            {
                return;
            }

            var trust = _caTrust;
            var thumbprint = trust?.AuthorityThumbprint;
            if (trust != null && trust.IsUntrustedConfirmed && !string.IsNullOrEmpty(thumbprint) && IsCaBannerDismissed(thumbprint))
            {
                proxyCaBanner.Visibility = Visibility.Collapsed;
                return;
            }

            if (trust != null && trust.IsUntrustedConfirmed)
            {
                proxyCaBanner.Visibility = Visibility.Visible;
                proxyCaFixButton.Visibility = _viewModel?.IsSetupState == true ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                proxyCaBanner.Visibility = Visibility.Collapsed;
                proxyCaFixButton.Visibility = Visibility.Collapsed;
            }
        }

        private void proxyCaDismissButton_Click(object sender, RoutedEventArgs e)
        {
            var thumbprint = _caTrust?.AuthorityThumbprint;
            if (string.IsNullOrEmpty(thumbprint))
            {
                return;
            }

            lock (CaBannerDismissedSync)
            {
                CaBannerDismissedThumbprints.Add(thumbprint);
            }

            EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl),
                $"WebContent proxy: untrusted-CA banner dismissed for CA {thumbprint} for the rest of this Smart Client session (the notice reappears next session while the condition persists).");
            UpdateCaBanner();
        }

        /// <summary>
        /// Setup-mode-only fix. Deliberate UX cost (from the ticket's grilling): the flow raises
        /// TWO Windows dialogs - the UAC elevation consent and crypt32's modal trusted-root
        /// warning - and neither is suppressed. The plugin hands the helper the EXACT DER the
        /// mint reported plus its thumbprint; the helper re-verifies the thumbprint before it
        /// touches a store, and the plugin independently re-reads the root stores afterwards, so
        /// "success" is proven, not claimed. There is no automatic or silent trust path anywhere.
        /// </summary>
        private void proxyCaFixButton_Click(object sender, RoutedEventArgs e)
        {
            var trust = _caTrust;
            if (trust == null || !trust.IsUntrustedConfirmed || _viewModel?.IsSetupState != true)
            {
                return;
            }

            byte[] authorityDer;
            try
            {
                authorityDer = Convert.FromBase64String(trust.AuthorityDerBase64 ?? string.Empty);
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(WebContentUserControl),
                    $"WebContent proxy: certificate-trust fix refused - the deployment CA DER from the leaf mint is not decodable: {exception.Message}");
                return;
            }

            if (authorityDer.Length == 0 || string.IsNullOrEmpty(trust.AuthorityThumbprint))
            {
                EnvironmentManager.Instance.Log(true, nameof(WebContentUserControl),
                    "WebContent proxy: certificate-trust fix refused - no deployment CA material to import.");
                return;
            }

            if (System.Threading.Interlocked.Exchange(ref _caFixRunning, 1) == 1)
            {
                return;
            }

            var thumbprint = trust.AuthorityThumbprint;
            EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl),
                $"WebContent proxy: certificate-trust fix requested for CA {thumbprint}; starting the elevated helper (expect the Windows elevation prompt, then crypt32's root-store warning).");

            // The helper may wait on the UAC consent and crypt32's warning for as long as a human
            // takes; the wait lives off the UI thread and the banner stays put meanwhile.
            Task.Run(() => RunTrustFixHelper(thumbprint, authorityDer));
        }

        private void RunTrustFixHelper(string thumbprint, byte[] authorityDer)
        {
            var exitCode = int.MinValue;
            string note = null;
            string tempFile = null;
            try
            {
                var helperPath = Path.Combine(
                    Path.GetDirectoryName(typeof(WebContentUserControl).Assembly.Location) ?? string.Empty,
                    TrustHelperExecutableName);
                if (!File.Exists(helperPath))
                {
                    note = $"the bundled {TrustHelperExecutableName} is missing from the plugin folder - reinstall the plugin";
                }
                else
                {
                    tempFile = Path.Combine(Path.GetTempPath(), "webcontent-proxy-ca-" + Guid.NewGuid().ToString("N") + ".der");
                    File.WriteAllBytes(tempFile, authorityDer);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = helperPath,
                        Arguments = "\"" + tempFile + "\" " + thumbprint,
                        UseShellExecute = true,
                        // The deliberate elevation gate: the plugin process must never write a
                        // trust store itself, so the import only ever runs through UAC.
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    using (var process = Process.Start(startInfo))
                    {
                        process.WaitForExit();
                        exitCode = process.ExitCode;
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
            {
                note = "the elevation was declined at the Windows prompt (ERROR_CANCELLED)";
            }
            catch (Exception exception)
            {
                note = exception.Message;
            }
            finally
            {
                try { if (tempFile != null) File.Delete(tempFile); } catch { }
            }

            // Prove the flip by re-reading the root stores ourselves; the helper's exit code is
            // only a hint. Then the leaf cache drops and the next mint re-publishes the posture.
            var nowTrusted = exitCode == 0
                && ProxyLoopbackTrust.IsRootTrusted(thumbprint, message => EnvironmentManager.Instance.Log(true, nameof(WebContentUserControl), message));

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _caFixRunning, 0);
                    if (_isClosed)
                    {
                        return;
                    }

                    if (nowTrusted)
                    {
                        EnvironmentManager.Instance.Log(false, nameof(WebContentUserControl),
                            $"WebContent proxy: certificate-trust fix confirmed - CA {thumbprint} is now in this machine's trusted roots; invalidating cached leaves for browser {_viewModel?.Browser?.Id} and reloading proxied https content (no Smart Client restart needed).");
                        _caTrust?.Publish(thumbprint, Convert.ToBase64String(authorityDer), true);
                        var browser = _viewModel?.Browser;
                        browser?.ProxyListener?.InvalidateLeafCache();

                        // A proxied https document that is sitting on Chromium's certificate error
                        // needs one reload to re-dial the tunnel against the newly anchored chain.
                        if (Uri.TryCreate(browser?.Address, UriKind.Absolute, out var address)
                            && string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        {
                            _viewModel?.ReloadCommand?.Execute(null);
                        }
                    }
                    else
                    {
                        EnvironmentManager.Instance.Log(true, nameof(WebContentUserControl),
                            "WebContent proxy: certificate-trust fix did not complete"
                            + (note != null ? $" - {note}." : $" - the helper exited with code {exitCode}.")
                            + " The banner stays; nothing was trusted silently and the browser posture is unchanged.");
                    }

                    UpdateCaBanner();
                }), DispatcherPriority.Background);
            }
            catch (Exception)
            {
                // Dispatcher shutdown: the fix outcome dies with the control; the store state is
                // whatever the elevated helper actually did, which is auditable in its own log.
            }
        }

        #endregion

        #region Proxied profile-ownership error (#87)

        /// <summary>
        /// #87: the browser instance whose <see cref="BrowserInstance.InitFailure"/> /
        /// <see cref="BrowserInstance.WebView"/> changes this control follows. Like the #83
        /// tally and the #72 trust posture it is browser-owned, so the subscription survives a
        /// WebView recreation; only a different browser (or none) needs a re-subscription.
        /// </summary>
        private BrowserInstance _failureBrowser;

        /// <summary>
        /// Attach this control to the browser's failed-initialization contract and paint the
        /// current state (a view re-selected onto an already-errored browser shows the notice
        /// immediately; a failure that lands while the view is open arrives via PropertyChanged).
        /// </summary>
        private void BindInitFailure(BrowserInstance browser)
        {
            if (!ReferenceEquals(_failureBrowser, browser))
            {
                UnbindInitFailure();
                _failureBrowser = browser;
                if (browser != null)
                {
                    browser.PropertyChanged += Browser_ProxiedStateChanged;
                }
            }

            UpdateProfileErrorOverlay();
        }

        private void UnbindInitFailure()
        {
            if (_failureBrowser != null)
            {
                _failureBrowser.PropertyChanged -= Browser_ProxiedStateChanged;
                _failureBrowser = null;
            }
        }

        private void Browser_ProxiedStateChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isClosed)
            {
                return;
            }

            if (e.PropertyName != nameof(BrowserInstance.InitFailure) && e.PropertyName != nameof(BrowserInstance.WebView))
            {
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isClosed)
                    {
                        return;
                    }

                    if (e.PropertyName == nameof(BrowserInstance.WebView))
                    {
                        AttachRecreatedWebView();
                    }

                    UpdateProfileErrorOverlay();
                }), DispatcherPriority.Background);
            }
            catch (Exception)
            {
                // Dispatcher shutdown mid-teardown: the control is going away anyway.
            }
        }

        /// <summary>
        /// #87: the Refresh path recreates the WebView while this view is open - pick the fresh
        /// control up exactly like Init does (visibility binding + panel), no re-selection
        /// needed. The re-attach for background/browser-moved cases stays untouched.
        /// </summary>
        private void AttachRecreatedWebView()
        {
            var webView = _failureBrowser?.WebView;
            if (webView == null || _isClosed || _viewModel == null)
            {
                return;
            }

            if (!Panel.Children.Contains(webView))
            {
                webView.SetBinding(VisibilityProperty, new Binding("BrowserVisibility") { Source = _viewModel });
                AddWebViewToPanel(webView);
            }
        }

        /// <summary>
        /// Paint the in-view contract: while a proxied initialization failure stands, the notice
        /// shows and the blank WebView2 control is collapsed to it (a local Visibility value
        /// outranks the BrowserVisibility binding - WebView2 is an HWND host nothing may be
        /// composited over, so the blank control must step aside, not share the area). When the
        /// failure clears (a recreation attempt began or succeeded), the control gets its
        /// BrowserVisibility binding back. Every write is idempotent.
        /// </summary>
        private void UpdateProfileErrorOverlay()
        {
            if (_isClosed)
            {
                return;
            }

            var failure = _failureBrowser?.InitFailure;
            if (failure != null)
            {
                profileOwnershipCause.Text = failure.CauseLabel;
                profileOwnershipHeadline.Text = failure.Headline;
                profileOwnershipText.Text = failure.Explanation;
                profileOwnershipDetail.Text = failure.Detail;
                profileOwnershipError.Visibility = Visibility.Visible;
                CollapseBrowserWebView();
            }
            else if (profileOwnershipError.Visibility != Visibility.Collapsed)
            {
                profileOwnershipError.Visibility = Visibility.Collapsed;
                RebindBrowserWebViewVisibility();
            }
        }

        private void CollapseBrowserWebView()
        {
            var webView = _failureBrowser?.WebView;
            if (webView == null)
            {
                return;
            }

            if (webView.Visibility != Visibility.Collapsed)
            {
                webView.SetValue(VisibilityProperty, Visibility.Collapsed);
            }
        }

        private void RebindBrowserWebViewVisibility()
        {
            var webView = _failureBrowser?.WebView;
            if (webView == null || _viewModel == null)
            {
                return;
            }

            webView.SetBinding(VisibilityProperty, new Binding("BrowserVisibility") { Source = _viewModel });
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
