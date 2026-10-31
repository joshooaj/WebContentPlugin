namespace WebContent
{
    /// <summary>
    /// Builds the WebView2 additional-browser-arguments string that pins a proxied workspace's
    /// browser profile to the loopback forwarding listener (#54). http and https are both routed
    /// to the listener — https arrives as CONNECT and is refused there, so the browser never
    /// falls back to a direct connection — and Chromium's implicit localhost bypass is disabled
    /// so loopback targets follow the same allowlist rule.
    /// </summary>
    internal static class ProxyBrowserArguments
    {
        internal static string For(int port)
        {
            // Schemes not listed in --proxy-server fall back to DIRECT, so every scheme the
            // browser can use against a web page is pinned to the listener explicitly. The
            // arguments are space-split by WebView2, and every value here is space-free.
            return $"--proxy-server=http=http://127.0.0.1:{port};https=http://127.0.0.1:{port};ws=http://127.0.0.1:{port};wss=http://127.0.0.1:{port} " +
                   "--proxy-bypass-list=<-loopback>";
        }
    }
}
