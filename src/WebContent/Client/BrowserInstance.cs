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

        private CancellationTokenSource _cancellationTokenSource;
        private void WebView_IsDocumentPlayingAudioChanged(object sender, object e)
        {
            if (!AllowAudioNotifications) return;
            if (WebView?.CoreWebView2 == null) return;
            if (WebView.IsVisible) return;

            if (!IsMuted && WebView.CoreWebView2.IsDocumentPlayingAudio)
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                SendAudioPlayingMessage();
            }
            else
            {
                _cancellationTokenSource = new CancellationTokenSource();
                Task.Delay(TimeSpan.FromSeconds(10), _cancellationTokenSource.Token).ContinueWith(task =>
                {
                    if (task.IsCanceled) return;
                    RemoveAudioPlayingMessage();
                });
            }
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
                var uri = WebContentConfiguration.CreateNavigationUri(url, Strings.HomeAddress);
                if (WebView.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.Navigate(uri.ToString());
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
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;

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
        }

        public void RecreateWebView(Uri initialAddress, bool isolateInstances)
        {
            EnvironmentManager.Instance.Log(false, nameof(BrowserInstance), $"Recreating WebView for browser {Id}. isolateInstances={isolateInstances}, address={initialAddress}.");
            Close(clearData: false, disposeWebView: true);

            var userDataFolder = BrowserProfileIdentity.GetUserDataFolder();

            WebView = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = userDataFolder,
                    ProfileName = isolateInstances ? Id.ToString() : string.Empty,
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
