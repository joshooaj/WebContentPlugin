using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace WebContent.Admin
{
    /// <summary>
    /// Thin Windows Forms wrapper required by the Management Client detail-page
    /// contract. The actual configuration form is the WPF <see cref="WorkspaceConfigPage"/>
    /// hosted through an <see cref="ElementHost"/>. The wrapper exists only to
    /// satisfy the WinForms return type of <c>GenerateDetailUserControl</c> and to
    /// forward the framework-facing members the shell calls.
    /// </summary>
    public class WorkspaceUserControl : UserControl
    {
        private readonly WorkspaceConfigPage _page;
        private readonly ElementHost _host;

        internal event EventHandler ConfigurationChangedByUser;

        public WorkspaceUserControl()
        {
            // Dock.Fill in the constructor is required: Management Client keeps
            // the returned control at its 150x150 design size unless it stretches.
            Dock = DockStyle.Fill;

            _page = new WorkspaceConfigPage();
            _page.ConfigurationChanged += OnPageChanged;

            _host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = _page,
                TabStop = false, // ElementHost honors WinForms TabStop, not WPF IsTabStop.
            };
            Controls.Add(_host);
        }

        public string WorkspaceName
        {
            get => _page.WorkspaceName;
            set => _page.WorkspaceName = value;
        }

        public string HomeAddress
        {
            get => _page.HomeAddress;
            set => _page.HomeAddress = value;
        }

        public string Address
        {
            get => HomeAddress;
            set => HomeAddress = value;
        }

        public bool RememberLastAddress
        {
            get => _page.RememberLastAddress;
            set => _page.RememberLastAddress = value;
        }

        public bool HideToolbar
        {
            get => _page.HideToolbar;
            set => _page.HideToolbar = value;
        }

        public bool HideAddress
        {
            get => _page.HideAddress;
            set => _page.HideAddress = value;
        }

        public double ZoomLevel
        {
            get => _page.ZoomLevel;
            set => _page.ZoomLevel = value;
        }

        public bool ClearBrowsingDataOnExit
        {
            get => _page.ClearBrowsingDataOnExit;
            set => _page.ClearBrowsingDataOnExit = value;
        }

        public bool AllowBackgroundBrowser
        {
            get => _page.AllowBackgroundBrowser;
            set => _page.AllowBackgroundBrowser = value;
        }

        public bool AllowAudioNotifications
        {
            get => _page.AllowAudioNotifications;
            set => _page.AllowAudioNotifications = value;
        }

        public bool MuteAudio
        {
            get => _page.MuteAudio;
            set => _page.MuteAudio = value;
        }

        public bool IsolateBrowserInstances
        {
            get => _page.IsolateBrowserInstances;
            set => _page.IsolateBrowserInstances = value;
        }

        public bool EnableDevTools
        {
            get => _page.EnableDevTools;
            set => _page.EnableDevTools = value;
        }

        public bool ProxyEnabled
        {
            get => _page.ProxyEnabled;
            set => _page.ProxyEnabled = value;
        }

        /// <summary>
        /// #85: forwards the Proxy tab's max-response-size field (whole-MB text) to the item
        /// manager for fill and validated save.
        /// </summary>
        internal string ProxyResponseCapText
        {
            get => _page.ProxyResponseCapText;
            set => _page.ProxyResponseCapText = value;
        }

        /// <summary>
        /// #81: forwards the structured allowlist grid's row surface to the item manager. The
        /// free-text ProxyTargetsText property left with the txtProxyTargets box it served.
        /// </summary>
        internal System.Collections.Generic.IReadOnlyList<ProxyTargetEditorRow> ProxyTargetRows => _page.ProxyTargetRows;

        internal int LoadProxyTargets(System.Collections.Generic.IEnumerable<ProxyTargetConfiguration> stored)
        {
            return _page.LoadProxyTargets(stored);
        }

        // #86: the "Advanced: headers" grids' row surface, forwarded to the item manager exactly
        // like the allowlist grid's.
        internal System.Collections.Generic.IReadOnlyList<ProxyHeaderEditorRow> ProxyRequestHeaderRows => _page.ProxyRequestHeaderRows;

        internal System.Collections.Generic.IReadOnlyList<ProxyHeaderEditorRow> ProxyResponseHeaderRows => _page.ProxyResponseHeaderRows;

        internal int LoadProxyHeaderPolicies(
            System.Collections.Generic.IEnumerable<ProxyHeaderRuleConfiguration> requestStored,
            System.Collections.Generic.IEnumerable<ProxyHeaderRuleConfiguration> responseStored)
        {
            return _page.LoadProxyHeaderPolicies(requestStored, responseStored);
        }

        internal void ClearUserControl()
        {
            _page.ClearUserControl();
        }

        internal void FillContent()
        {
            _page.FillContent();
        }

        /// <summary>
        /// Ticket #27: a definition that failed detection loads with fallback values, so the page
        /// must both say so (red banner) and become dirty so the shell's Save button is enabled.
        /// The change event is re-raised after the current fill completes because the shell resets
        /// dirty state once <c>FillUserControl</c> returns — a synchronous raise is swallowed.
        /// </summary>
        internal void SignalLoadDefect(string bannerText)
        {
            _page.ShowConfigurationError(bannerText);
            RaiseDeferredUserChange();
        }

        /// <summary>
        /// A freshly created workspace loads with empty values that the user is about to complete.
        /// That is not a defect, so no banner is shown — but the fill still resets the shell's
        /// dirty state, so the deferred change event is raised to keep Save enabled.
        /// </summary>
        internal void SignalUnfinishedNewItem()
        {
            _page.ClearConfigurationError();
            RaiseDeferredUserChange();
        }

        internal void ClearConfigurationError()
        {
            _page.ClearConfigurationError();
        }

        /// <summary>
        /// #74: forwards the Status tab role-gate decision to the WPF page.
        /// </summary>
        internal void SetStatusTabVisible(bool visible)
        {
            _page.SetStatusTabVisible(visible);
        }

        /// <summary>
        /// #84: forwards the Diagnostics tab role-gate decision (same edit-rights check) to the
        /// WPF page.
        /// </summary>
        internal void SetDiagnosticsTabVisible(bool visible)
        {
            _page.SetDiagnosticsTabVisible(visible);
        }

        /// <summary>
        /// #76: tells the Status tab which workspace item to ask the Event Server about.
        /// </summary>
        internal void SetWorkspaceItemId(string workspaceItemId)
        {
            _page.SetWorkspaceItemId(workspaceItemId);
        }

        private void OnPageChanged(object sender, EventArgs e)
        {
            ConfigurationChangedByUser?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseDeferredUserChange()
        {
            if (IsHandleCreated)
            {
                BeginInvoke((MethodInvoker)delegate { OnPageChanged(this, EventArgs.Empty); });
            }
            else
            {
                HandleCreated += DeferredUserChangeOnce;
            }
        }

        private void DeferredUserChangeOnce(object sender, EventArgs e)
        {
            HandleCreated -= DeferredUserChangeOnce;
            BeginInvoke((MethodInvoker)delegate { OnPageChanged(this, EventArgs.Empty); });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_page != null)
                {
                    _page.ConfigurationChanged -= OnPageChanged;
                }
                _host?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
