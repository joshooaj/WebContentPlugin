using Microsoft.Web.WebView2.Wpf;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using VideoOS.Platform;
using VideoOS.Platform.Messaging;
using WebContent;
using WebContent.Admin;

namespace WebContent.Client
{

    public class WebContentViewModel : INotifyPropertyChanged, IDisposable
    {
        public event PropertyChangedEventHandler PropertyChanged;

        #region Properties
        public string Home
        {
            get => _viewItemManager?.Settings.HomeAddress ?? Strings.HomeAddress;
        }

        private string _address = Strings.HomeAddress;
        public string Address
        {
            get => Browser?.Address ?? Strings.HomeAddress;
        }

        public bool IsAddressEnabled {
            get => (ClientControl.Instance?.WorkSpaceState ?? WorkSpaceState.Normal) == WorkSpaceState.Normal ? true : false;
        }

        private Visibility _toolbarVisibility = Visibility.Visible;
        public Visibility ToolbarVisibility
        {
            get => _toolbarVisibility;
            set
            {
                if (value == _toolbarVisibility) return;
                _toolbarVisibility = SiteLicenseHandler.IsExpired ? Visibility.Visible : value;
                OnPropertyChanged();
            }
        }

        private Visibility _browserVisibility = Visibility.Hidden;
        public Visibility BrowserVisibility
        {
            get => _browserVisibility;
            set
            {
                if (_browserVisibility == value) return;
                if (SiteLicenseHandler.IsExpired)
                {
                    _browserVisibility = Visibility.Collapsed;
                }
                else
                {
                    _browserVisibility = value;
                }
                _browserVisibility = SiteLicenseHandler.IsExpired ? Visibility.Collapsed : value;
                OnPropertyChanged();
            }
        }

        public Visibility AddressVisibility => (_viewItemManager?.Settings.HideAddress ?? false) ? Visibility.Collapsed : Visibility.Visible;

        private Visibility _trialVisibility = Visibility.Collapsed;
        public Visibility TrialVisibility
        {
            get => _trialVisibility = SiteLicenseHandler.IsActivated ? Visibility.Collapsed : Visibility.Visible;
            set
            {
                _trialVisibility = value;
                OnPropertyChanged(nameof(TrialVisibility));
            }
        }

        private string _trialText = $"Trial expires in 30 days";

        public string TrialText
        {
            get => _trialText;
            set
            {
                _trialText = value;
                OnPropertyChanged(nameof(TrialText));
            }
        }

        public Guid Id => _viewItemManager?.Settings.InstanceId ?? Guid.NewGuid();

        
        private object _workspaceStateChangedReceiver;

        private bool _isClosed;

        private bool _isWorkspaceFloating;
        public bool IsWorkspaceFloating
        {
            get => _isWorkspaceFloating;
            set
            {
                _isWorkspaceFloating = value;
                OnPropertyChanged();
            }
        }

        private BrowserInstance _browser;
        public BrowserInstance Browser
        {
            get
            {
                if (_browser == null)
                {
                    _browser = BrowserManager.GetBrowser(
                        Id,
                        WebContentConfiguration.CreateNavigationUri(Home, Strings.HomeAddress),
                        ViewItemManager?.Settings.IsolateBrowserInstances ?? false);
                }

                return _browser;
            }
        } 

        private BrowserCommand _backCommand;
        public ICommand BackCommand
        {
            get
            {
                if (_backCommand == null)
                {
                    _backCommand = new BrowserCommand(BrowserAction.NavigateBack, this);
                }
                return _backCommand;
            }
        }

        public ICommand ForwardCommand
        {
            get
            {
                if (_forwardCommand == null)
                {
                    _forwardCommand = new BrowserCommand(BrowserAction.NavigateForward, this);
                }
                return _forwardCommand;
            }
        }
        private BrowserCommand _forwardCommand;

        public ICommand ReloadCommand
        {
            get
            {
                if (_reloadCommand == null)
                {
                    _reloadCommand = new BrowserCommand(BrowserAction.Refresh, this);
                }
                return _reloadCommand;
            }
        }
        private BrowserCommand _reloadCommand;

        public ICommand PrintCommand
        {
            get
            {
                if (_printCommand == null)
                {
                    _printCommand = new BrowserCommand(BrowserAction.Print, this);
                }
                return _printCommand;
            }
        }
        private BrowserCommand _printCommand;

        public ICommand HomeCommand
        {
            get
            {
                if (_homeCommand == null)
                {
                    _homeCommand = new BrowserCommand(BrowserAction.NavigateHome, this);
                }
                return _homeCommand;
            }
        }
        private BrowserCommand _homeCommand;

        private WebContentViewItemManager _viewItemManager;
        public WebContentViewItemManager ViewItemManager
        {
            get => _viewItemManager;
            internal set
            {
                _viewItemManager = value;
            }
        }

        public bool IsInitialized { get; set; }

        #endregion

        #region Constructors
        public WebContentViewModel()
        {
            // Empty constructor for WPF designer support
            if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
            {
                _browserVisibility = Visibility.Visible;
                //Browser = BrowserManager.GetBrowser(Guid.Empty, new Uri(Home), false);
                Browser.Initialize();
                Browser.Address = Strings.HomeAddress;
                Browser.WebView.Visibility = Visibility.Visible;
            }
        }

        public void Init()
        {
            var browserReferenceAdded = false;
            try
            {
                if (IsInitialized) return;

                BrowserManager.AddReference(Id);
                browserReferenceAdded = true;

                EnsureCommandsInitialized();
                ApplyWorkspaceState(ClientControl.Instance.WorkSpaceState);
                _viewItemManager.PropertyChangedEvent += ViewItemManager_PropertyChangedEvent;
                _viewItemManager.ClearBrowsingDataRequest += ClearBrowsingDataRequestHandler;
                _workspaceStateChangedReceiver = EnvironmentManager.Instance.RegisterReceiver(WorkspaceStateChangeHandler, new MessageIdFilter(MessageId.SmartClient.WorkSpaceStateChangedIndication));
                SiteLicenseHandler.LicenseActivated += SiteLicenseHandler_LicenseActivated;

                Browser.AllowDevTools = ViewItemManager.Settings.EnableDevTools;
                Browser.AllowAudioNotifications = ViewItemManager.Settings.AllowAudioNotifications;
                Browser.IsMuted = ViewItemManager.Settings.MuteAudio;
                Browser.PropertyChanged += Browser_PropertyChanged;
                try
                {
                    UpdateTrialState();

                    if (!Browser.IsInitialized)
                    {
                        Browser.Initialize();
                    }

                    if (ClientControl.Instance.WorkSpaceState == WorkSpaceState.Normal)
                    {
                        RestoreAddressIfNeeded();
                    }

                    if (_viewItemManager.Settings.ClearBrowsingDataOnExit)
                    {
                        WebContentViewItemPlugin.BrowsersToDispose.AddOrUpdate(Browser.Id, Browser, (id, instance) => instance);
                    }
                    OnPropertyChanged(nameof(Address));
                    OnPropertyChanged(nameof(Home));
                    _backCommand.Init();
                    _forwardCommand.Init();
                    _reloadCommand.Init();
                    _printCommand.Init();
                    _homeCommand.Init();
                    
                    // Can't set this here because the damn corewebview2 isn't available yet
                    // Browser.WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                }
                catch (Exception ex)
                {
                    EnvironmentManager.Instance.Log(true, nameof(WebContentViewModel), $"{ex.Message}\r\n{ex.StackTrace}");
                }

                IsInitialized = true;
            }
            catch (Exception ex)
            {
                SiteLicenseHandler.LicenseActivated -= SiteLicenseHandler_LicenseActivated;
                if (browserReferenceAdded)
                {
                    BrowserManager.ReleaseReference(Id);
                }

                var location = nameof(WebContentViewModel) + ".Init()";
                EnvironmentManager.Instance.Log(true, location, $"{ex.Message}\r\n{ex.StackTrace}");
                EnvironmentManager.Instance.ExceptionHandler(location, ex);
            }
        }

        private void Browser_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Address))
            {
                OnPropertyChanged(nameof(Address));
                if (_viewItemManager.Settings.RememberLastAddress)
                {
                    _viewItemManager.Settings.LastAddress = Address;
                }
            }
        }

        private object WorkspaceStateChangeHandler(Message message, FQID destination, FQID sender)
        {
            if (message.Data is WorkSpaceState state)
            {
                ApplyWorkspaceState(state);
                if (state == WorkSpaceState.Normal)
                {
                    IsWorkspaceFloating = false;
                    RestoreAddressIfNeeded();
                }
            }
            return null;
        }

        #endregion

        #region Public methods

        public void Close()
        {
            try
            {
                _isClosed = true;
                if (_workspaceStateChangedReceiver != null)
                {
                    EnvironmentManager.Instance.UnRegisterReceiver(_workspaceStateChangedReceiver);
                    _workspaceStateChangedReceiver = null;
                }

                _viewItemManager.PropertyChangedEvent -= ViewItemManager_PropertyChangedEvent;
                _viewItemManager.ClearBrowsingDataRequest -= ClearBrowsingDataRequestHandler;
                SiteLicenseHandler.LicenseActivated -= SiteLicenseHandler_LicenseActivated;
                Browser.PropertyChanged -= Browser_PropertyChanged;

                _backCommand?.Dispose();
                _forwardCommand?.Dispose();
                _reloadCommand?.Dispose();
                _printCommand?.Dispose();
                _homeCommand?.Dispose();

                var remainingReferences = BrowserManager.ReleaseReference(Id);
                if (remainingReferences == 0)
                {
                    if (BrowserManager.IsTracked(Id))
                    {
                        var allowBackground = _viewItemManager?.Settings.AllowBackgroundBrowser ?? false;
                        var clearOnExit = _viewItemManager?.Settings.ClearBrowsingDataOnExit ?? false;
                        if (!allowBackground)
                        {
                            Browser.Close(clearData: clearOnExit, disposeWebView: true);
                            BrowserManager.RemoveBrowserTracking(Id);
                            WebContentViewItemPlugin.BrowsersToDispose.TryRemove(Id, out _);
                        }
                        else
                        {
                            Browser.Close(clearData: false, disposeWebView: false);
                        }
                    }
                    // else: the browser was already reclaimed (e.g. the owning view item
                    // was removed while this control held the last reference). Nothing
                    // left to close or untrack.
                    _browser = null;
                }

                IsInitialized = false;
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(WebContentViewModel), ex);
            }
        }

        public void Dispose()
        {
            if (_viewItemManager != null)
            {
                Close();
            }
        }

        #endregion

        #region Private methods

        private void EnsureCommandsInitialized()
        {
            _ = BackCommand;
            _ = ForwardCommand;
            _ = ReloadCommand;
            _ = PrintCommand;
            _ = HomeCommand;
        }

        private void RestoreAddressIfNeeded()
        {
            var restoreAddress = GetRestoreAddress();
            if (string.IsNullOrWhiteSpace(restoreAddress))
            {
                restoreAddress = Strings.HomeAddress;
            }

            var currentAddress = Browser.Address;

            if (string.IsNullOrWhiteSpace(currentAddress) || IsSameAddress(currentAddress, Home))
            {
                Browser.Address = restoreAddress;
            }
        }

        private string GetRestoreAddress()
        {
            if (_viewItemManager?.Settings.RememberLastAddress == true && !string.IsNullOrWhiteSpace(_viewItemManager.Settings.LastAddress))
            {
                return _viewItemManager.Settings.LastAddress;
            }

            return Home;
        }

        private static bool IsSameAddress(string first, string second)
        {
            return string.Equals(NormalizeAddress(first), NormalizeAddress(second), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return string.Empty;
            }

            var trimmed = address.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.AbsoluteUri.TrimEnd('/');
            }

            if (Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out var normalizedUri))
            {
                return normalizedUri.AbsoluteUri.TrimEnd('/');
            }

            return trimmed.TrimEnd('/');
        }

        private void ApplyWorkspaceState(WorkSpaceState state)
        {
            OnPropertyChanged(nameof(IsAddressEnabled));
            switch (state)
            {
                case WorkSpaceState.Normal:
                    ToolbarVisibility = _viewItemManager.Settings.HideToolbar ? Visibility.Collapsed : Visibility.Visible;
                    BrowserVisibility = Visibility.Visible;
                    if (_backCommand != null) _backCommand.Enabled = true;
                    if (_forwardCommand != null) _forwardCommand.Enabled = true;
                    if (_homeCommand != null) _homeCommand.Enabled = true;
                    if (_printCommand != null) _printCommand.Enabled = true;
                    if (_reloadCommand != null) _reloadCommand.Enabled = true;
                    break;
                case WorkSpaceState.Setup:
                    ToolbarVisibility = Visibility.Visible;
                    BrowserVisibility = Visibility.Hidden;
                    if (_backCommand != null) _backCommand.Enabled = false;
                    if (_forwardCommand != null) _forwardCommand.Enabled = false;
                    if (_homeCommand != null) _homeCommand.Enabled = false;
                    if (_printCommand != null) _printCommand.Enabled = false;
                    if (_reloadCommand != null) _reloadCommand.Enabled = false;
                    break;
            }
        }

        private void UpdateTrialState()
        {
            var trialText = $"Trial expires on {SiteLicenseHandler.Expires.ToLocalTime().Date:d}";
            if (SiteLicenseHandler.IsExpired)
            {
                trialText = trialText.Replace("expires", "expired");
            }

            TrialText = trialText;
            OnPropertyChanged(nameof(TrialVisibility));
        }

        private void ExecuteOnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action);
        }

        private void SiteLicenseHandler_LicenseActivated(object sender, EventArgs e)
        {
            ExecuteOnUiThread(() =>
            {
                if (_isClosed)
                {
                    return;
                }

                UpdateTrialState();
                ApplyWorkspaceState(ClientControl.Instance?.WorkSpaceState ?? WorkSpaceState.Normal);
            });
        }

        private void ReloadProperties()
        {
            OnPropertyChanged(nameof(ToolbarVisibility));
            OnPropertyChanged(nameof(AddressVisibility));
            OnPropertyChanged(nameof(Home));

            Browser.ZoomFactor = _viewItemManager.Settings.ZoomLevel / 100;
            Browser.AllowAudioNotifications = ViewItemManager.Settings.AllowAudioNotifications;
            Browser.IsMuted = ViewItemManager.Settings.MuteAudio;

            if (Browser.WebView?.CoreWebView2 != null)
            {
                Browser.WebView.CoreWebView2.Settings.AreDevToolsEnabled = ViewItemManager.Settings.EnableDevTools;
            }

            if (_viewItemManager.Settings.ClearBrowsingDataOnExit)
            {
                WebContentViewItemPlugin.BrowsersToDispose.AddOrUpdate(Browser.Id, Browser, (id, instance) => instance);
            }
            else
            {
                WebContentViewItemPlugin.BrowsersToDispose.TryRemove(Browser.Id, out var instance);
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region Event handlers

        private void ClearBrowsingDataRequestHandler(object sender, EventArgs e)
        {
            Browser.ClearBrowsingData();
        }

        private void ViewItemManager_PropertyChangedEvent(object sender, EventArgs e)
        {
            ReloadProperties();
        }

        #endregion
    }
}
