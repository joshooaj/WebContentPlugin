using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using WebContent.Admin;

namespace WebContent.Client
{
    public class WebContentSettings : INotifyPropertyChanged
    {
        public string HomeLabel => $"{Strings.HomeLabel}:";
        public string RememberLastAddressLabel => Strings.RememberLastAddressLabel;
        public string HideToolbarLabel => Strings.HideToolbarLabel;
        public string HideAddressLabel => Strings.HideAddressLabel;
        public string ZoomLevelLabel => $"{Strings.ZoomLevelLabel}:";
        public string ClearBrowsingDataOnExitLabel => Strings.ClearBrowsingDataOnExitLabel;
        public string PurchaseLicenseLabel => Strings.PurchaseLicenseLabel;
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
            get => _viewItemManager?.GetProperty(nameof(HomeAddress)) ?? Strings.HomeAddress;
            set
            {
                if (HomeAddress == value) return;
                _viewItemManager?.SetProperty(nameof(HomeAddress), value);
                OnPropertyChanged();
            }
        }

        public bool RememberLastAddress
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(RememberLastAddress)) ?? false.ToString());
            set
            {
                if (RememberLastAddress == value) return;
                _viewItemManager?.SetProperty(nameof(RememberLastAddress), value.ToString());
                OnPropertyChanged();
            }
        }

        public bool HideToolbar
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(HideToolbar)) ?? false.ToString());
            set
            {
                if (HideToolbar == value) return;
                _viewItemManager?.SetProperty(nameof(HideToolbar), value.ToString());
                OnPropertyChanged();
            }
        }

        public bool HideAddress
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(HideAddress)) ?? false.ToString());
            set
            {
                if (HideAddress == value) return;
                _viewItemManager?.SetProperty(nameof(HideAddress), value.ToString());
                OnPropertyChanged();
            }
        }

        public double ZoomLevel
        {
            get => double.Parse(_viewItemManager?.GetProperty(nameof(ZoomLevel)) ?? "100");
            set
            {
                if (ZoomLevel == value) return;
                _viewItemManager?.SetProperty(nameof(ZoomLevel), value.ToString());
                OnPropertyChanged();
            }
        }

        public string LastAddress
        {
            get => _viewItemManager?.GetProperty(nameof(LastAddress));
            set
            {
                if (LastAddress == value) return;
                _viewItemManager?.SetProperty(nameof(LastAddress), value);
                OnPropertyChanged();
            }
        }

        public bool ClearBrowsingDataOnExit
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(ClearBrowsingDataOnExit)) ?? false.ToString());
            set
            {
                if (ClearBrowsingDataOnExit == value) return;
                _viewItemManager?.SetProperty(nameof(ClearBrowsingDataOnExit), value.ToString());
                OnPropertyChanged();
            }
        }

        public bool AllowBackgroundBrowser
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(AllowBackgroundBrowser)) ?? true.ToString());
            set
            {
                if (AllowBackgroundBrowser == value) return;
                _viewItemManager?.SetProperty(nameof(AllowBackgroundBrowser), value.ToString());
                OnPropertyChanged();
            }
        }

        public bool AllowAudioNotifications
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(AllowAudioNotifications)) ?? true.ToString());
            set
            {
                if (AllowAudioNotifications == value) return;
                _viewItemManager?.SetProperty(nameof(AllowAudioNotifications), value.ToString());
                OnPropertyChanged();
            }
        }

        public bool MuteAudio
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(MuteAudio)) ?? false.ToString());
            set
            {
                if (MuteAudio == value) return;
                _viewItemManager?.SetProperty(nameof(MuteAudio), value.ToString());
                OnPropertyChanged();
            }
        }

        public bool IsolateBrowserInstances
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(IsolateBrowserInstances)) ?? false.ToString());
            set
            {
                if (IsolateBrowserInstances == value) return;
                _viewItemManager?.SetProperty(nameof(IsolateBrowserInstances), value.ToString());
                OnPropertyChanged();
            }
        }

        public bool EnableDevTools
        {
            get => bool.Parse(_viewItemManager?.GetProperty(nameof(EnableDevTools)) ?? false.ToString());
            set
            {
                if (EnableDevTools == value) return;
                _viewItemManager?.SetProperty(nameof(EnableDevTools), value.ToString());
                OnPropertyChanged();
            }
        }

        public Guid InstanceId {
            get => Guid.TryParse(_viewItemManager?.GetProperty(nameof(InstanceId)) ?? string.Empty, out var id) ? id : Guid.Empty;
            set
            {
                if (InstanceId == value) return;
                _viewItemManager?.SetProperty(nameof(InstanceId), value.ToString());
                OnPropertyChanged();
            }
        }

        public Visibility IsLicenseActivated
        {
            get => SiteLicenseHandler.IsActivated ? Visibility.Collapsed : Visibility.Visible;
        }

        private WebContentViewItemManager _viewItemManager;

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

        //protected bool SetProperty<T>(ref T field, T newValue, [CallerMemberName] string propertyName = null)
        //{
        //    if (!Equals(field, newValue))
        //    {
        //        field = newValue;
        //        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        //        return true;
        //    }

        //    return false;
        //}
    }
}
