using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Windows;
using System.Windows.Input;
using VideoOS.Platform;

namespace WebContent.Client
{
    class BrowserCommand : ICommand, IDisposable
    {
        private BrowserAction _action;
        private WebContentViewModel _viewModel;
        private BrowserInstance _instance;

        private bool _enabled = true;
        public bool Enabled {
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
                _instance.WebView.ContentLoading += _browser_ContentLoading;
                _instance.WebView.Loaded += _browser_Loaded;
                _instance.WebView.NavigationStarting += WebView_NavigationStarting;
                _instance.WebView.NavigationCompleted += WebView_NavigationCompleted;
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
                _instance.WebView.ContentLoading -= _browser_ContentLoading;
                _instance.WebView.Loaded -= _browser_Loaded;
                _instance.WebView.NavigationStarting -= WebView_NavigationStarting;
                _instance.WebView.NavigationCompleted -= WebView_NavigationCompleted;
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler($"{nameof(BrowserCommand)}.Dispose({_action})", ex);
            }
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

        public bool CanExecute(object parameter)
        {
            if (!Enabled) return false;

            switch (_action)
            {
                case BrowserAction.NavigateBack:
                    return _instance?.WebView.CanGoBack ?? false;
                case BrowserAction.NavigateForward:
                    return _instance?.WebView.CanGoForward ?? false;
                case BrowserAction.NavigateHome:
                    return _instance?.WebView.IsLoaded ?? false;
                case BrowserAction.NavigateTo:
                    return _instance?.WebView.IsLoaded ?? false;
                case BrowserAction.Refresh:
                    return _instance?.WebView.IsLoaded ?? false;
                case BrowserAction.Print:
                    return _instance?.WebView.IsLoaded ?? false;
                default:
                    throw new InvalidOperationException($"BrowserAction not supported: {_action}");
            }
        }

        public void Execute(object parameter)
        {
            try
            {
                EnvironmentManager.Instance.Log(false, nameof(BrowserCommand), $"Executing command {_action}. Parameter: {parameter?.GetType().Name ?? "null"}.");
                switch (_action)
                {
                    case BrowserAction.NavigateBack:
                        _instance?.WebView.GoBack();
                        break;
                    case BrowserAction.NavigateForward:
                        _instance?.WebView.GoForward();
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
                        _instance?.WebView.Reload();
                        break;
                    case BrowserAction.Print:
                        _instance?.WebView.CoreWebView2?.ShowPrintUI();
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
