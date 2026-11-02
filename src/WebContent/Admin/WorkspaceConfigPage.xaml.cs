using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

using VideoOS.Platform;

namespace WebContent.Admin
{
    /// <summary>
    /// The WPF workspace configuration page, hosted inside the WinForms
    /// <see cref="WorkspaceUserControl"/> wrapper through an ElementHost.
    /// Since #74 the page is a four-tab shell (Settings | Proxy | Diagnostics | Status): the
    /// Settings tab carries the configuration content, Proxy/Diagnostics are still landing panels
    /// with no input controls, and Status (#76) renders the workspace's live proxy sessions behind
    /// the SecurityActions edit-rights gate — read-only, and polled only while that tab is open.
    /// All field labels come from <see cref="Strings"/> so the Smart Client
    /// properties page and the Management Client admin page share wording.
    /// </summary>
    public partial class WorkspaceConfigPage : UserControl
    {
        /// <summary>#76: how often the open Status tab pulls a fresh snapshot from the Event Server.</summary>
        internal static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(3);

        /// <summary>#76: how long a status query may stay unanswered before the page says so.</summary>
        internal static readonly TimeSpan StatusQueryDeadline = TimeSpan.FromSeconds(2);

        private bool _syncingZoom;
        private bool _proxyTestRunning;
        private bool _statusQueryRunning;
        private string _statusWorkspaceItemId;
        private readonly ProxyStatusRateTracker _statusRates = new ProxyStatusRateTracker();
        private readonly DispatcherTimer _statusPoll;

        internal event EventHandler ConfigurationChanged;

        public WorkspaceConfigPage()
        {
            InitializeComponent();
            lblZoomLevel.Text = Strings.ZoomLevelLabel + ":";
            chkHideToolbar.Content = Strings.HideToolbarLabel;
            chkHideAddress.Content = Strings.HideAddressLabel;
            chkRememberLastAddress.Content = Strings.RememberLastAddressLabel;
            chkAllowBackgroundBrowser.Content = Strings.AllowBackgroundBrowserLabel;
            chkAllowAudioNotifications.Content = Strings.AllowAudioNotificationsLabel;
            chkMuteAudio.Content = Strings.MuteAudioLabel;
            chkClearBrowsingDataOnExit.Content = Strings.ClearBrowsingDataOnExitLabel;
            chkIsolateBrowserInstances.Content = Strings.IsolateBrowserInstancesLabel;
            chkEnableDevTools.Content = Strings.EnableDevToolsLabel;
            chkProxyEnabled.Content = Strings.ProxyEnabledLabel;
            lblProxyTargets.Text = Strings.ProxyTargetsLabel + ":";
            lblProxyTargetsHint.Text = Strings.ProxyTargetsHint;
            lblProxyTestUrl.Text = Strings.ProxyTestFetchLabel + ":";
            lblProxyTestHint.Text = Strings.ProxyTestFetchHint;

            // #76: the pull loop exists only while the Status tab is the one being looked at. No
            // push channel, no persistence, and no timer running for a page nobody is reading.
            _statusPoll = new DispatcherTimer { Interval = StatusPollInterval };
            _statusPoll.Tick += (sender, args) => RefreshStatus();
            workspaceTabs.SelectionChanged += OnTabSelectionChanged;
        }

        public string WorkspaceName
        {
            get => txtName.Text;
            set => txtName.Text = value;
        }

        public string HomeAddress
        {
            get => txtHomeAddress.Text;
            set => txtHomeAddress.Text = value;
        }

        public string Address
        {
            get => HomeAddress;
            set => HomeAddress = value;
        }

        public bool RememberLastAddress
        {
            get => chkRememberLastAddress.IsChecked == true;
            set => chkRememberLastAddress.IsChecked = value;
        }

        public bool HideToolbar
        {
            get => chkHideToolbar.IsChecked == true;
            set => chkHideToolbar.IsChecked = value;
        }

        public bool HideAddress
        {
            get => chkHideAddress.IsChecked == true;
            set => chkHideAddress.IsChecked = value;
        }

        public double ZoomLevel
        {
            get => double.TryParse(txtZoomLevel.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
                ? z
                : WebContentConfiguration.DefaultZoomLevel;
            set
            {
                var clamped = WebContentConfiguration.ClampZoomLevel(value);
                _syncingZoom = true;
                txtZoomLevel.Text = clamped.ToString("F0", CultureInfo.InvariantCulture);
                if (Math.Abs(zoomSlider.Value - clamped) > 0.01)
                {
                    zoomSlider.Value = clamped;
                }
                _syncingZoom = false;
            }
        }

        public bool ClearBrowsingDataOnExit
        {
            get => chkClearBrowsingDataOnExit.IsChecked == true;
            set => chkClearBrowsingDataOnExit.IsChecked = value;
        }

        public bool AllowBackgroundBrowser
        {
            get => chkAllowBackgroundBrowser.IsChecked == true;
            set => chkAllowBackgroundBrowser.IsChecked = value;
        }

        public bool AllowAudioNotifications
        {
            get => chkAllowAudioNotifications.IsChecked == true;
            set => chkAllowAudioNotifications.IsChecked = value;
        }

        public bool MuteAudio
        {
            get => chkMuteAudio.IsChecked == true;
            set => chkMuteAudio.IsChecked = value;
        }

        public bool IsolateBrowserInstances
        {
            get => chkIsolateBrowserInstances.IsChecked == true;
            set => chkIsolateBrowserInstances.IsChecked = value;
        }

        public bool EnableDevTools
        {
            get => chkEnableDevTools.IsChecked == true;
            set => chkEnableDevTools.IsChecked = value;
        }

        public bool ProxyEnabled
        {
            get => chkProxyEnabled.IsChecked == true;
            set => chkProxyEnabled.IsChecked = value;
        }

        /// <summary>
        /// The allowlisted proxy targets, one origin per line as the admin types them
        /// (ticket #52). The item manager validates and canonicalizes the lines on save.
        /// </summary>
        public string ProxyTargetsText
        {
            get => txtProxyTargets.Text;
            set => txtProxyTargets.Text = value ?? string.Empty;
        }

        public void ClearUserControl()
        {
            txtName.Text = "New Workspace";
            txtHomeAddress.Text = string.Empty;
            chkRememberLastAddress.IsChecked = false;
            chkHideToolbar.IsChecked = false;
            chkHideAddress.IsChecked = false;
            ZoomLevel = WebContentConfiguration.DefaultZoomLevel;
            chkClearBrowsingDataOnExit.IsChecked = false;
            chkAllowBackgroundBrowser.IsChecked = true;
            chkAllowAudioNotifications.IsChecked = true;
            chkMuteAudio.IsChecked = false;
            chkIsolateBrowserInstances.IsChecked = false;
            chkEnableDevTools.IsChecked = false;
            chkProxyEnabled.IsChecked = false;
            txtProxyTargets.Text = string.Empty;
            txtProxyTestUrl.Text = string.Empty;
            lblProxyTestResult.Text = string.Empty;
            lblProxyTestResult.Visibility = Visibility.Collapsed;
            configError.Visibility = Visibility.Collapsed;
            chkHideAddress.IsEnabled = true;
            // #74 shell: every fresh fill opens on Settings, and with no item context the
            // Status role gate has nothing to grant — the tab stays hidden until a fill says otherwise.
            workspaceTabs.SelectedItem = tabSettings;
            tabStatus.Visibility = Visibility.Collapsed;

            // #76: a different item means a different workspace's sessions - drop the old page's
            // rows, its rate baseline, and its pull timer before the next fill.
            StopStatusPolling();
            _statusWorkspaceItemId = null;
            _statusRates.Reset();
            dgStatusSessions.ItemsSource = null;
            txtStatusSummary.Text = string.Empty;
            statusError.Visibility = Visibility.Collapsed;
            txtStatusError.Text = string.Empty;
        }

        public void FillContent()
        {
            chkHideAddress.IsEnabled = chkHideToolbar.IsChecked != true;
        }

        public void ShowConfigurationError(string bannerText)
        {
            configErrorText.Text = bannerText;
            configError.Visibility = Visibility.Visible;
        }

        public void ClearConfigurationError()
        {
            configError.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// #74 role gate: the Status tab renders only when the current Management Client user
        /// passes the workspace item's SecurityActions Manage (edit-rights) check. Hiding it also
        /// deselects it if it was somehow the active tab.
        /// </summary>
        public void SetStatusTabVisible(bool visible)
        {
            tabStatus.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible && ReferenceEquals(workspaceTabs.SelectedItem, tabStatus))
            {
                workspaceTabs.SelectedItem = tabSettings;
            }

            // A gate that closes mid-session must stop the pull loop with the tab: the page must
            // never keep querying (or keep rendering) a status feed the user cannot see.
            if (!visible)
            {
                StopStatusPolling();
            }
        }

        /// <summary>
        /// #76: which workspace the page is showing, so the Status tab knows what to ask the Event
        /// Server for. The id travels in exactly the form the Smart Client's block reports and
        /// session events carry (the workspace item's <c>FQID.ObjectId.ToString()</c>), because the
        /// Event Server matches it as a string against its own workspace lookup.
        /// </summary>
        public void SetWorkspaceItemId(string workspaceItemId)
        {
            var changed = !string.Equals(_statusWorkspaceItemId, workspaceItemId, StringComparison.Ordinal);
            _statusWorkspaceItemId = workspaceItemId;
            if (changed)
            {
                // Byte counters are per session and per Event Server process: a new item context
                // never inherits the previous one's rate baseline.
                _statusRates.Reset();
                if (IsStatusTabOpen)
                {
                    RefreshStatus();
                }
            }
        }

        private bool IsStatusTabOpen =>
            tabStatus != null && tabStatus.Visibility == Visibility.Visible && ReferenceEquals(workspaceTabs.SelectedItem, tabStatus);

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, workspaceTabs) && !ReferenceEquals(e.Source, workspaceTabs))
            {
                // TabItem children raise their own selection events; only the TabControl's switch
                // decides whether the Status tab is being looked at.
                return;
            }

            if (IsStatusTabOpen)
            {
                StartStatusPolling();
            }
            else
            {
                StopStatusPolling();
            }
        }

        private void OnStatusRefresh(object sender, RoutedEventArgs e)
        {
            RefreshStatus();
        }

        private void StartStatusPolling()
        {
            if (_statusPoll.IsEnabled)
            {
                return;
            }

            _statusPoll.Start();

            // The first snapshot comes immediately - a tab that opens onto three seconds of blank
            // space reads as broken even when it is not.
            RefreshStatus();
        }

        private void StopStatusPolling()
        {
            if (_statusPoll != null)
            {
                _statusPoll.Stop();
            }
        }

        /// <summary>
        /// #76: one pull of the Event Server's status snapshot for this workspace. Re-entrancy
        /// guarded, and no ConfigureAwait(false) anywhere: every statement after the await touches
        /// WPF elements and must stay on the UI thread.
        /// </summary>
        private async void RefreshStatus()
        {
            if (_statusQueryRunning)
            {
                return;
            }

            if (string.IsNullOrEmpty(_statusWorkspaceItemId))
            {
                RenderStatusFailure(Strings.StatusNoItemContext);
                return;
            }

            var client = ProxyFetchClient.Active;
            if (client == null || !client.IsReady)
            {
                RenderStatusFailure(Strings.StatusNotConnected);
                return;
            }

            _statusQueryRunning = true;
            try
            {
                var response = await client.RequestWorkspaceStatusAsync(_statusWorkspaceItemId, StatusQueryDeadline);
                var view = ProxyStatusViewBuilder.Build(response, _statusRates, DateTime.UtcNow);
                if (!view.QuerySucceeded)
                {
                    // #76 acceptance trace: every pull of the poll loop leaves exactly one line in
                    // the MC's MIP.log, so "polling stopped when the tab closed" is provable from
                    // the messaging trace, not narrated.
                    EnvironmentManager.Instance.Log(
                        true,
                        nameof(WorkspaceConfigPage),
                        $"Proxy status query for workspace '{_statusWorkspaceItemId}' failed (code={view.FailureCode}).");
                    RenderStatusFailure(StatusFailureText(view.FailureCode, response));
                    return;
                }

                EnvironmentManager.Instance.Log(
                    false,
                    nameof(WorkspaceConfigPage),
                    $"Proxy status snapshot for workspace '{_statusWorkspaceItemId}' answered: {view.SessionCount} live session(s).");
                dgStatusSessions.ItemsSource = view.Sessions;
                statusError.Visibility = Visibility.Collapsed;
                txtStatusError.Text = string.Empty;
                txtStatusSummary.Text = view.SessionCount == 0
                    ? Strings.StatusNoSessions
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        Strings.StatusSummaryFormat,
                        view.SessionCount,
                        view.TotalRequestsPerMinute,
                        ProxyStatusFormat.Rate(view.SendBytesPerSecond),
                        ProxyStatusFormat.Rate(view.ReceiveBytesPerSecond));
            }
            catch (Exception exception)
            {
                // A failed pull must not kill the timer or throw through the WPF dispatcher; the
                // next tick simply asks again.
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(WorkspaceConfigPage),
                    $"Proxy status query for workspace '{_statusWorkspaceItemId}' threw: {exception.Message}");
                RenderStatusFailure(string.Format(
                    CultureInfo.InvariantCulture,
                    Strings.StatusQueryFailedFormat,
                    ProxyFetchErrorCodes.Error,
                    exception.Message));
            }
            finally
            {
                _statusQueryRunning = false;
            }
        }

        /// <summary>
        /// The Event Server's failure codes the admin can act on get their own sentence: an unknown
        /// workspace (the item was deleted, or this page is stale) reads differently from an Event
        /// Server that never answered.
        /// </summary>
        private static string StatusFailureText(string code, ProxyStatusResponsePayload response)
        {
            switch (code)
            {
                case ProxyFetchErrorCodes.BadRequest:
                    return Strings.StatusWorkspaceGone;
                case ProxyFetchErrorCodes.ServerUnavailable:
                    return Strings.StatusNotConnected;
                default:
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        Strings.StatusQueryFailedFormat,
                        code,
                        response == null ? "no response" : response.ErrorMessage);
            }
        }

        private void RenderStatusFailure(string text)
        {
            dgStatusSessions.ItemsSource = null;
            txtStatusSummary.Text = string.Empty;
            txtStatusError.Text = text;
            statusError.Visibility = Visibility.Visible;
        }

        private void OnUserChanged(object sender, RoutedEventArgs e)
        {
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnHideToolbarChanged(object sender, RoutedEventArgs e)
        {
            chkHideAddress.IsEnabled = chkHideToolbar.IsChecked != true;
            if (chkHideToolbar.IsChecked == true && chkHideAddress.IsChecked == true)
            {
                chkHideAddress.IsChecked = false;
            }
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnZoomLevelTextChanged(object sender, TextChangedEventArgs e)
        {
            // Guards against null fields: BAML load fires these handlers while the
            // object graph is still being built (initial Text/Value assignments).
            if (!_syncingZoom && zoomSlider != null && double.TryParse(txtZoomLevel.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
            {
                _syncingZoom = true;
                zoomSlider.Value = WebContentConfiguration.ClampZoomLevel(z);
                _syncingZoom = false;
            }
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnZoomSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_syncingZoom && txtZoomLevel != null)
            {
                _syncingZoom = true;
                txtZoomLevel.Text = e.NewValue.ToString("F0", CultureInfo.InvariantCulture);
                _syncingZoom = false;
            }
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnProxyTargetsTextChanged(object sender, TextChangedEventArgs e)
        {
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Ticket #53 demo harness: sends a proxy-fetch request from the Management Client over
        /// classic MIP messaging (ProxyFetchClient.Active, provisioned during Init) and shows the
        /// status line, body size, and duration that came back from the Event Server fetcher.
        /// No ConfigureAwait(false) anywhere: this is a WPF event handler, and every statement
        /// after the await - including the finally block - must stay on the UI thread.
        /// </summary>
        private async void OnProxyTestFetch(object sender, RoutedEventArgs e)
        {
            if (_proxyTestRunning)
            {
                return;
            }

            _proxyTestRunning = true;
            btnProxyTestFetch.IsEnabled = false;
            lblProxyTestResult.Visibility = Visibility.Visible;
            lblProxyTestResult.Text = Strings.ProxyTestFetchRunning;

            try
            {
                var client = ProxyFetchClient.Active;
                if (client == null || !client.IsReady)
                {
                    lblProxyTestResult.Text = Strings.ProxyTestFetchNotConnected;
                    return;
                }

                var url = (txtProxyTestUrl.Text ?? string.Empty).Trim();
                var stopwatch = Stopwatch.StartNew();
                var result = await client.FetchAsync(url);
                stopwatch.Stop();

                if (result.Success)
                {
                    lblProxyTestResult.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        Strings.ProxyTestFetchSuccessFormat,
                        result.StatusCode,
                        result.Body == null ? 0L : (long)result.Body.LongLength,
                        stopwatch.Elapsed.TotalSeconds);
                }
                else
                {
                    lblProxyTestResult.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        Strings.ProxyTestFetchFailureFormat,
                        result.ErrorCode,
                        stopwatch.Elapsed.TotalSeconds,
                        result.ErrorMessage);
                }
            }
            catch (Exception exception)
            {
                lblProxyTestResult.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    Strings.ProxyTestFetchFailureFormat,
                    ProxyFetchErrorCodes.Error,
                    0d,
                    exception.Message);
            }
            finally
            {
                _proxyTestRunning = false;
                if (btnProxyTestFetch != null)
                {
                    btnProxyTestFetch.IsEnabled = true;
                }
            }
        }
    }
}