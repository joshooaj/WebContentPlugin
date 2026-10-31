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
    /// Since #55 the flow also reads the request body per Content-Length and carries method,
    /// headers, and body to the fetch seam, and relays Set-Cookie/3xx back untouched.
    /// </summary>
    public class ProxyForwardConnectionTests
    {
        private static readonly string[] Allowlist = { "http://10.0.0.5:8080" };

        [Fact]
        public async Task ForwardsAllowlistedGetAndRelaysTheFetchedPage()
        {
            var requests = new List<string>();
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
            {
                requests.Add(query.Uri.AbsoluteUri);
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
        public async Task RelaysPostMethodFormBodyAndBrowserRequestHeadersToTheFetcher()
        {
            // #55 AC1/AC2: a vendor login POST crosses the seam with method, body, and the
            // browser's Cookie header intact; the target's 302 + Set-Cookie come back untouched.
            ProxyFetchQuery seen = null;
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
            {
                seen = query;
                return Task.FromResult(new ProxyFetchResult
                {
                    Success = true,
                    StatusCode = 302,
                    StatusText = "Found",
                    Headers = new List<ProxyFetchHeader>
                    {
                        new ProxyFetchHeader { Name = "Location", Value = "/dashboard" },
                        new ProxyFetchHeader { Name = "Set-Cookie", Value = "SID=abc123; Path=/; HttpOnly" },
                    },
                    BodyLength = 0,
                    Body = new byte[0],
                });
            };

            var form = "user=admin&pass=x&go=1";
            var request =
                "POST http://10.0.0.5:8080/login HTTP/1.1\r\n" +
                "Host: 10.0.0.5:8080\r\n" +
                "Content-Type: application/x-www-form-urlencoded\r\n" +
                "Cookie: sid=keepme\r\n" +
                $"Content-Length: {form.Length}\r\n" +
                "\r\n" +
                form;

            var response = await Handle(request, fetch);

            Assert.Equal("POST", seen.Method);
            Assert.Equal("http://10.0.0.5:8080/login", seen.Uri.AbsoluteUri);
            Assert.Equal(Encoding.UTF8.GetBytes(form), seen.Body);
            Assert.Contains(seen.Headers, h => h.Name == "Cookie" && h.Value == "sid=keepme");
            Assert.Contains(seen.Headers, h => h.Name == "Content-Type");
            Assert.DoesNotContain(seen.Headers, h => h.Name == "Host");

            // AC3 half: the session cookie rides back to the browser on the redirect.
            Assert.StartsWith("HTTP/1.1 302 Found\r\n", response);
            Assert.Contains("Location: /dashboard\r\n", response);
            Assert.Contains("Set-Cookie: SID=abc123; Path=/; HttpOnly\r\n", response);
        }

        [Fact]
        public async Task RelaysEverySetCookieValueAsItsOwnHeaderLine()
        {
            // Cookie round-trip hazard: joined Set-Cookie values corrupt the session. The proxy
            // must render each captured value as its own header line.
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query => Task.FromResult(new ProxyFetchResult
            {
                Success = true,
                StatusCode = 200,
                StatusText = "OK",
                Headers = new List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Set-Cookie", Value = "SID=a; Path=/" },
                    new ProxyFetchHeader { Name = "Set-Cookie", Value = "CSRF=b; Path=/; Secure" },
                    new ProxyFetchHeader { Name = "Content-Type", Value = "text/html" },
                },
                BodyLength = 2,
                Body = Encoding.UTF8.GetBytes("ok"),
            });

            var response = await Handle("GET http://10.0.0.5:8080/ HTTP/1.1\r\n\r\n", fetch);

            Assert.Equal(2, CountLines(response, "Set-Cookie: "));
            Assert.Contains("Set-Cookie: SID=a; Path=/\r\n", response);
            Assert.Contains("Set-Cookie: CSRF=b; Path=/; Secure\r\n", response);
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

            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
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

        [Fact]
        public async Task RefusesNonAllowlistedNavigationWith403WithoutTouchingTheFetcher()
        {
            var fetchCalled = false;
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
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
        public async Task RefusesOversizedRequestBodyByDeclarationWithoutTouchingTheFetcher()
        {
            // A declared Content-Length past the proxy's request-body cap is refused before any
            // body bytes are taken from the socket.
            var fetchCalled = false;
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
            {
                fetchCalled = true;
                return Task.FromResult(new ProxyFetchResult { Success = false });
            };

            var oversized = ProxyForwardConnection.MaxRequestBodyBytes + 1;
            var response = await Handle(
                $"POST http://10.0.0.5:8080/upload HTTP/1.1\r\nContent-Length: {oversized}\r\n\r\n",
                fetch);

            Assert.False(fetchCalled);
            Assert.StartsWith("HTTP/1.1 413", response);
        }

        [Fact]
        public async Task RefusesChunkedRequestBodiesWith411WithoutTouchingTheFetcher()
        {
            // Browsers put a Content-Length on form/XHR bodies; a chunked request body has no
            // declared size to bound, and the proxy refuses it rather than buffering endlessly.
            var fetchCalled = false;
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
            {
                fetchCalled = true;
                return Task.FromResult(new ProxyFetchResult { Success = false });
            };

            var response = await Handle(
                "POST http://10.0.0.5:8080/stream HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n",
                fetch);

            Assert.False(fetchCalled);
            Assert.StartsWith("HTTP/1.1 411", response);
        }

        [Fact]
        public async Task SurfacesUnreachableFetchFailuresAs502WithTheFetchersReason()
        {
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query => Task.FromResult(new ProxyFetchResult
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
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query => Task.FromResult(new ProxyFetchResult
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.Timeout,
                ErrorMessage = "no response headers within 5000 ms",
            });

            var response = await Handle("GET http://10.0.0.5:8080/slow HTTP/1.1\r\n\r\n", fetch);

            Assert.StartsWith("HTTP/1.1 504 Gateway Timeout\r\n", response);
        }

        private static async Task<string> Handle(string request, Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch)
        {
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(request)))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(input, output, Allowlist, fetch, message => { });
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        private static int CountLines(string response, string prefix)
        {
            var count = 0;
            var index = 0;
            while ((index = response.IndexOf(prefix, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += prefix.Length;
            }

            return count;
        }
    }
}
