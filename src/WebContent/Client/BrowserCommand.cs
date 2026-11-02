using System;
using System.Windows;
using System.Windows.Input;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

using VideoOS.Platform;

namespace WebContent.Client
{
    class BrowserCommand : ICommand, IDisposable
    {
        private readonly BrowserAction _action;
        private readonly WebContentViewModel _viewModel;
        private BrowserInstance _instance;
        private WebView2 _subscribedWebView;

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                CanExecuteChanged?.Invoke(this, CoreWebView2NavigationCompletedEventArgs.Empty);
            }
        }

        public event EventHandler CanExecuteChanged;

        public BrowserCommand(BrowserAction action, WebContentViewModel viewModel)
        {
            _action = action;
            _viewModel = viewModel;
        }

        public void Init()
        {
            _instance = _viewModel.Browser;
            EnvironmentManager.Instance.Log(false, nameof(BrowserCommand), $"Initializing command {_action} for browser {_instance.Id}.");
            try
            {
                AttachWebViewEvents(_instance.WebView);
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler($"{nameof(BrowserCommand)}.Init({_action})", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                DetachWebViewEvents();
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler($"{nameof(BrowserCommand)}.Dispose({_action})", ex);
            }
        }

        private void AttachWebViewEvents(WebView2 webView)
        {
            DetachWebViewEvents();
            if (webView == null)
            {
                return;
            }

            _subscribedWebView = webView;
            _subscribedWebView.ContentLoading += _browser_ContentLoading;
            _subscribedWebView.Loaded += _browser_Loaded;
            _subscribedWebView.NavigationStarting += WebView_NavigationStarting;
            _subscribedWebView.NavigationCompleted += WebView_NavigationCompleted;
        }

        private void DetachWebViewEvents()
        {
            if (_subscribedWebView == null)
            {
                return;
            }

            _subscribedWebView.ContentLoading -= _browser_ContentLoading;
            _subscribedWebView.Loaded -= _browser_Loaded;
            _subscribedWebView.NavigationStarting -= WebView_NavigationStarting;
            _subscribedWebView.NavigationCompleted -= WebView_NavigationCompleted;
            _subscribedWebView = null;
        }

        private void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            CanExecuteChanged?.Invoke(this, e);
        }

        private void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            CanExecuteChanged?.Invoke(this, e);
        }

        private void _browser_Loaded(object sender, RoutedEventArgs e)
        {
            CanExecuteChanged?.Invoke(this, e);
        }

        private void _browser_ContentLoading(object sender, CoreWebView2ContentLoadingEventArgs e)
        {
            CanExecuteChanged?.Invoke(this, e);
        }

        private void LogUninitialized(BrowserAction action)
        {
            EnvironmentManager.Instance.Log(
                true,
                nameof(BrowserCommand),
                $"Refused {action} on browser {_instance?.Id}: the view has no CoreWebView2 " +
                "(initialization failed); the WebView recreates when the view is re-selected.");
        }

        public bool CanExecute(object parameter)
        {
            if (!Enabled) return false;

            var webView = _instance?.WebView;
            // The wrapper's own CanGoBack/CanGoForward getters already answer false without a
            // CoreWebView2; pass their values anyway so the admission rule is stated once.
            return BrowserCommandAdmission.IsAvailable(
                _action,
                webView?.CoreWebView2 != null,
                webView?.CanGoBack ?? false,
                webView?.CanGoForward ?? false);
        }

        public void Execute(object parameter)
        {
            try
            {
                EnvironmentManager.Instance.Log(false, nameof(BrowserCommand), $"Executing command {_action}. Parameter: {parameter?.GetType().Name ?? "null"}.");
                switch (_action)
                {
                    case BrowserAction.NavigateBack:
                        if (_instance?.WebView?.CoreWebView2 != null) _instance.WebView.GoBack();
                        else LogUninitialized(_action);
                        break;
                    case BrowserAction.NavigateForward:
                        if (_instance?.WebView?.CoreWebView2 != null) _instance.WebView.GoForward();
                        else LogUninitialized(_action);
                        break;
                    case BrowserAction.NavigateHome:
                        if (parameter is string address && _instance != null)
                        {
                            _instance.Address = address;
                        }
                        break;
                    case BrowserAction.NavigateTo:
                        break;
                    case BrowserAction.Refresh:
                        // Reload() throws InvalidOperationException (VerifyCoreWebView2) when the
                        // init failed — the crash the live user hit. Never call it without a
                        // CoreWebView2; NeedsRecreation then recreates the WebView on next select.
                        if (_instance?.WebView?.CoreWebView2 != null) _instance.WebView.Reload();
                        else LogUninitialized(_action);
                        break;
                    case BrowserAction.Print:
                        _instance?.WebView?.CoreWebView2?.ShowPrintUI();
                        break;
                    default:
                        EnvironmentManager.Instance.Log(true, nameof(BrowserCommand), $"Unsupported BrowserAction: {_action}");
                        break;
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler($"{nameof(BrowserCommand)}.Execute({_action})", ex);
            }
        }
    }
}