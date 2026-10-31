using Microsoft.Web.WebView2.Wpf;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VideoOS.Platform;
using VideoOS.Platform.Messaging;
using WebContent;

namespace WebContent.Client
{
    public class BrowserInstance : IDisposable, INotifyPropertyChanged
    {
        public Guid Id { get; }
        
        public WebView2 WebView { get; set; }

        private readonly object _sync = new object();
        private object _audioNotificationClickReceiver;
        private bool _requiresRecreation;

        public bool IsInitialized { get; set; }

        public bool NeedsRecreation => _requiresRecreation || WebView == null || (IsInitialized && WebView?.CoreWebView2 == null);

        public string Address
        {
            get
            {
                try
                {
                    return WebView?.CoreWebView2?.Source?.ToString() ?? WebView?.Source?.ToString() ?? Strings.HomeAddress;
                }
                catch (ObjectDisposedException)
                {
                    return WebView?.Source?.ToString() ?? Strings.HomeAddress;
                }
            }
            set
            {
                NavigateTo(value);
                //OnPropertyChanged();
            }
        }

        public double ZoomFactor
        {
            get => WebView?.ZoomFactor ?? 1.0;
            set
            {
                if (WebView != null)
                {
                    WebView.ZoomFactor = value;
                }
            }
        }

        private bool _clearBrowsingDataOnExit;
        

        public bool ClearBrowsingDataOnExit {
            get => _clearBrowsingDataOnExit;
            set
            {
                _clearBrowsingDataOnExit = value;
                OnPropertyChanged();
            }
        }

        private bool _allowDevTools;
        public bool AllowDevTools
        {
            get => _allowDevTools;
            set
            {
                _allowDevTools = value;
                if (WebView?.CoreWebView2 != null) {
                    WebView.CoreWebView2.Settings.AreDevToolsEnabled = value;
                }
                OnPropertyChanged();
            }
        }

        private bool _allowAudioNotifications = true;
        public bool AllowAudioNotifications
        {
            get => _allowAudioNotifications;
            set
            {
                _allowAudioNotifications = value;
                OnPropertyChanged();
            }
        }

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted == value) return;
                _isMuted = value;
                if (WebView?.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.IsMuted = value;
                }

                OnPropertyChanged();
            }
        }

        public BrowserInstance()
        {
            Id = Guid.Empty;
        }

        public BrowserInstance(WebView2 browser, Guid id)
        {
            WebView = browser ?? throw new ArgumentNullException(nameof(browser));
            Id = id;
        }

        /// <summary>
        /// Ticket #54: loopback forwarding proxy bound to this browser profile. The arguments are
        /// re-applied on every WebView recreation, the listener survives recreation, and it stops
        /// when the browser instance is disposed.
        /// </summary>
        internal LoopbackProxyListener ProxyListener { get; set; }

        internal string ProxyBrowserArguments { get; set; }

        /// <summary>
        /// Distinct WebView2 user-data folder for a proxied profile (its own browser process, so
        /// the --proxy-server argument cannot collide with shared-folder WebViews); null when the
        /// browser routes direct.
        /// </summary>
        internal string ProxyUserDataFolder { get; set; }

        public void Initialize()
        {
            lock (_sync)
            {
                if (IsInitialized || _requiresRecreation) return;

                if (WebView == null)
                {
                    _requiresRecreation = true;
                    EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), $"Cannot initialize browser {Id} because the WebView is unavailable.");
                    return;
                }

                try
                {
                    WebView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;
                    WebView.SourceChanged += SourceChangedEventHandler;

                    if (_audioNotificationClickReceiver == null)
                    {
                        _audioNotificationClickReceiver = EnvironmentManager.Instance.RegisterReceiver(Notification_Clicked, new MessageIdFilter(MessageId.SmartClient.SmartClientMessageButtonClickedIndication));
                    }

                    IsInitialized = true;
                    _ = WebView.EnsureCoreWebView2Async();
                }
                catch (Exception ex)
                {
                    IsInitialized = false;
                    EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), $"Call to EnsureCoreWebView2Async failed. {ex.Message}\r\n{ex.StackTrace}");
                }
            }
        }

        private void WebView_CoreWebView2InitializationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!IsInitialized)
            {
                return;
            }

            var webView = WebView;
            if (webView == null)
            {
                return;
            }

            webView.CoreWebView2InitializationCompleted -= WebView_CoreWebView2InitializationCompleted;
            if (!e.IsSuccess)
            {
                EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), $"CoreWebView2 initialization failed. InitializationException: {e.InitializationException?.Message}");
                return;
            }
            if (webView.CoreWebView2 == null)
            {
                EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), "CoreWebView2 is null after successful initialization.");
                return;
            }
            webView.CoreWebView2.Settings.AreDevToolsEnabled = AllowDevTools;
            webView.CoreWebView2.IsDocumentPlayingAudioChanged -= WebView_IsDocumentPlayingAudioChanged;
            webView.CoreWebView2.IsDocumentPlayingAudioChanged += WebView_IsDocumentPlayingAudioChanged;
            webView.CoreWebView2.IsMuted = _isMuted;
        }

        private readonly CallbackOwnership _callbackOwnership = new CallbackOwnership();
        private CancellationTokenSource _cancellationTokenSource;

        private void WebView_IsDocumentPlayingAudioChanged(object sender, object e)
        {
            if (!AllowAudioNotifications) return;
            if (WebView?.CoreWebView2 == null) return;
            if (!IsInitialized) return;
            if (WebView.IsVisible) return;

            if (!IsMuted && WebView.CoreWebView2.IsDocumentPlayingAudio)
            {
                CancelPendingAudioRemoval();
                SendAudioPlayingMessage();
            }
            else
            {
                ScheduleAudioRemoval();
            }
        }

        private void CancelPendingAudioRemoval()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        private void ScheduleAudioRemoval()
        {
            // Capture ownership so a removal scheduled before the browser is closed is
            // ignored after the browser's lifecycle has ended (ticket #15).
            var ownershipToken = _callbackOwnership.Capture();
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ContinueWith(task =>
            {
                if (task.IsCanceled) return;
                if (!_callbackOwnership.IsCurrent(ownershipToken)) return;
                if (WebView == null || !IsInitialized) return;
                RemoveAudioPlayingMessage();
            });
        }

        private void SendAudioPlayingMessage()
        {
            EnvironmentManager.Instance.SendMessage(new Message(MessageId.SmartClient.SmartClientMessageCommand, new SmartClientMessageData
            {
                MessageId = Id.ToString(),
                IsClosable = true,
                Message = $"Audio source: {WebView?.CoreWebView2?.DocumentTitle}",
                MessageType = SmartClientMessageDataType.None,
                ButtonText = IsMuted ? "Unmute" : "Mute"
            }));
        }

        private void RemoveAudioPlayingMessage()
        {
            EnvironmentManager.Instance.SendMessage(new Message(MessageId.SmartClient.SmartClientMessageCommand, new SmartClientMessageData
            {
                MessageId = Id.ToString(),
                IsClosable = true,
                Message = string.Empty,
                MessageType = SmartClientMessageDataType.None
            }));
        }

        private object Notification_Clicked(Message message, FQID destination, FQID sender)
        {
            if (WebView?.CoreWebView2 == null) return null;
            if (!(message.Data is string messageId) || messageId != Id.ToString()) return null;

            IsMuted = !IsMuted;
            SendAudioPlayingMessage();
            return null;
        }

        private void SourceChangedEventHandler(object sender, Microsoft.Web.WebView2.Core.CoreWebView2SourceChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Address));
        }

        private void NavigateTo(string url)
        {
            if (WebView == null) return;
            try
            {
                if (!WebContentConfiguration.TryCreateNavigationUri(url, out var uri))
                {
                    EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), $"Refused to navigate browser {Id}: invalid address '{url}'.");
                    return;
                }

                if (WebView.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.Navigate(uri.AbsoluteUri);
                    WebView.Focus();
                }
                else
                {
                    WebView.Source = uri;
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(NavigateTo), ex);
            }
        }

        public void ClearBrowsingData()
        {
            EnvironmentManager.Instance.Log(false, nameof(ClearBrowsingData), $"Clearing browser data for browser with id {Id}.");
            if (WebView?.CoreWebView2?.Profile != null)
            {
                _ = WebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            }
        }

        public void Close(bool clearData, bool disposeWebView = true)
        {
            EnvironmentManager.Instance.Log(false, nameof(BrowserInstance), $"Closing browser {Id}. clearData={clearData}, disposeWebView={disposeWebView}.");
            lock (_sync)
            {
                try
                {
                    _callbackOwnership.End();
                    CancelPendingAudioRemoval();

                    if (_audioNotificationClickReceiver != null)
                    {
                        EnvironmentManager.Instance.UnRegisterReceiver(_audioNotificationClickReceiver);
                        _audioNotificationClickReceiver = null;
                    }

                    RemoveAudioPlayingMessage();

                    if (WebView != null)
                    {
                        WebView.CoreWebView2InitializationCompleted -= WebView_CoreWebView2InitializationCompleted;
                        WebView.SourceChanged -= SourceChangedEventHandler;
                        if (WebView.CoreWebView2 != null)
                        {
                            WebView.CoreWebView2.IsDocumentPlayingAudioChanged -= WebView_IsDocumentPlayingAudioChanged;
                        }
                    }

                    if (clearData)
                    {
                        ClearBrowsingData();
                    }

                    if (disposeWebView)
                    {
                        WebView?.Dispose();
                        _requiresRecreation = true;
                    }
                    else
                    {
                        _requiresRecreation = false;
                    }

                    IsInitialized = false;
                }
                catch (Exception ex)
                {
                    EnvironmentManager.Instance.ExceptionHandler(nameof(Close), ex);
                }
            }
        }

        public void Dispose()
        {
            EnvironmentManager.Instance.Log(false, nameof(BrowserInstance), $"Disposing browser {Id}. ClearBrowsingDataOnExit={ClearBrowsingDataOnExit}.");
            Close(ClearBrowsingDataOnExit);

            var proxyListener = ProxyListener;
            ProxyListener = null;
            proxyListener?.Stop();
        }

        public void RecreateWebView(Uri initialAddress, bool isolateInstances)
        {
            EnvironmentManager.Instance.Log(false, nameof(BrowserInstance), $"Recreating WebView for browser {Id}. isolateInstances={isolateInstances}, address={initialAddress}.");
            Close(clearData: false, disposeWebView: true);

            var userDataFolder = ProxyUserDataFolder ?? BrowserProfileIdentity.GetUserDataFolder();

            // A proxied browser keeps isolation even if the setting says otherwise: its
            // --proxy-server argument belongs to one profile only, and sharing the default
            // profile would let the proxy (or its absence) leak across workspaces (#54).
            var isolated = BrowserManager.ShouldIsolateProfile(isolateInstances, ProxyBrowserArguments != null);

            WebView = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = userDataFolder,
                    ProfileName = isolated ? Id.ToString() : string.Empty,
                    AdditionalBrowserArguments = ProxyBrowserArguments,
                },
                Source = initialAddress ?? WebContentConfiguration.CreateNavigationUri(Strings.HomeAddress),
            };

            _requiresRecreation = false;
            IsInitialized = false;
            Initialize();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        internal void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
