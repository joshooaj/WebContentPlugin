using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #53: the Event Server fetcher performs the HTTP(S) GET from the server network and
    /// enforces connect timeout, read timeout, and a maximum response size; the status line and
    /// response headers come back with the body. These pins run the real HttpWebRequest transport
    /// against a local test page.
    /// </summary>
    public class ProxyFetchTransportTests : IDisposable
    {
        private readonly TestHttpServer _server = new TestHttpServer();

        [Fact]
        public async Task FetchingTheTestPageCapturesStatusHeadersAndBody()
        {
            _server.Handler = async context =>
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/html";
                context.Response.Headers["X-Probe"] = "present";
                var page = Encoding.UTF8.GetBytes("<html><body>proxy test page</body></html>");
                await context.Response.OutputStream.WriteAsync(page, 0, page.Length);
                context.Response.Close();
            };

            var fetcher = new ProxyHttpFetcher();
            var result = await fetcher.FetchAsync(new Uri(_server.UrlFor("/index.html")), ProxyFetchLimits.Default());

            Assert.True(result.Success);
            Assert.Equal(200, result.StatusCode);
            Assert.Equal(Encoding.UTF8.GetBytes("<html><body>proxy test page</body></html>"), result.Body);
            Assert.Equal("<html><body>proxy test page</body></html>".Length, result.BodyLength);

            var headers = result.Headers ?? new List<ProxyFetchHeader>();
            Assert.Contains(headers, h => string.Equals(h.Name, "X-Probe", StringComparison.OrdinalIgnoreCase) && h.Value == "present");
            Assert.Contains(headers, h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase) && h.Value.StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task BodyOverTheMaximumResponseSizeIsRefusedWithTooLarge()
        {
            _server.Handler = async context =>
            {
                var page = new byte[5000];
                await context.Response.OutputStream.WriteAsync(page, 0, page.Length);
                context.Response.Close();
            };

            var limits = ProxyFetchLimits.Default();
            limits.MaxResponseBytes = 1000;

            var result = await new ProxyHttpFetcher().FetchAsync(new Uri(_server.UrlFor("/large")), limits);

            Assert.False(result.Success);
            Assert.Equal(ProxyFetchErrorCodes.TooLarge, result.ErrorCode);
        }

        [Fact]
        public async Task BodyStallingPastTheReadTimeoutIsRefusedWithTimeout()
        {
            _server.Handler = async context =>
            {
                context.Response.SendChunked = true;
                var first = new byte[] { 1 };
                await context.Response.OutputStream.WriteAsync(first, 0, 1);

                // Stall well past the read budget, then try to keep the stream alive.
                await Task.Delay(400);
                var second = new byte[] { 2 };
                await context.Response.OutputStream.WriteAsync(second, 0, 1);
                context.Response.Close();
            };

            var limits = ProxyFetchLimits.Default();
            limits.ReadTimeoutMs = 100;

            var result = await new ProxyHttpFetcher().FetchAsync(new Uri(_server.UrlFor("/stalling")), limits);

            Assert.False(result.Success);
            Assert.Equal(ProxyFetchErrorCodes.Timeout, result.ErrorCode);
        }

        [Fact]
        public async Task HeadersStallingPastTheConnectBudgetIsRefusedWithTimeout()
        {
            _server.Handler = async context =>
            {
                // Never send the response - headers alone must fail inside the connect budget.
                await Task.Delay(4000);
                context.Response.Close();
            };

            var limits = ProxyFetchLimits.Default();
            limits.ConnectTimeoutMs = 200;

            var result = await new ProxyHttpFetcher().FetchAsync(new Uri(_server.UrlFor("/silent")), limits);

            Assert.False(result.Success);
            Assert.Equal(ProxyFetchErrorCodes.Timeout, result.ErrorCode);
        }

        [Fact]
        public async Task AnUnreachableTargetReportsUnreachableNotSuccess()
        {
            var unusedPort = FreePort();
            var result = await new ProxyHttpFetcher().FetchAsync(new Uri($"http://127.0.0.1:{unusedPort}/nope"), ProxyFetchLimits.Default());

            Assert.False(result.Success);
            Assert.Equal(ProxyFetchErrorCodes.Unreachable, result.ErrorCode);
        }

        [Fact]
        public async Task AnHttpErrorStatusIsStillPassedThroughAsAStatusLine()
        {
            _server.Handler = async context =>
            {
                context.Response.StatusCode = 404;
                var page = Encoding.UTF8.GetBytes("not found page");
                await context.Response.OutputStream.WriteAsync(page, 0, page.Length);
                context.Response.Close();
            };

            var result = await new ProxyHttpFetcher().FetchAsync(new Uri(_server.UrlFor("/missing")), ProxyFetchLimits.Default());

            // The proxy relays whatever the target answered; a 404 page is still a page.
            Assert.True(result.Success);
            Assert.Equal(404, result.StatusCode);
            Assert.Equal(Encoding.UTF8.GetBytes("not found page"), result.Body);
        }

        [Theory]
        [InlineData("ftp://127.0.0.1/")]
        [InlineData("file:///C:/windows/win.ini")]
        public async Task NonHttpSchemeIsRefusedWithoutTouchingTheNetwork(string target)
        {
            var result = await new ProxyHttpFetcher().FetchAsync(new Uri(target), ProxyFetchLimits.Default());

            Assert.False(result.Success);
            Assert.Equal(ProxyFetchErrorCodes.BadRequest, result.ErrorCode);
        }

        public void Dispose()
        {
            _server.Dispose();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>Loopback HttpListener standing in for "a test page served on the server network".</summary>
        private sealed class TestHttpServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly string _prefix;

            internal TestHttpServer()
            {
                var port = FreePort();
                _prefix = $"http://127.0.0.1:{port}/";
                _listener = new HttpListener();
                _listener.Prefixes.Add(_prefix);
                _listener.Start();

                Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext context;
                        try
                        {
                            context = await _listener.GetContextAsync();
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Handler(context);
                            }
                            catch
                            {
                                // The client losing interest is not a test failure.
                            }
                        });
                    }
                });
            }

            internal Func<HttpListenerContext, Task> Handler = context =>
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
                return Task.CompletedTask;
            };

            internal string UrlFor(string path) => _prefix + path.TrimStart('/');

            public void Dispose()
            {
                try
                {
                    _listener.Stop();
                    _listener.Close();
                }
                catch
                {
                }
            }
        }
    }
}
