using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WebContent;

namespace WebContent.Client
{
    public class WebContentSettings : INotifyPropertyChanged, IDisposable
    {
        public string HomeLabel => $"{Strings.HomeLabel}:";
        public string RememberLastAddressLabel => Strings.RememberLastAddressLabel;
        public string HideToolbarLabel => Strings.HideToolbarLabel;
        public string HideAddressLabel => Strings.HideAddressLabel;
        public string ZoomLevelLabel => $"{Strings.ZoomLevelLabel}:";
        public string ClearBrowsingDataOnExitLabel => Strings.ClearBrowsingDataOnExitLabel;
        public string EnableDevToolsLabel => Strings.EnableDevToolsLabel;
        public string EnableDevToolsToolTip => Strings.EnableDevToolsToolTip;
        public string ClearBrowsingDataNowLabel => Strings.ClearBrowsingDataNowLabel;
        public string AllowBackgroundBrowserLabel => Strings.AllowBackgroundBrowserLabel;
        public string IsolateBrowserInstancesLabel => Strings.IsolateBrowserInstancesLabel;
        public string IsolateBrowserInstancesToolTip => Strings.IsolateBrowserInstancesToolTip;
        public string RememberLastAddressToolTip => Strings.RememberLastAddressToolTip;
        public string AllowBackgroundBrowserToolTip => Strings.AllowBackgroundBrowserToolTip;
        public string HideToolbarToolTip => Strings.HideToolbarToolTip;
        public string HideAddressToolTip => Strings.HideAddressToolTip;
        public string ClearBrowsingDataOnExitToolTip => Strings.ClearBrowsingDataOnExitToolTip;
        public string AllowAudioNotificationsLabel => Strings.AllowAudioNotificationsLabel;
        public string AllowAudioNotificationsToolTip => Strings.AllowAudioNotificationsToolTip;
        public string MuteAudioLabel => Strings.MuteAudioLabel;
        public string MuteAudioToolTip => Strings.MuteAudioToolTip;

        public bool IsHideAddressEnabled => !HideToolbar;

        public string HomeAddress
        {
            get => WebContentConfiguration.NormalizeHomeAddress(
                WebContentConfiguration.GetString(GetProperty, Strings.HomeAddress, nameof(HomeAddress), "Address"));
            set
            {
                string normalizedAddress;
                if (string.IsNullOrWhiteSpace(value))
                {
                    normalizedAddress = WebContentConfiguration.NormalizeHomeAddress(value);
                }
                else if (WebContentConfiguration.TryCreateNavigationUri(value, out var uri))
                {
                    normalizedAddress = uri.AbsoluteUri;
                }
                else
                {
                    throw new ArgumentException(string.Format(Strings.InvalidAddressMessage, value), nameof(value));
                }

                if (string.Equals(HomeAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase)) return;
                SetProperty(nameof(HomeAddress), normalizedAddress);
                SetProperty("Address", normalizedAddress);
                OnPropertyChanged();
            }
        }

        public bool RememberLastAddress
        {
            get => WebContentConfiguration.GetBoolean(GetProperty, false, nameof(RememberLastAddress));
            set
            {
                if (RememberLastAddress == value) return;
                SetProperty(nameof(RememberLastAddress), WebContentConfiguration.FormatBoolean(value));
                OnPropertyChanged();
            }
        }

        public bool HideToolbar
        {
            get => WebContentConfiguration.GetBoolean(GetProperty, false, nameof(HideToolbar));
            set
            {
                if (HideToolbar == value) return;
                SetProperty(nameof(HideToolbar), WebContentConfiguration.FormatBoolean(value));
                if (value && HideAddress)
                {
                    SetProperty(nameof(HideAddress), WebContentConfiguration.FormatBoolean(false));
                    OnPropertyChanged(nameof(HideAddress));
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHideAddressEnabled));
            }
        }

        public bool HideAddress
        {
            get => !HideToolbar && WebContentConfiguration.GetBoolean(GetProperty, false, nameof(HideAddress));
            set
            {
                var normalizedValue = HideToolbar ? false : value;
                if (HideAddress == normalizedValue) return;
                SetProperty(nameof(HideAddress), WebContentConfiguration.FormatBoolean(normalizedValue));
                OnPropertyChanged();
            }
        }

        public double ZoomLevel
        {
            get => WebContentConfiguration.GetZoomLevel(GetProperty, WebContentConfiguration.DefaultZoomLevel, nameof(ZoomLevel));
            set
            {
                var normalizedValue = WebContentConfiguration.ClampZoomLevel(value);
                if (Math.Abs(ZoomLevel - normalizedValue) < double.Epsilon) return;
                SetProperty(nameof(ZoomLevel), WebContentConfiguration.FormatZoomLevel(normalizedValue));
                OnPropertyChanged();
            }
        }

        public string LastAddress
        {
            get => WebContentConfiguration.NormalizeOptionalAddress(
                WebContentConfiguration.GetString(GetProperty, string.Empty, nameof(LastAddress)));
            set
            {
                var normalizedAddress = WebContentConfiguration.NormalizeOptionalAddress(value);
                if (string.Equals(LastAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase)) return;
                SetProperty(nameof(LastAddress), normalizedAddress);
                OnPropertyChanged();
            }
        }

        public bool ClearBrowsingDataOnExit
        {
            get => WebContentConfiguration.GetBoolean(GetProperty, false, nameof(ClearBrowsingDataOnExit));
            set
            {
                if (ClearBrowsingDataOnExit == value) return;
                SetProperty(nameof(ClearBrowsingDataOnExit), WebContentConfiguration.FormatBoolean(value));
                OnPropertyChanged();
            }
        }

        public bool AllowBackgroundBrowser
        {
            get => WebContentConfiguration.GetBoolean(GetProperty, true, nameof(AllowBackgroundBrowser));
            set
            {
                if (AllowBackgroundBrowser == value) return;
                SetProperty(nameof(AllowBackgroundBrowser), WebContentConfiguration.FormatBoolean(value));
                OnPropertyChanged();
            }
        }

        public bool AllowAudioNotifications
        {
            get => WebContentConfiguration.GetBoolean(GetProperty, true, nameof(AllowAudioNotifications));
            set
            {
                if (AllowAudioNotifications == value) return;
                SetProperty(nameof(AllowAudioNotifications), WebContentConfiguration.FormatBoolean(value));
                OnPropertyChanged();
            }
        }

        public bool MuteAudio
        {
            get => WebContentConfiguration.GetBoolean(GetProperty, false, nameof(MuteAudio));
            set
            {
                if (MuteAudio == value) return;
                SetProperty(nameof(MuteAudio), WebContentConfiguration.FormatBoolean(value));
                OnPropertyChanged();
            }
        }

        public bool IsolateBrowserInstances
        {
            get => WebContentConfiguration.GetBoolean(GetProperty, false, nameof(IsolateBrowserInstances));
            set
            {
                if (IsolateBrowserInstances == value) return;
                SetProperty(nameof(IsolateBrowserInstances), WebContentConfiguration.FormatBoolean(value));
                OnPropertyChanged();
            }
        }

        public bool EnableDevTools
        {
            get => WebContentConfiguration.GetBoolean(GetProperty, false, nameof(EnableDevTools));
            set
            {
                if (EnableDevTools == value) return;
                SetProperty(nameof(EnableDevTools), WebContentConfiguration.FormatBoolean(value));
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Ticket #52: admin-owned proxy state for this workspace. Read-only on the client: the
        /// Smart Client user can see whether the browser routes through the VMS server and which
        /// targets are allowlisted, but only the Management Client admin may change it.
        /// </summary>
        public string ProxyLabel => Strings.ProxyLabel;

        public bool ProxyEnabled => WebContentConfiguration.GetBoolean(GetProperty, false, nameof(ProxyEnabled));

        public string ProxyDisplay
        {
            get
            {
                if (!ProxyEnabled)
                {
                    return Strings.ProxyDisplayDisabled;
                }

                var rawTargets = WebContentConfiguration.GetString(GetProperty, string.Empty, "ProxyTargets");
                var targets = new List<string>();
                foreach (var target in (rawTargets ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = target.Trim();
                    if (trimmed.Length > 0)
                    {
                        targets.Add(trimmed);
                    }
                }

                return targets.Count == 0
                    ? Strings.ProxyDisplayEnabledNoTargets
                    : Strings.ProxyDisplayEnabledPrefix + " " + string.Join(", ", targets);
            }
        }

        public Guid InstanceId
        {
            get => WebContentConfiguration.GetGuid(GetProperty, Guid.Empty, nameof(InstanceId));
            set
            {
                if (InstanceId == value) return;
                SetProperty(nameof(InstanceId), value.ToString());
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Fingerprint of the admin workspace definition that seeded this view item (ticket #9).
        /// Empty for standalone (manually placed) view items, which disables session URL tracking.
        /// </summary>
        public string DefinitionFingerprint =>
            WebContentConfiguration.GetString(GetProperty, string.Empty, WorkspaceViewItemSync.FingerprintKey);

        /// <summary>
        /// Updates the session-scoped last-visited URL for this workspace tab. No-op for
        /// standalone items (no definition fingerprint).
        /// </summary>
        public void RecordSessionAddress(string address)
        {
            var fingerprint = DefinitionFingerprint;
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                return;
            }

            WorkspaceSessionState.Record(InstanceId, fingerprint, address);
        }

        /// <summary>
        /// Address a fresh browser for this tab should restore to: this session's recorded URL,
        /// else the persisted LastAddress (when RememberLastAddress is on), else the home URL.
        /// </summary>
        public string ResolveRestoreAddress()
        {
            var fingerprint = DefinitionFingerprint;
            var recorded = string.IsNullOrWhiteSpace(fingerprint)
                ? null
                : WorkspaceSessionState.TryGetRecordedUrl(InstanceId, fingerprint);
            return WorkspaceSessionState.ResolveRestoreAddress(recorded, LastAddress, RememberLastAddress, HomeAddress);
        }

        /// <summary>
        /// Clears the cross-session LastAddress so an admin definition update restores the new
        /// home URL (ticket #9). Session state is cleared separately via WorkspaceSessionState.Reset.
        /// Returns true when a value was actually cleared.
        /// </summary>
        public bool ResetPersistedLastAddress()
        {
            if (string.IsNullOrEmpty(LastAddress))
            {
                return false;
            }

            SetProperty(nameof(LastAddress), string.Empty);
            OnPropertyChanged();
            return true;
        }

        private WebContentViewItemManager _viewItemManager;
        private bool _disposed;

        public WebContentSettings()
        {
            // Empty constructor provided for WPF designer support
        }

        public WebContentSettings(WebContentViewItemManager viewItemManager)
        {
            _viewItemManager = viewItemManager;
            if (InstanceId == Guid.Empty) InstanceId = Guid.NewGuid();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        private string GetProperty(string key)
        {
            return _viewItemManager?.GetProperty(key);
        }

        private void SetProperty(string key, string value)
        {
            _viewItemManager?.SetProperty(key, value);
        }
    }
}
