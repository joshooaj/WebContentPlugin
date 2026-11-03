using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Microsoft.Web.WebView2.Wpf;

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


        public bool ClearBrowsingDataOnExit
        {
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
                if (WebView?.CoreWebView2 != null)
                {
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

        /// <summary>
        /// Ticket #83: the blocked-resource tally for this browser view instance. Owned by the
        /// browser (not the listener) so it survives a proxy rewire, fed by whatever listener
        /// currently fronts the profile, and reset on every top-level navigation — the view's
        /// setup-mode indicator ("N resources blocked") reads it directly, no ES roundtrip.
        /// </summary>
        internal ProxyBlockTally ProxyBlocks { get; set; }

        /// <summary>
        /// Ticket #72: the deployment-CA trust posture for this browser's proxied tunnels
        /// (null for an unproxied browser). Browser-owned so it survives a listener rewire;
        /// every mint through the current listener republishes it, and the Smart Client view
        /// renders an untrusted posture as its banner.
        /// </summary>
        internal ProxyCaTrustState ProxyCaTrust { get; set; }

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

            // #83: a top-level navigation starts a fresh page load - the blocked-resource
            // tally resets here, before the document request reaches the listener, exactly
            // like the browser's own console clearing on navigation.
            webView.CoreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
            webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;

            // #88: a proxied browser also gets the https refusal gate. Chromium transports
            // https targets to the loopback listener ONLY as CONNECT, and Chromium drops the
            // body of a non-2xx CONNECT answer - the proxy's own 403 page can never reach the
            // screen for those. WebResourceRequested sits above the proxy dispatch inside the
            // browser stack, so the view can answer an off-allowlist https refusal there with
            // the same friendly page, committed at the requested URL, before any CONNECT.
            // (Plain http keeps riding the proxy's absolute-form 403; ws/wss upgrades are not
            // surfaced by this event and keep the CONNECT posture - documented asymmetry.)
            webView.CoreWebView2.WebResourceRequested -= CoreWebView2_WebResourceRequested;
            if (ProxyListener != null)
            {
                webView.CoreWebView2.AddWebResourceRequestedFilter("https://*/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
            }
        }

        private void CoreWebView2_NavigationStarting(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
        {
            try
            {
                ProxyBlocks?.Reset();
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), $"Blocked-resource reset failed for browser {Id}: {ex.Message}");
            }

            // #72: a top-level navigation is also the cheap re-check point for a deployment CA
            // this process last saw as untrusted (the fix helper, an admin's Import-Certificate,
            // or a GPO push can land trust mid-session). When the store now holds it, drop the
            // cached leaves so the very next https tunnel re-mints and re-publishes the trusted
            // posture - the proxied page then renders without restarting Smart Client.
            RecheckProxyCaTrust();
        }

        /// <summary>
        /// #72: re-reads the root stores for a posture this browser previously saw as untrusted
        /// and, when the CA has become trusted, invalidates the listener's leaf cache so the next
        /// mint re-publishes the flipped posture. Never writes a store; a null/unproxied or
        /// already-trusted posture costs nothing.
        /// </summary>
        private void RecheckProxyCaTrust()
        {
            var trust = ProxyCaTrust;
            if (trust == null || !trust.IsUntrustedConfirmed)
            {
                return;
            }

            var thumbprint = trust.AuthorityThumbprint;
            if (string.IsNullOrEmpty(thumbprint) || ProxyListener == null)
            {
                return;
            }

            try
            {
                if (!ProxyLoopbackTrust.IsRootTrusted(thumbprint, message => EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), message)))
                {
                    return;
                }

                EnvironmentManager.Instance.Log(false, nameof(BrowserInstance),
                    $"WebContent proxy: deployment CA {thumbprint} newly present in this machine's trusted roots; invalidating cached leaves for browser {Id} so the next mint re-publishes the trusted posture.");
                ProxyListener.InvalidateLeafCache();
                trust.Publish(thumbprint, trust.AuthorityDerBase64, true);
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, nameof(BrowserInstance), $"Deployment-CA trust re-check failed for browser {Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// #88: the view-layer https refusal. The filter is registered only for proxied browsers;
        /// allowlisted origins fall through untouched (no response set = the request proceeds to
        /// the loopback listener exactly as before), and every refusal decision runs the ONE
        /// shared ProxyPolicy matcher over the listener's live entry set - the view cannot
        /// disagree with the listener, and nothing here can widen what loads. The block rides the
        /// #83 tally (which forwards to the #69 ES reporter) exactly like the listener's own 403.
        /// </summary>
        private void CoreWebView2_WebResourceRequested(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var listener = ProxyListener;
                var core = WebView?.CoreWebView2;
                if (listener == null || core == null || e.Request == null)
                {
                    return;
                }

                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
                {
                    return;
                }

                if (!ProxyViewRefusal.TryRefuseHttpsRequest(
                        listener.CanonicalTargets,
                        listener.VmsFloorHosts,
                        uri,
                        e.Request.Method,
                        out var rejectReason,
                        out var refusalHtml))
                {
                    return;
                }

                ProxyBlocks?.Report(uri, ProxyBlockReasons.NotAllowed);
                EnvironmentManager.Instance.Log(true, nameof(BrowserInstance),
                    $"WebContent proxy: view-level https refusal for browser {Id}: {e.Request.Method} '{e.Request.Uri}' - {rejectReason} No CONNECT was opened and the browser is never taken direct.");

                var content = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(refusalHtml));
                e.Response = core.Environment.CreateWebResourceResponse(
                    content,
                    403,
                    "Forbidden",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            }
            catch (Exception ex)
            {
                // A failing gate never becomes a browser fault: the request simply proceeds and
                // the loopback listener's own decision table answers it (fail-closed unchanged).
                EnvironmentManager.Instance.Log(true, nameof(BrowserInstance),
                    $"View-level https refusal gate failed for browser {Id}: {ex.Message}");
            }
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
                            WebView.CoreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
                            WebView.CoreWebView2.WebResourceRequested -= CoreWebView2_WebResourceRequested;
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
