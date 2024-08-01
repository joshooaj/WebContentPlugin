using Microsoft.Web.WebView2.Wpf;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VideoOS.Platform;
using VideoOS.Platform.Messaging;

namespace WebContent.Client
{
    public class BrowserInstance : IDisposable, INotifyPropertyChanged
    {
        public Guid Id { get; }
        
        public WebView2 WebView { get; }

        private object _audioNotificationClickReceiver;

        public bool IsInitialized { get; set; }

        public string Address
        {
            get => WebView?.Source?.ToString() ?? Strings.HomeAddress;
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
                WebView.ZoomFactor = value;
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
                if (WebView.CoreWebView2 != null) {
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

        public bool IsMuted
        {
            get => WebView.CoreWebView2?.IsMuted ?? false;
            set
            {
                if (WebView.CoreWebView2 == null) return;
                WebView.CoreWebView2.IsMuted = value;
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
            if (IsInitialized) return;
            try
            {
                WebView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;
                Application.Current?.Dispatcher.InvokeAsync(async () => await WebView.EnsureCoreWebView2Async()).Wait();
                WebView.SourceChanged += SourceChangedEventHandler;
                _audioNotificationClickReceiver = EnvironmentManager.Instance.RegisterReceiver(Notification_Clicked, new MessageIdFilter(MessageId.SmartClient.SmartClientMessageButtonClickedIndication));
                IsInitialized = true;
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), $"Call to EnsureCoreWebView2Async failed. {ex.Message}\r\n{ex.StackTrace}");
            }
        }

        private void WebView_CoreWebView2InitializationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = AllowDevTools;
            WebView.CoreWebView2.IsDocumentPlayingAudioChanged += WebView_IsDocumentPlayingAudioChanged;
            WebView.CoreWebView2.IsMuted = IsMuted;
        }

        private CancellationTokenSource _cancellationTokenSource;
        private void WebView_IsDocumentPlayingAudioChanged(object sender, object e)
        {
            if (!AllowAudioNotifications) return;
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
                Message = $"Audio source: {WebView.CoreWebView2.DocumentTitle}",
                MessageType = SmartClientMessageDataType.None,
                ButtonText = WebView.CoreWebView2.IsMuted ? "Unmute" : "Mute"
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
            if (WebView.CoreWebView2 == null) return null;
            if (!(message.Data is string messageId) || messageId != Id.ToString()) return null;

            WebView.CoreWebView2.IsMuted = !WebView.CoreWebView2.IsMuted;
            SendAudioPlayingMessage();
            return null;
        }

        private void SourceChangedEventHandler(object sender, Microsoft.Web.WebView2.Core.CoreWebView2SourceChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Address));
        }

        private void NavigateTo(string url)
        {
            if (!WebView?.IsInitialized ?? false) return;
            try
            {
                var uri = new Uri(url, UriKind.RelativeOrAbsolute);
                var uribuilder = new UriBuilder(url);
                if (!uri.IsAbsoluteUri)
                {
                    uribuilder.Scheme = "https";
                    uribuilder.Port = 443;
                }
                WebView?.CoreWebView2.Navigate(uribuilder.Uri.ToString());
                WebView?.Focus();
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(NavigateTo), ex);
            }
        }

        public void ClearBrowsingData()
        {
            EnvironmentManager.Instance.Log(false, nameof(ClearBrowsingData), $"Clearing browser data for browser with id {Id}.");
            Application.Current?.Dispatcher.InvokeAsync(async () => await WebView.CoreWebView2?.Profile.ClearBrowsingDataAsync()).Wait();
        }

        public void Close(bool clearData)
        {            
            EnvironmentManager.Instance.UnRegisterReceiver(_audioNotificationClickReceiver);
            RemoveAudioPlayingMessage();

            WebView.CoreWebView2InitializationCompleted -= WebView_CoreWebView2InitializationCompleted;
            WebView.SourceChanged -= SourceChangedEventHandler;
            WebView.CoreWebView2.IsDocumentPlayingAudioChanged -= WebView_IsDocumentPlayingAudioChanged;

            if (clearData) ClearBrowsingData();
            WebView?.Dispose();
        }

        public void Dispose()
        {
            Close(ClearBrowsingDataOnExit);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        internal void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
