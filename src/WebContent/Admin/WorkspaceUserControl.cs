using System;
using System.Windows.Forms;

namespace WebContent.Admin
{
    public partial class WorkspaceUserControl : UserControl
    {
        internal event EventHandler ConfigurationChangedByUser;
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
            get => chkRememberLastAddress.Checked;
            set => chkRememberLastAddress.Checked = value;
        }

        public bool HideToolbar
        {
            get => chkHideToolbar.Checked;
            set => chkHideToolbar.Checked = value;
        }

        public bool HideAddress
        {
            get => chkHideAddress.Checked;
            set => chkHideAddress.Checked = value;
        }

        public double ZoomLevel
        {
            get => double.TryParse(txtZoomLevel.Text, out var z) ? z : 100;
            set => txtZoomLevel.Text = value.ToString("F0");
        }

        public bool ClearBrowsingDataOnExit
        {
            get => chkClearBrowsingDataOnExit.Checked;
            set => chkClearBrowsingDataOnExit.Checked = value;
        }

        public bool AllowBackgroundBrowser
        {
            get => chkAllowBackgroundBrowser.Checked;
            set => chkAllowBackgroundBrowser.Checked = value;
        }

        public bool AllowAudioNotifications
        {
            get => chkAllowAudioNotifications.Checked;
            set => chkAllowAudioNotifications.Checked = value;
        }

        public bool MuteAudio
        {
            get => chkMuteAudio.Checked;
            set => chkMuteAudio.Checked = value;
        }

        public bool IsolateBrowserInstances
        {
            get => chkIsolateBrowserInstances.Checked;
            set => chkIsolateBrowserInstances.Checked = value;
        }

        public bool EnableDevTools
        {
            get => chkEnableDevTools.Checked;
            set => chkEnableDevTools.Checked = value;
        }

        public WorkspaceUserControl()
        {
            InitializeComponent();
        }

        internal void ClearUserControl()
        {
            txtName.Text = "New Workspace";
            txtHomeAddress.Text = string.Empty;
            chkRememberLastAddress.Checked = false;
            chkHideToolbar.Checked = false;
            chkHideAddress.Checked = false;
            txtZoomLevel.Text = "100";
            chkClearBrowsingDataOnExit.Checked = false;
            chkAllowBackgroundBrowser.Checked = true;
            chkAllowAudioNotifications.Checked = true;
            chkMuteAudio.Checked = false;
            chkIsolateBrowserInstances.Checked = false;
            chkEnableDevTools.Checked = false;
        }

        internal void FillContent()
        {
            chkHideAddress.Enabled = !chkHideToolbar.Checked;
        }

        private void chkHideToolbar_CheckedChanged(object sender, EventArgs e)
        {
            chkHideAddress.Enabled = !chkHideToolbar.Checked;
            if (chkHideToolbar.Checked && chkHideAddress.Checked)
            {
                chkHideAddress.Checked = false;
            }
            OnUserChange();
        }

        private void OnUserChange(object sender = null, EventArgs e = null)
        {
            ConfigurationChangedByUser?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Marks the page dirty without any user edit (ticket #27). Used when the stored
        /// configuration is broken so the Management Client Save button is enabled right away
        /// and one click can rewrite a clean definition from the displayed values.
        /// </summary>
        internal void MarkDirty()
        {
            OnUserChange();
        }
    }
}
