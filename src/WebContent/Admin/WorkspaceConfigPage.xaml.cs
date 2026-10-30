using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WebContent.Admin
{
    /// <summary>
    /// The WPF workspace configuration page, hosted inside the WinForms
    /// <see cref="WorkspaceUserControl"/> wrapper through an ElementHost.
    /// All field labels come from <see cref="Strings"/> so the Smart Client
    /// properties page and the Management Client admin page share wording.
    /// </summary>
    public partial class WorkspaceConfigPage : UserControl
    {
        private bool _syncingZoom;

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
            configError.Visibility = Visibility.Collapsed;
            chkHideAddress.IsEnabled = true;
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
    }
}
