using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #54: the connection-handling flow over a byte stream — read the request head, run
    /// the decision table, fetch or refuse, render the answer. The stream stands in for the
    /// loopback socket; the TcpListener accept loop around it is host-coupled and verified live.
    /// </summary>
    public class ProxyForwardConnectionTests
    {
        private static readonly string[] Allowlist = { "http://10.0.0.5:8080" };

        [Fact]
        public async Task ForwardsAllowlistedGetAndRelaysTheFetchedPage()
        {
            var requests = new List<string>();
            Func<string, Task<ProxyFetchResult>> fetch = url =>
            {
                requests.Add(url);
                return Task.FromResult(new ProxyFetchResult
                {
                    Success = true,
                    StatusCode = 200,
                    StatusText = "OK",
                    Headers = new List<ProxyFetchHeader> { new ProxyFetchHeader { Name = "Content-Type", Value = "text/html" } },
                    BodyLength = 5,
                    Body = Encoding.UTF8.GetBytes("paged"),
                });
            };

            var response = await Handle("GET http://10.0.0.5:8080/index.html HTTP/1.1\r\nAccept: */*\r\n\r\n", fetch);

            Assert.Equal(new[] { "http://10.0.0.5:8080/index.html" }, requests);
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
            Assert.Contains("\r\n\r\npaged", response);
        }

        [Fact]
        public async Task RefusesNonAllowlistedNavigationWith403WithoutTouchingTheFetcher()
        {
            var fetchCalled = false;
            Func<string, Task<ProxyFetchResult>> fetch = url =>
            {
                fetchCalled = true;
                return Task.FromResult(new ProxyFetchResult { Success = false });
            };

            var response = await Handle("GET http://elsewhere.test/page HTTP/1.1\r\n\r\n", fetch);

            Assert.False(fetchCalled);
            Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", response);
            Assert.Contains("elsewhere.test", response);
        }

        [Fact]
        public async Task SurfacesUnreachableFetchFailuresAs502WithTheFetchersReason()
        {
            Func<string, Task<ProxyFetchResult>> fetch = url => Task.FromResult(new ProxyFetchResult
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.Unreachable,
                ErrorMessage = "10.0.0.5:8080: no route to host",
            });

            var response = await Handle("GET http://10.0.0.5:8080/gone HTTP/1.1\r\n\r\n", fetch);

            Assert.StartsWith("HTTP/1.1 502 Bad Gateway\r\n", response);
            Assert.Contains("no route to host", response);
        }

        [Fact]
        public async Task MapsFetchTimeoutsTo504()
        {
            Func<string, Task<ProxyFetchResult>> fetch = url => Task.FromResult(new ProxyFetchResult
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.Timeout,
                ErrorMessage = "no response headers within 5000 ms",
            });

            var response = await Handle("GET http://10.0.0.5:8080/slow HTTP/1.1\r\n\r\n", fetch);

            Assert.StartsWith("HTTP/1.1 504 Gateway Timeout\r\n", response);
        }

        [Fact]
        public async Task RelaysTheFramedChunkStreamThroughCorrelationIntoTheForwardedResponse()
        {
            // AC4's round trip on the existing #53 doubles: the fetcher's meta+chunk frames feed
            // the correlator exactly as the MIP receiver thread would, and the reassembled
            // outcome is what gets framed back to the browser.
            var fetched = new ProxyFetchResult
            {
                Success = true,
                StatusCode = 200,
                StatusText = "OK",
                Headers = new List<ProxyFetchHeader> { new ProxyFetchHeader { Name = "Content-Type", Value = "text/css" } },
                BodyLength = 9,
                Body = Encoding.UTF8.GetBytes("body{red}"),
            };

            Func<string, Task<ProxyFetchResult>> fetch = url =>
            {
                var correlation = new ProxyFetchCorrelation();
                var completion = correlation.Begin("exchange-1", TimeSpan.FromSeconds(5));
                foreach (var frame in ProxyFetchFramer.Frame("exchange-1", fetched, 4))
                {
                    correlation.Feed(frame);
                }

                return completion;
            };

            var response = await Handle("GET http://10.0.0.5:8080/theme.css HTTP/1.1\r\n\r\n", fetch);

            Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
            Assert.Contains("Content-Type: text/css\r\n", response);
            Assert.Contains("Content-Length: 9\r\n", response);
            Assert.Contains("\r\n\r\nbody{red}", response);
        }

        private static async Task<string> Handle(string request, Func<string, Task<ProxyFetchResult>> fetch)
        {
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(request)))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(input, output, Allowlist, fetch, message => { });
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }
}
