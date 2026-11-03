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
    /// Settings tab carries the configuration content, Proxy (#81) edits the structured allowlist,
    /// Diagnostics (#84) renders the Event Server's recently-blocked list with one-click
    /// promotion, and Status (#76) renders the workspace's live proxy sessions - the last two
    /// behind the SecurityActions edit-rights gate, read-only, and polled only while one of them
    /// is the open tab.
    /// All field labels come from <see cref="Strings"/> so the Smart Client
    /// properties page and the Management Client admin page share wording.
    /// </summary>
    public partial class WorkspaceConfigPage : UserControl
    {
        /// <summary>#76/#84: how often an open live tab (Status or Diagnostics) pulls a fresh
        /// snapshot from the Event Server - one query pair feeds both.</summary>
        internal static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(3);

        /// <summary>#76/#84: how long a status query may stay unanswered before the page says so.</summary>
        internal static readonly TimeSpan StatusQueryDeadline = TimeSpan.FromSeconds(2);

        private bool _syncingZoom;
        private bool _proxyTestRunning;
        private bool _statusQueryRunning;
        private string _statusWorkspaceItemId;
        private readonly ProxyStatusRateTracker _statusRates = new ProxyStatusRateTracker();
        private readonly DispatcherTimer _statusPoll;

        /// <summary>#84: the last successful snapshot, kept so an allowlist promotion can re-annotate
        /// the Diagnostics rows (now-allowed state) without waiting for the next poll tick.</summary>
        private ProxyStatusResponsePayload _lastStatusSnapshot;

        // #81: the row collection behind the Proxy tab's structured allowlist grid. _loading
        // suppresses the dirty chain while the item manager fills rows from a stored definition -
        // a fill is not a user edit.
        private readonly ProxyAllowlistEditor _allowlist = new ProxyAllowlistEditor();
        private bool _loadingProxyTargets;

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
            btnProxyTargetAdd.Content = Strings.ProxyAddTargetButton;
            txtProxyWildcardWarning.Text = Strings.ProxyWildcardWarning;

            // #81: the structured allowlist grid. Headers come from Strings like every other
            // label; the column ORDER is pinned by WorkspaceTabShellGuardTests, which also keeps
            // the indexer assignments here honest.
            dgProxyTargets.ItemsSource = _allowlist.Rows;
            if (dgProxyTargets.Columns.Count >= 5)
            {
                dgProxyTargets.Columns[0].Header = Strings.ProxyColumnScheme;
                dgProxyTargets.Columns[1].Header = Strings.ProxyColumnHost;
                dgProxyTargets.Columns[2].Header = Strings.ProxyColumnPort;
                dgProxyTargets.Columns[3].Header = Strings.ProxyColumnAcceptUntrusted;
                dgProxyTargets.Columns[4].Header = Strings.ProxyColumnProblem;
            }

            // Every row edit re-keys the warning banner, the row counter, and the row's localized
            // Problem text, and marks the pane dirty; rows entering/leaving the collection ride
            // the same handler via subscription bookkeeping.
            _allowlist.Rows.CollectionChanged += OnProxyRowsCollectionChanged;
            UpdateProxyRowCounter();

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
        /// #81: the rows behind the structured allowlist grid. The item manager fills them from
        /// the stored definition (either #73 shape) and reads them back on Save; the free-text
        /// one-line-per-entry property is gone with the text box it served.
        /// </summary>
        // internal, not public: the row/record types are assembly-internal (they cross only to
        // WorkspaceUserControl and WorkspaceItemManager, and the unit tests compile them in by link).
        internal System.Collections.Generic.IReadOnlyList<ProxyTargetEditorRow> ProxyTargetRows => _allowlist.Rows;

        /// <summary>
        /// #81: fills the grid from stored proxy target records (legacy <c>host</c> and structured
        /// <c>pattern</c> shapes both render, forever). Returns how many stored records could not
        /// be rendered so the item manager can log them; the fill never marks the pane dirty.
        /// </summary>
        internal int LoadProxyTargets(System.Collections.Generic.IEnumerable<ProxyTargetConfiguration> stored)
        {
            _loadingProxyTargets = true;
            int dropped;
            try
            {
                dropped = _allowlist.Load(stored);
                foreach (var row in _allowlist.Rows)
                {
                    SyncProxyRowErrorText(row);
                }
            }
            finally
            {
                _loadingProxyTargets = false;
            }

            UpdateProxyWildcardWarning();
            UpdateProxyRowCounter();
            return dropped;
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
            _loadingProxyTargets = true;
            try
            {
                _allowlist.Clear();
            }
            finally
            {
                _loadingProxyTargets = false;
            }
            proxyWildcardWarning.Visibility = Visibility.Collapsed;
            UpdateProxyRowCounter();
            txtProxyTestUrl.Text = string.Empty;
            lblProxyTestResult.Text = string.Empty;
            lblProxyTestResult.Visibility = Visibility.Collapsed;
            configError.Visibility = Visibility.Collapsed;
            chkHideAddress.IsEnabled = true;
            // #74 shell: every fresh fill opens on Settings, and with no item context the
            // Status/Diagnostics role gate has nothing to grant — both tabs stay hidden until a
            // fill says otherwise.
            workspaceTabs.SelectedItem = tabSettings;
            tabStatus.Visibility = Visibility.Collapsed;
            tabDiagnostics.Visibility = Visibility.Collapsed;

            // #76: a different item means a different workspace's sessions - drop the old page's
            // rows, its rate baseline, and its pull timer before the next fill.
            StopStatusPolling();
            _statusWorkspaceItemId = null;
            _statusRates.Reset();
            dgStatusSessions.ItemsSource = null;
            txtStatusSummary.Text = string.Empty;
            statusError.Visibility = Visibility.Collapsed;
            txtStatusError.Text = string.Empty;

            // #84: the same for the Diagnostics tab's blocked-origin rows.
            _lastStatusSnapshot = null;
            dgDiagnosticsBlocked.ItemsSource = null;
            txtDiagnosticsSummary.Text = string.Empty;
            diagnosticsError.Visibility = Visibility.Collapsed;
            txtDiagnosticsError.Text = string.Empty;
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
            // never keep querying (or keep rendering) a status feed the user cannot see. Shared
            // with Diagnostics (#84): the loop only stops when NO live tab is open any more.
            if (!visible)
            {
                MaybeStopLivePolling();
            }
        }

        /// <summary>
        /// #84 role gate: the Diagnostics tab shows status-derived data and carries the one
        /// allowlist-writing affordance, so it rides the exact same SecurityActions Manage
        /// (edit-rights) gate as the Status tab — denial or a failed lookup hides the whole tab.
        /// </summary>
        public void SetDiagnosticsTabVisible(bool visible)
        {
            tabDiagnostics.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible && ReferenceEquals(workspaceTabs.SelectedItem, tabDiagnostics))
            {
                workspaceTabs.SelectedItem = tabSettings;
            }

            if (!visible)
            {
                MaybeStopLivePolling();
            }
        }

        /// <summary>
        /// #76/#84: which workspace the page is showing, so the live tabs know what to ask the
        /// Event Server for. The id travels in exactly the form the Smart Client's block reports and
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
                // never inherits the previous one's rate baseline. Same for the blocked ring:
                // every snapshot in flight describes the OTHER workspace.
                _statusRates.Reset();
                _lastStatusSnapshot = null;
                if (IsLiveTabOpen)
                {
                    RefreshStatus();
                }
            }
        }

        private bool IsStatusTabOpen =>
            tabStatus != null && tabStatus.Visibility == Visibility.Visible && ReferenceEquals(workspaceTabs.SelectedItem, tabStatus);

        private bool IsDiagnosticsTabOpen =>
            tabDiagnostics != null && tabDiagnostics.Visibility == Visibility.Visible && ReferenceEquals(workspaceTabs.SelectedItem, tabDiagnostics);

        /// <summary>#84: one status-query pair feeds both live tabs; the pull loop is alive exactly
        /// while either of them is the tab being looked at.</summary>
        private bool IsLiveTabOpen => IsStatusTabOpen || IsDiagnosticsTabOpen;

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, workspaceTabs) && !ReferenceEquals(e.Source, workspaceTabs))
            {
                // TabItem children raise their own selection events; only the TabControl's switch
                // decides whether a live tab is being looked at.
                return;
            }

            if (IsLiveTabOpen)
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

        /// <summary>#84: the Diagnostics tab's explicit re-pull - the same query pair, one answer
        /// renders both tabs.</summary>
        private void OnDiagnosticsRefresh(object sender, RoutedEventArgs e)
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

        /// <summary>#84: a role gate closing one live tab must not kill the loop out from under
        /// the other one - stop only when neither tab is open.</summary>
        private void MaybeStopLivePolling()
        {
            if (!IsLiveTabOpen)
            {
                StopStatusPolling();
            }
        }

        /// <summary>
        /// #76/#84: one pull of the Event Server's status snapshot for this workspace, rendered
        /// into BOTH live tabs (sessions on Status, blocked origins on Diagnostics). Re-entrancy
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
                RenderDiagnosticsFailure(Strings.StatusNoItemContext);
                return;
            }

            var client = ProxyFetchClient.Active;
            if (client == null || !client.IsReady)
            {
                RenderStatusFailure(Strings.StatusNotConnected);
                RenderDiagnosticsFailure(Strings.StatusNotConnected);
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
                    var failureText = StatusFailureText(view.FailureCode, response);
                    RenderStatusFailure(failureText);
                    RenderDiagnosticsFailure(failureText);
                    return;
                }

                _lastStatusSnapshot = response;
                var blockedCount = response.Blocked == null ? 0 : response.Blocked.Count;
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(WorkspaceConfigPage),
                    $"Proxy status snapshot for workspace '{_statusWorkspaceItemId}' answered: {view.SessionCount} live session(s), {blockedCount} blocked origin(s).");
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

                RenderDiagnostics(ProxyDiagnosticsViewBuilder.Build(
                    response,
                    _allowlist.ValidCanonicalEntries(),
                    VmsFloorHosts(),
                    DateTime.UtcNow));
            }
            catch (Exception exception)
            {
                // A failed pull must not kill the timer or throw through the WPF dispatcher; the
                // next tick simply asks again.
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(WorkspaceConfigPage),
                    $"Proxy status query for workspace '{_statusWorkspaceItemId}' threw: {exception.Message}");
                var failureText = string.Format(
                    CultureInfo.InvariantCulture,
                    Strings.StatusQueryFailedFormat,
                    ProxyFetchErrorCodes.Error,
                    exception.Message);
                RenderStatusFailure(failureText);
                RenderDiagnosticsFailure(failureText);
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

        // ---- #84: Diagnostics tab wiring ---------------------------------------------------

        /// <summary>
        /// One render of the Diagnostics tab. The rows arrive pre-formatted (the builder is pure
        /// and unit-tested); only the Status cell's localized sentence is written here, the same
        /// code→text split the #81 grid uses so the XAML stays BAML-trivial.
        /// </summary>
        private void RenderDiagnostics(ProxyDiagnosticsView view)
        {
            foreach (var row in view.Rows)
            {
                switch (row.StatusCode)
                {
                    case ProxyBlockedRowStatus.Allowlisted:
                        row.StatusText = Strings.DiagnosticsStatusAllowlisted;
                        break;
                    case ProxyBlockedRowStatus.CertTrust:
                        row.StatusText = Strings.DiagnosticsStatusCertTrust;
                        break;
                    default:
                        row.StatusText = string.Empty;
                        break;
                }
            }

            dgDiagnosticsBlocked.ItemsSource = view.Rows;
            txtDiagnosticsSummary.Text = view.BlockedCount == 0
                ? Strings.DiagnosticsEmpty
                : string.Format(CultureInfo.InvariantCulture, Strings.DiagnosticsSummaryFormat, view.BlockedCount);
            diagnosticsError.Visibility = Visibility.Collapsed;
            txtDiagnosticsError.Text = string.Empty;
        }

        private void RenderDiagnosticsFailure(string text)
        {
            _lastStatusSnapshot = null;
            dgDiagnosticsBlocked.ItemsSource = null;
            txtDiagnosticsSummary.Text = string.Empty;
            txtDiagnosticsError.Text = text;
            diagnosticsError.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// #84: the one-click "Add to allowlist" promotion. The row's canonical blocked origin
        /// becomes an exact scheme+host+port row in the Proxy tab's structured grid (identical to
        /// what the grid itself would save — the admin can then widen it into a wildcard); the
        /// row-collection chain dirties the pane exactly like a hand-typed row. Then the tab
        /// re-annotates from the last snapshot WITHOUT a new query, so the row visibly updates to
        /// its now-allowed state. CertUntrusted rows never carry the button; the builder already
        /// hid it, and the promotion path refuses such an origin again as a guard.
        /// </summary>
        private void OnDiagnosticsPromote(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as ProxyBlockedRowView;
            if (row == null || string.IsNullOrEmpty(row.Origin))
            {
                return;
            }

            var outcome = _allowlist.TryAddExactOrigin(row.Origin, out _);
            EnvironmentManager.Instance.Log(
                false,
                nameof(WorkspaceConfigPage),
                $"Proxy allowlist promotion for workspace '{_statusWorkspaceItemId}': origin '{row.Origin}' {DescribePromotion(outcome)}" +
                (outcome == ProxyPromotionOutcome.Added ? " (grid edit, not yet saved - Save writes it)." : "."));

            if (outcome == ProxyPromotionOutcome.Refused)
            {
                RenderDiagnosticsFailure(string.Format(
                    CultureInfo.InvariantCulture,
                    Strings.DiagnosticsPromotionRefusedFormat,
                    row.Origin));
                return;
            }

            // Immediate honest feedback: re-annotate the whole list against the grid as it now
            // stands. The promoted row and any same-origin sibling flip to "on allowlist"; no
            // button remains on them, so a double click cannot stack duplicates.
            if (_lastStatusSnapshot != null)
            {
                RenderDiagnostics(ProxyDiagnosticsViewBuilder.Build(
                    _lastStatusSnapshot,
                    _allowlist.ValidCanonicalEntries(),
                    VmsFloorHosts(),
                    DateTime.UtcNow));
            }
        }

        private static string DescribePromotion(ProxyPromotionOutcome outcome)
        {
            switch (outcome)
            {
                case ProxyPromotionOutcome.Added: return "added to the allowlist grid";
                case ProxyPromotionOutcome.AlreadyPresent: return "already covered by an existing entry";
                default: return "refused (not an exact allowlist origin)";
            }
        }

        /// <summary>
        /// #73/#84: the VMS's own host names for the wildcard SSRF floor - the same
        /// <c>EnvironmentManager</c> source the Smart Client feeds its forward gate, so the
        /// Diagnostics annotation ("this origin is already covered") can never disagree with what
        /// the Event Server's gate would actually decide for a wildcard entry. Absent (offline)
        /// just leaves the matcher's syntactic floor (IPs, localhost).
        /// </summary>
        private static string[] VmsFloorHosts()
        {
            try
            {
                var host = EnvironmentManager.Instance?.MasterSite?.ServerId?.Uri?.Host;
                return string.IsNullOrWhiteSpace(host) ? null : new[] { host };
            }
            catch (Exception)
            {
                return null;
            }
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

        // ---- #81: structured allowlist grid wiring --------------------------------------

        private void OnProxyRowsCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ProxyTargetEditorRow row in e.OldItems)
                {
                    row.PropertyChanged -= OnProxyRowPropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (ProxyTargetEditorRow row in e.NewItems)
                {
                    SyncProxyRowErrorText(row);
                    row.PropertyChanged += OnProxyRowPropertyChanged;
                }
            }

            UpdateProxyWildcardWarning();
            UpdateProxyRowCounter();
            if (!_loadingProxyTargets)
            {
                ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnProxyRowPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProxyTargetEditorRow.ErrorCode))
            {
                SyncProxyRowErrorText((ProxyTargetEditorRow)sender);
            }

            UpdateProxyWildcardWarning();
            if (!_loadingProxyTargets)
            {
                ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnProxyTargetAdd(object sender, RoutedEventArgs e)
        {
            _allowlist.AddBlankRow();
        }

        private void OnProxyRowRemove(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement)?.DataContext as ProxyTargetEditorRow;
            if (row != null)
            {
                _allowlist.Remove(row);
            }
        }

        /// <summary>
        /// The row model carries a machine-readable error code; the Problem cell shows the
        /// localized sentence for it. Kept on the row (not a converter) so the grid XAML stays
        /// BAML-trivial - the #76 tri-state lesson: anything exotic in this page's XAML takes
        /// the WHOLE config page down at load time.
        /// </summary>
        private void SyncProxyRowErrorText(ProxyTargetEditorRow row)
        {
            switch (row.ErrorCode)
            {
                case ProxyTargetRowError.HostRequired:
                    row.ErrorText = Strings.ProxyRowErrorHostRequired;
                    break;
                case ProxyTargetRowError.PortInvalid:
                    row.ErrorText = Strings.ProxyRowErrorPortInvalid;
                    break;
                case ProxyTargetRowError.HostOrPatternInvalid:
                    row.ErrorText = string.Format(
                        CultureInfo.InvariantCulture,
                        Strings.ProxyRowErrorHostInvalid,
                        (row.Host ?? string.Empty).Trim());
                    break;
                default:
                    row.ErrorText = string.Empty;
                    break;
            }
        }

        private void UpdateProxyWildcardWarning()
        {
            if (proxyWildcardWarning == null)
            {
                return;
            }

            proxyWildcardWarning.Visibility = _allowlist.HasFullHostWildcard ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateProxyRowCounter()
        {
            if (lblProxyRowCounter == null)
            {
                return;
            }

            lblProxyRowCounter.Text = string.Format(
                CultureInfo.InvariantCulture,
                Strings.ProxyRowCounterFormat,
                _allowlist.Count);
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

                // #65: stamp the workspace this page is editing so the Event Server can evaluate
                // this workspace's rule-trigger rows against a test fetch too.
                var result = await client.FetchAsync(url, _statusWorkspaceItemId);
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
