using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using VideoOS.Platform;
using VideoOS.Platform.Messaging;
using WebContent.Admin;

namespace WebContent.Client
{

    public class WebContentViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly static Dictionary<Guid, WebView2> Browsers = new Dictionary<Guid, WebView2>();
        
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

        public BrowserInstance Browser
        {
            get => BrowserManager.GetBrowser(Id, new Uri(Home), ViewItemManager?.Settings.IsolateBrowserInstances ?? false);
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
            try
            {
                if (IsInitialized) return;

                ApplyWorkspaceState(ClientControl.Instance.WorkSpaceState);
                _viewItemManager.PropertyChangedEvent += ViewItemManager_PropertyChangedEvent;
                _viewItemManager.ClearBrowsingDataRequest += ClearBrowsingDataRequestHandler;
                _workspaceStateChangedReceiver = EnvironmentManager.Instance.RegisterReceiver(WorkspaceStateChangeHandler, new MessageIdFilter(MessageId.SmartClient.WorkSpaceStateChangedIndication));

                var homeAddress = _viewItemManager.Settings.RememberLastAddress && !string.IsNullOrWhiteSpace(_viewItemManager.Settings.LastAddress) ? _viewItemManager.Settings.LastAddress : Home;
                Browser.AllowDevTools = ViewItemManager.Settings.EnableDevTools;
                Browser.AllowAudioNotifications = ViewItemManager.Settings.AllowAudioNotifications;
                Browser.IsMuted = ViewItemManager.Settings.MuteAudio;
                Browser.PropertyChanged += Browser_PropertyChanged;
                try
                {
                    TrialText = $"Trial expires on {SiteLicenseHandler.Expires.ToLocalTime().Date:d}";
                    if (SiteLicenseHandler.IsExpired)
                    {
                        TrialText = TrialText.Replace("expires", "expired");
                    }

                    if (!Browser.IsInitialized)
                    {
                        Browser.Initialize();
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
                    Application.Current?.Dispatcher.InvokeAsync(async () => await Browser.WebView.EnsureCoreWebView2Async()).Wait();
                    
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
            if (message.Data is WorkSpaceState state) {
                if (state == WorkSpaceState.Normal)
                {
                    // Transitioned from setup to Normal - navigate to home address
                    Browser.Address = Home;
                }
                ApplyWorkspaceState(state);
            };
            return null;
        }

        #endregion

        #region Public methods

        public void Close()
        {
            try
            {
                EnvironmentManager.Instance.UnRegisterReceiver(_workspaceStateChangedReceiver);
                _viewItemManager.PropertyChangedEvent -= ViewItemManager_PropertyChangedEvent;
                _backCommand.Dispose();
                _forwardCommand.Dispose();
                _reloadCommand.Dispose();
                _printCommand.Dispose();
                _homeCommand.Dispose();

                if (!_viewItemManager?.Settings.AllowBackgroundBrowser ?? false)
                {
                    BrowserManager.DestroyBrowser(Browser);
                }
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

        private void ApplyWorkspaceState(WorkSpaceState state)
        {
            OnPropertyChanged(nameof(IsAddressEnabled));
            switch (state)
            {
                case WorkSpaceState.Normal:
                    ToolbarVisibility = _viewItemManager.Settings.HideToolbar ? Visibility.Collapsed : Visibility.Visible;
                    BrowserVisibility = Visibility.Visible;
                    _backCommand.Enabled = true;
                    _forwardCommand.Enabled = true;
                    _homeCommand.Enabled = true;
                    _printCommand.Enabled = true;
                    _reloadCommand.Enabled = true;
                    break;
                case WorkSpaceState.Setup:
                    ToolbarVisibility = Visibility.Visible;
                    BrowserVisibility = Visibility.Hidden;
                    _backCommand.Enabled = false;
                    _forwardCommand.Enabled = false;
                    _homeCommand.Enabled = false;
                    _printCommand.Enabled = false;
                    _reloadCommand.Enabled = false;
                    break;
            }
        }

        private void ReloadProperties()
        {
            OnPropertyChanged(nameof(ToolbarVisibility));
            OnPropertyChanged(nameof(AddressVisibility));
            OnPropertyChanged(nameof(Home));

            Browser.ZoomFactor = _viewItemManager.Settings.ZoomLevel / 100;
            Browser.AllowAudioNotifications = ViewItemManager.Settings.AllowAudioNotifications;
            Browser.IsMuted = ViewItemManager.Settings.MuteAudio;

            if (Browser.WebView.CoreWebView2 != null)
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
