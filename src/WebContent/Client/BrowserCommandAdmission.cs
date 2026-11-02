namespace WebContent.Client
{
    /// <summary>
    /// Pure admission gate for the Smart Client toolbar commands. Every one of them is a call
    /// into the live CoreWebView2 — Reload, GoBack, GoForward and ShowPrintUI all die in the
    /// WebView2 wrapper's VerifyCoreWebView2 with InvalidOperationException while the control
    /// has no CoreWebView2 (a failed EnsureCoreWebView2Async leaves the WPF control alive, its
    /// IsLoaded true, and CoreWebView2 null — the old IsLoaded gate let Refresh through and the
    /// user got an exception dialog instead of a browser). The wrapper's CanGoBack/CanGoForward
    /// getters answer false in that state, but the admission here does not rely on that.
    /// A failed init still recovers on its own: <see cref="BrowserInstance.NeedsRecreation"/>
    /// reports the state and the view's next Init recreates the WebView.
    /// </summary>
    internal static class BrowserCommandAdmission
    {
        internal static bool IsAvailable(
            BrowserAction action,
            bool coreReady,
            bool canGoBack,
            bool canGoForward)
        {
            switch (action)
            {
                case BrowserAction.NavigateBack:
                    return coreReady && canGoBack;
                case BrowserAction.NavigateForward:
                    return coreReady && canGoForward;
                case BrowserAction.NavigateHome:
                case BrowserAction.Refresh:
                case BrowserAction.Print:
                    return coreReady;
                default:
                    return false;
            }
        }
    }
}