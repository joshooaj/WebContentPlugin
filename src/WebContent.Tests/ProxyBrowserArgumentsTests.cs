using System;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #54 AC1: the proxied workspace's WebView2 environment is created with the browser
    /// proxy-server argument pointed at the loopback listener. These tests pin the argument
    /// builder: every http and https scheme is pinned to the listener (never direct) and
    /// Chromium's implicit localhost bypass is turned off so even loopback targets follow the rule.
    /// </summary>
    public class ProxyBrowserArgumentsTests
    {
        [Fact]
        public void PinsHttpAndHttpsTrafficToTheLoopbackListener()
        {
            var args = ProxyBrowserArguments.For(49312);

            Assert.Contains("--proxy-server=http=http://127.0.0.1:49312;https=http://127.0.0.1:49312", args);
        }

        [Fact]
        public void RemovesChromiumImplicitLoopbackBypass()
        {
            // By default Chromium bypasses proxies for localhost URLs; "<-loopback" re-enlists
            // them so an allowlisted device on loopback still routes through the VMS fetcher.
            // Values carry no spaces, so no quoting layer is involved.
            var args = ProxyBrowserArguments.For(49312);

            Assert.Contains("--proxy-bypass-list=<-loopback>", args);
        }
    }
}
