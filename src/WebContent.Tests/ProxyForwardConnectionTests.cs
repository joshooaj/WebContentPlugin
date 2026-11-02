using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
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
        public async Task ReportsLocal403RefusalsToTheBlockReporter()
        {
            // #69: the client-side gate refusal is exactly the block the Event Server never sees
            // - the reporter must get the canonical origin and the NotAllowed category.
            var blocks = new RecordingBlockReporter();
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("GET http://elsewhere.test/page HTTP/1.1\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input, output, Allowlist, query => Task.FromResult(new ProxyFetchResult { Success = true }), message => { }, null, null, blocks);
            }

            Assert.Equal(new[] { "NotAllowed http://elsewhere.test" }, blocks.Reports);
        }

        [Fact]
        public async Task ReportsWebsocketTunnelOpenFailuresAsTunnelRefused()
        {
            // A tunnel open that throws never reached the Event Server - client-side fault, one
            // TunnelRefused block for the upgrade origin.
            var blocks = new RecordingBlockReporter();
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(
                "GET ws://elsewhere.test:8080/ws HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: k\r\nSec-WebSocket-Version: 13\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input,
                    output,
                    new[] { "http://elsewhere.test:8080" },
                    query => Task.FromResult(new ProxyFetchResult { Success = true }),
                    message => { },
                    null,
                    request => throw new InvalidOperationException("messaging channel died"),
                    blocks);
            }

            Assert.Equal(new[] { "TunnelRefused http://elsewhere.test:8080" }, blocks.Reports);
        }

        [Fact]
        public async Task DoesNotReReportTunnelRefusalsTheEventServerAlreadyRecorded()
        {
            // The ES answered this open with an allowlist failure frame - it files that block
            // itself; the client staying quiet is what keeps the ring counts honest.
            var blocks = new RecordingBlockReporter();
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(
                "GET ws://elsewhere.test:8080/ws HTTP/1.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: k\r\nSec-WebSocket-Version: 13\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input,
                    output,
                    new[] { "http://elsewhere.test:8080" },
                    query => Task.FromResult(new ProxyFetchResult { Success = true }),
                    message => { },
                    null,
                    request => Task.FromResult(new ProxyTunnelOpenOutcome
                    {
                        Success = false,
                        ErrorCode = ProxyFetchErrorCodes.NotAllowed,
                        ErrorMessage = "refused by the VMS server",
                    }),
                    blocks);
            }

            Assert.Empty(blocks.Reports);
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
            // #58: the page names the side that failed - the VMS server could not reach the
            // target; the Smart Client machine is explicitly ruled out.
            Assert.Contains("VMS server could not reach the target", response);
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
            // #58: a Timeout FAILURE FRAME is the target leg timing out at the VMS server -
            // distinct from the silent-channel case below.
            Assert.Contains("VMS server could not get an answer from the target", response);
        }

        [Fact]
        public async Task RendersAnExplicit503PageWhenTheServerSideProxyComponentIsUnavailable()
        {
            // #58 AC1: the Event Server proxy component absent/unreachable gets its own page
            // naming the component - never a spinner, never a direct fallback.
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query => Task.FromResult(new ProxyFetchResult
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.ServerUnavailable,
                ErrorMessage = "The Smart Client has no proxy messaging channel to the VMS Event Server.",
            });

            var logged = new List<string>();
            var response = await Handle("GET http://10.0.0.5:8080/ HTTP/1.1\r\n\r\n", fetch, null, logged.Add);

            Assert.StartsWith("HTTP/1.1 503 Service Unavailable\r\n", response);
            Assert.Contains("Web Content proxy: 503", response);
            Assert.Contains("VMS server proxy component is not available", response);
            Assert.Contains("never loaded direct from this computer", response);
            // AC4: the log line attributes the failure to the Smart Client side, with the code.
            Assert.Contains(logged, line => line.Contains("Smart Client side") && line.Contains("ServerUnavailable"));
        }

        [Fact]
        public async Task RendersServerSideAllowlistRefusalAs403DistinctFromTargetFailure()
        {
            // #58: a server-side NotAllowed (allowlist drift between the two machines) keeps the
            // allowlist posture - it must not be mistaken for "the server could not reach it".
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query => Task.FromResult(new ProxyFetchResult
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.NotAllowed,
                ErrorMessage = "The origin 'http://10.0.0.9:80' is not in the proxy allowlist of any proxied workspace.",
            });

            var response = await Handle("GET http://10.0.0.9:80/ HTTP/1.1\r\n\r\n", fetch);

            Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", response);
            Assert.Contains("allowlist", response);
            Assert.DoesNotContain("could not reach", response);
        }

        [Fact]
        public async Task RendersAnExplicitPageWhenTheResponseExceedsTheSizeCap()
        {
            // #58 AC1: past the response size cap the view gets a page naming the cap, not a
            // truncated document and not a hang.
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query => Task.FromResult(new ProxyFetchResult
            {
                Success = false,
                ErrorCode = ProxyFetchErrorCodes.TooLarge,
                ErrorMessage = "Response body from 10.0.0.5:8080 exceeded the 5242880 byte response cap.",
            });

            var response = await Handle("GET http://10.0.0.5:8080/huge HTTP/1.1\r\n\r\n", fetch);

            Assert.StartsWith("HTTP/1.1 502 Bad Gateway\r\n", response);
            Assert.Contains("exceeded the VMS proxy response size cap", response);
            Assert.Contains("5242880", response);
        }

        // #56: allowlisted CONNECT tunnels are terminated at the listener; the decrypted inner
        // requests ride the same decision/fetch/render flow. The fake unwrap seam stands in for
        // the ephemeral-certificate SslStream, which its own tests and the live harness cover.

        [Fact]
        public async Task TerminatesAllowlistedConnectAndServesTheInnerRequestAgainstTheTunnelOrigin()
        {
            var seen = new List<ProxyFetchQuery>();
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
            {
                seen.Add(query);
                return Task.FromResult(new ProxyFetchResult
                {
                    Success = true,
                    StatusCode = 200,
                    StatusText = "OK",
                    Headers = new List<ProxyFetchHeader> { new ProxyFetchHeader { Name = "Content-Type", Value = "text/html" } },
                    BodyLength = 6,
                    Body = Encoding.UTF8.GetBytes("secure"),
                });
            };

            var request =
                "CONNECT cam1.test:8443 HTTP/1.1\r\n" +
                "Proxy-Connection: keep-alive\r\n" +
                "\r\n" +
                "GET /live/index.html HTTP/1.1\r\n" +
                "Host: cam1.test:8443\r\n" +
                "\r\n";

            var response = await HandleTunnel(request, new[] { "https://cam1.test:8443" }, fetch);

            Assert.StartsWith("HTTP/1.1 200 Connection established\r\n\r\n", response);
            Assert.Single(seen);
            Assert.Equal("https://cam1.test:8443/live/index.html", seen[0].Uri.AbsoluteUri);
            Assert.Contains("HTTP/1.1 200 OK\r\n", response);
            Assert.Contains("\r\n\r\nsecure", response);
        }

        [Fact]
        public async Task ServesSeveralInnerRequestsOverOneTerminatedTunnel()
        {
            var seen = new List<string>();
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
            {
                seen.Add(query.Uri.AbsoluteUri);
                return Task.FromResult(new ProxyFetchResult
                {
                    Success = true,
                    StatusCode = 200,
                    StatusText = "OK",
                    BodyLength = 0,
                    Body = new byte[0],
                });
            };

            var request =
                "CONNECT cam1.test:8443 HTTP/1.1\r\n\r\n" +
                "GET /one HTTP/1.1\r\nHost: cam1.test:8443\r\n\r\n" +
                "POST /two HTTP/1.1\r\nHost: cam1.test:8443\r\nContent-Length: 4\r\n\r\nbody";

            var response = await HandleTunnel(request, new[] { "https://cam1.test:8443" }, fetch);

            Assert.Equal(
                new[] { "https://cam1.test:8443/one", "https://cam1.test:8443/two" },
                seen);
            Assert.Equal(2, CountLines(response, "HTTP/1.1 200 OK\r\n"));
        }

        [Fact]
        public async Task RefusesVerbsInsideTheTunnelAfterTheHandshakeWithoutTouchingTheFetcher()
        {
            var fetchCalled = false;
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
            {
                fetchCalled = true;
                return Task.FromResult(new ProxyFetchResult { Success = false });
            };

            var request =
                "CONNECT cam1.test:8443 HTTP/1.1\r\n\r\n" +
                "TRACE /debug HTTP/1.1\r\nHost: cam1.test:8443\r\n\r\n";

            var response = await HandleTunnel(request, new[] { "https://cam1.test:8443" }, fetch);

            Assert.False(fetchCalled);
            Assert.StartsWith("HTTP/1.1 200 Connection established\r\n\r\n", response);
            Assert.Contains("HTTP/1.1 405", response);
        }

        [Fact]
        public async Task TunnelsInTunnelWebSocketUpgradeAndRelaysTheHandshakeIntoTheTunnel()
        {
            // #60: a wss:// upgrade inside a terminated tunnel gets its own byte tunnel - the
            // handshake gate and the open seam run inside the tunnel loop, and the target's 101
            // answer rides back INTO the tunnel stream (plaintext onto the raw socket would make
            // the browser fail TLS). The upgrade's pipelined first frame crosses into the tunnel.
            ProxyForwardedRequest opened = null;
            var handle = new FakeTunnelHandle();
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
            {
                opened = request;
                return Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = true,
                    Handle = handle,
                    HandshakeBytes = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: s7AQWlie1Rh0h9M7N8QV3YQ==\r\n\r\n"),
                });
            };

            var fetchCalled = false;
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch = query =>
            {
                fetchCalled = true;
                return Task.FromResult(new ProxyFetchResult { Success = false });
            };

            var inner = new DuplexStream(
                new MemoryStream(Encoding.GetEncoding("ISO-8859-1").GetBytes(
                    "GET /ws/live HTTP/1.1\r\nHost: cam1.test:8443\r\n" +
                    "Connection: keep-alive, Upgrade\r\nUpgrade: websocket\r\n" +
                    "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                    "\r\n\x81\x05hello")),
                new MemoryStream());

            string response;
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("CONNECT cam1.test:8443 HTTP/1.1\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input, output, new[] { "https://cam1.test:8443" }, fetch, message => { },
                    (stream, leftover, tunnelOrigin) => Task.FromResult<Stream>(inner),
                    openTunnel);
                response = Encoding.UTF8.GetString(output.ToArray());
            }

            // The CONNECT is accepted on the raw socket...
            Assert.StartsWith("HTTP/1.1 200 Connection established\r\n\r\n", response);
            Assert.False(fetchCalled);

            // ...the upgrade request reached the open seam resolved against the tunnel origin.
            Assert.Equal("https://cam1.test:8443/ws/live", opened.Uri.AbsoluteUri);

            // ...the target's 101 went back INTO the tunnel, verbatim, never onto the raw socket.
            var inside = Encoding.UTF8.GetString(inner.WrittenBuffer.ToArray());
            Assert.Contains(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: s7AQWlie1Rh0h9M7N8QV3YQ==\r\n\r\n",
                inside);

            // ...and the decrypted first frame crossed into the byte tunnel.
            Assert.Single(handle.Sent);
            Assert.Equal(new byte[] { 0x81, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, handle.Sent[0]);
        }

        [Fact]
        public async Task Renders502InTheTunnelAndKeepsItWhenTheInTunnelOpenFails()
        {
            // #60 fail-closed: an in-tunnel wss upgrade the Event Server refuses (old ES still
            // rejecting https-scheme targets, target unreachable, non-101) gets an explicit 502
            // page on the TUNNEL - the tunnel itself stays usable for the page's other requests.
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
                Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.NotAllowed,
                    ErrorMessage = "The tunnel target is not an allowlisted http origin.",
                });

            var inner = new DuplexStream(
                new MemoryStream(Encoding.UTF8.GetBytes(
                    "GET /ws/live HTTP/1.1\r\nHost: cam1.test:8443\r\n" +
                    "Connection: Upgrade\r\nUpgrade: websocket\r\n" +
                    "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n")),
                new MemoryStream());

            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("CONNECT cam1.test:8443 HTTP/1.1\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input, output, new[] { "https://cam1.test:8443" },
                    query => Task.FromResult(new ProxyFetchResult { Success = false }),
                    message => { },
                    (stream, leftover, tunnelOrigin) => Task.FromResult<Stream>(inner),
                    openTunnel);

                var inside = Encoding.UTF8.GetString(inner.WrittenBuffer.ToArray());
                Assert.Contains("HTTP/1.1 502", inside);
                Assert.Contains("allowlisted http origin", inside);
            }
        }

        [Fact]
        public async Task NamesTheOpenExceptionOnTheInTunnelFailurePageWhenTheOpenThrows()
        {
            // #60, same explicit page as the top-level ws posture (#57): an open that THROWS
            // names the exception on the 502 page written into the tunnel - the message is
            // never collapsed into the generic no-outcome wording.
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
                throw new IOException("the dialer exploded");

            var inner = new DuplexStream(
                new MemoryStream(Encoding.UTF8.GetBytes(
                    "GET /ws/live HTTP/1.1\r\nHost: cam1.test:8443\r\n" +
                    "Connection: Upgrade\r\nUpgrade: websocket\r\n" +
                    "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n")),
                new MemoryStream());

            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("CONNECT cam1.test:8443 HTTP/1.1\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input, output, new[] { "https://cam1.test:8443" },
                    query => Task.FromResult(new ProxyFetchResult { Success = false }),
                    message => { },
                    (stream, leftover, tunnelOrigin) => Task.FromResult<Stream>(inner),
                    openTunnel);

                var inside = Encoding.UTF8.GetString(inner.WrittenBuffer.ToArray());
                Assert.Contains("HTTP/1.1 502", inside);
                Assert.Contains("the dialer exploded", inside);
            }
        }

        [Fact]
        public async Task PumpsInTunnelTargetBytesBackIntoTheTunnelUntilTheHandleCloses()
        {
            // Both directions ride the tunnel stream after the 101: target bytes land decrypted
            // into the tunnel byte-identically - no chunking, no re-framing.
            var handle = new FakeTunnelHandle();
            handle.PushFromTarget(new byte[] { 0x81, 0x02, 0xAA, 0xBB });
            handle.PushFromTarget(new byte[] { 0x88, 0x00 });

            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
                Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = true,
                    Handle = handle,
                    HandshakeBytes = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\n\r\n"),
                });

            var inner = new DuplexStream(
                new MemoryStream(Encoding.UTF8.GetBytes(
                    "GET /ws HTTP/1.1\r\nHost: cam1.test:8443\r\n" +
                    "Connection: Upgrade\r\nUpgrade: websocket\r\n" +
                    "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n")),
                new MemoryStream());

            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("CONNECT cam1.test:8443 HTTP/1.1\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input, output, new[] { "https://cam1.test:8443" },
                    query => Task.FromResult(new ProxyFetchResult { Success = false }),
                    message => { },
                    (stream, leftover, tunnelOrigin) => Task.FromResult<Stream>(inner),
                    openTunnel);

                var inside = inner.WrittenBuffer.ToArray();
                Assert.True(StartsWithBytes(inside, "HTTP/1.1 101 Switching Protocols\r\n\r\n"));
                Assert.True(EndsWithBytes(inside, new byte[] { 0x81, 0x02, 0xAA, 0xBB, 0x88, 0x00 }),
                    "target bytes did not land byte-identical in the tunnel stream");
            }
        }

        private static bool StartsWithBytes(byte[] buffer, string prefix) =>
            Encoding.UTF8.GetString(buffer, 0, Math.Min(prefix.Length, buffer.Length)) == prefix;

        [Fact]
        public async Task RefusesConnectWith502WhenNoTlsTerminationIsAvailable()
        {
            // Defensive wiring path: an allowlisted CONNECT the listener cannot terminate (no
            // ephemeral-certificate plumbing) is refused outright - never tunnelled, never direct.
            var response = await Handle(
                "CONNECT cam1.test:8443 HTTP/1.1\r\n\r\n",
                query => Task.FromResult(new ProxyFetchResult { Success = false }),
                new[] { "https://cam1.test:8443" });

            Assert.StartsWith("HTTP/1.1 502 Bad Gateway\r\n", response);
            Assert.Contains("cam1.test", response);
        }

        [Fact]
        public async Task DisposesTheTerminatedTunnelWhenTheRequestLoopEnds()
        {
            // The TLS wrapper handed back by the unwrap seam is owned by the connection flow: the
            // leaf-backed SslStream must not outlive the tunnel (and in production its disposal
            // must not touch the raw socket, which the caller owns).
            MemoryStream tunnel = null;
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(
                "CONNECT cam1.test:8443 HTTP/1.1\r\n\r\nGET /live HTTP/1.1\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                tunnel = new MemoryStream();
                await ProxyForwardConnection.HandleAsync(
                    input, output, new[] { "https://cam1.test:8443" },
                    query => Task.FromResult(new ProxyFetchResult { Success = true, Body = Encoding.UTF8.GetBytes("ok"), StatusCode = 200 }),
                    message => { },
                    (stream, leftover, tunnelOrigin) =>
                    {
                        if (leftover != null && leftover.Length > 0)
                        {
                            tunnel.Write(leftover, 0, leftover.Length);
                        }

                        var buffer = new byte[4096];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            tunnel.Write(buffer, 0, read);
                        }

                        tunnel.Position = 0;
                        return Task.FromResult<Stream>(tunnel);
                    });
            }

            Assert.False(tunnel.CanRead);
        }

        [Fact]
        public async Task WritesInnerTunnelResponsesIntoTheTunnelNotOntoTheRawSocket()
        {
            // A terminated tunnel is a SEPARATE duplex stream in production (the SslStream
            // wrapper): after the CONNECT 200 the browser only understands TLS records, so the
            // inner responses must be written into the tunnel. Writing plaintext onto the raw
            // socket made the live browser fail the tunnel with a TLS decryption error (#56).
            var inner = new DuplexStream(
                new MemoryStream(Encoding.UTF8.GetBytes("GET /live HTTP/1.1\r\nHost: cam1.test:8443\r\n\r\n")),
                new MemoryStream());

            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("CONNECT cam1.test:8443 HTTP/1.1\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input,
                    output,
                    new[] { "https://cam1.test:8443" },
                    query => Task.FromResult(new ProxyFetchResult
                    {
                        Success = true,
                        StatusCode = 200,
                        StatusText = "OK",
                        Body = Encoding.UTF8.GetBytes("tunnel-body"),
                    }),
                    message => { },
                    (stream, leftover, tunnelOrigin) => Task.FromResult<Stream>(inner));

                var raw = Encoding.UTF8.GetString(output.ToArray());
                var inside = Encoding.UTF8.GetString(inner.WrittenBuffer.ToArray());

                Assert.Equal("HTTP/1.1 200 Connection established\r\n\r\n", raw);
                Assert.Contains("HTTP/1.1 200 OK\r\n", inside);
                Assert.Contains("tunnel-body", inside);
            }
        }

        // #57: an allowlisted ws:// upgrade becomes a CONNECT-style byte tunnel to the Event
        // Server instead of an HTTP forward. The open seam stands in for the MIP-messaging tunnel
        // (ProxyFetchClient.OpenTunnelAsync in production); the handle stands in for the live
        // tunnel. Everything after the 101 rides as opaque bytes in both directions.

        [Fact]
        public async Task TunnelsAllowlistedWebSocketUpgradeAndRelaysTheTargetsHandshakeVerbatim()
        {
            ProxyForwardedRequest opened = null;
            var handle = new FakeTunnelHandle();
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
            {
                opened = request;
                return Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = true,
                    Handle = handle,
                    HandshakeBytes = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: s7AQWlie1Rh0h9M7N8QV3YQ==\r\n\r\n"),
                });
            };

            var request =
                "GET http://10.0.0.5:8080/ws/live HTTP/1.1\r\n" +
                "Connection: keep-alive, Upgrade\r\nUpgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "\r\n" +
                "\x81\x05hello"; // browser's first websocket frame rides the same segment

            // Latin-1 keeps the frame bytes byte-faithful into the fake socket (UTF-8 would
            // expand 0x81 the way a text encoding must not for HTTP framing).
            var response = await HandleWebSocket(
                Encoding.GetEncoding("ISO-8859-1").GetBytes(request), openTunnel);

            Assert.Equal("http://10.0.0.5:8080/ws/live", opened.Uri.AbsoluteUri);

            // The target's 101 answer goes to the browser verbatim — no re-rendering, no
            // re-framing (the browser verifies Sec-WebSocket-Accept against the relayed key).
            Assert.StartsWith(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: s7AQWlie1Rh0h9M7N8QV3YQ==\r\n\r\n",
                response);

            // And the frame bytes that followed the upgrade head crossed into the tunnel.
            Assert.Single(handle.Sent);
            Assert.Equal(new byte[] { 0x81, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, handle.Sent[0]);
        }

        [Fact]
        public async Task TunnelsConnectCarriedWebSocketHandshakeAndRelaysBothAnswersVerbatim()
        {
            // Live repro (#57): Chromium proxies plain ws:// as CONNECT + a PLAINTEXT upgrade
            // inside the tunnel, origin-form against the CONNECT authority. The listener answers
            // the CONNECT, verifies the inner handshake, opens the byte tunnel, then relays the
            // target's 101 as the next bytes on the raw socket.
            ProxyForwardedRequest opened = null;
            var handle = new FakeTunnelHandle();
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
            {
                opened = request;
                return Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = true,
                    Handle = handle,
                    HandshakeBytes = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: s7AQWlie1Rh0h9M7N8QV3YQ==\r\n\r\n"),
                });
            };

            var request =
                "CONNECT 10.0.0.5:8080 HTTP/1.1\r\nProxy-Connection: keep-alive\r\n\r\n" +
                "GET /ws/live HTTP/1.1\r\nHost: 10.0.0.5:8080\r\n" +
                "Connection: Upgrade\r\nUpgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "\r\n" +
                "\x81\x05hello"; // the first websocket frame rides the same segment

            var response = await HandleWebSocket(
                Encoding.GetEncoding("ISO-8859-1").GetBytes(request), openTunnel);

            // The CONNECT is accepted first (the browser sends the handshake only after the 200)...
            Assert.StartsWith("HTTP/1.1 200 Connection established\r\n\r\n", response);
            // ...then the target's 101 rides the same raw stream verbatim.
            Assert.Contains(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: s7AQWlie1Rh0h9M7N8QV3YQ==\r\n\r\n",
                response);

            // The inner origin-form upgrade resolved against the tunnel's http twin origin.
            Assert.Equal("http://10.0.0.5:8080/ws/live", opened.Uri.AbsoluteUri);

            // And the frame bytes that followed the inner head crossed into the tunnel.
            Assert.Single(handle.Sent);
            Assert.Equal(new byte[] { 0x81, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, handle.Sent[0]);
        }

        [Fact]
        public async Task ClosesConnectWebSocketTunnelWhenTheFirstTunnelBytesAreNotAWebsocketHandshake()
        {
            // The http-twin CONNECT posture admits a WEBSOCKET handshake and nothing else: a
            // plain in-tunnel request (or a wss ClientHello) gets the tunnel closed with no
            // Event Server socket opened - never blind-tunnelled, never fetched.
            var openCalled = 0;
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
            {
                openCalled++;
                return Task.FromResult<ProxyTunnelOpenOutcome>(null);
            };

            var request =
                "CONNECT 10.0.0.5:8080 HTTP/1.1\r\n\r\n" +
                "GET /quiet HTTP/1.1\r\nHost: 10.0.0.5:8080\r\n\r\n";

            var response = await HandleWebSocket(Encoding.UTF8.GetBytes(request), openTunnel);

            Assert.Equal("HTTP/1.1 200 Connection established\r\n\r\n", response);
            Assert.Equal(0, openCalled);
        }

        [Fact]
        public async Task ClosesConnectWebSocketTunnelQuietlyWhenTheEventServerOpenFails()
        {
            // Inside a CONNECT tunnel no HTTP status can follow the 200: a failed tunnel open
            // ends the connection (the browser's websocket fails with onerror), it never writes
            // a plaintext error page into the tunnel stream.
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
                Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Unreachable,
                    ErrorMessage = "the target refused the tunnel handshake",
                });

            var request =
                "CONNECT 10.0.0.5:8080 HTTP/1.1\r\n\r\n" +
                "GET /ws/live HTTP/1.1\r\nHost: 10.0.0.5:8080\r\n" +
                "Connection: Upgrade\r\nUpgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n";

            var response = await HandleWebSocket(Encoding.UTF8.GetBytes(request), openTunnel);

            Assert.Equal("HTTP/1.1 200 Connection established\r\n\r\n", response);
        }

        [Fact]
        public async Task PumpsTargetBytesBackToTheBrowserUntilTheHandleCloses()
        {
            var handle = new FakeTunnelHandle();
            handle.PushFromTarget(new byte[] { 0x81, 0x02, 0xAA, 0xBB });
            handle.PushFromTarget(new byte[] { 0x88, 0x00 }); // close frame, then the tunnel ends

            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
                Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = true,
                    Handle = handle,
                    HandshakeBytes = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\n\r\n"),
                });

            var request =
                "GET http://10.0.0.5:8080/ws HTTP/1.1\r\n" +
                "Connection: Upgrade\r\nUpgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n";

            var response = await HandleWebSocketBytes(Encoding.UTF8.GetBytes(request), openTunnel);

            // The pumped target bytes land raw on the browser socket, in order, after the head -
            // byte-identical, never base64 or chunk-framed a second time.
            Assert.StartsWith("HTTP/1.1 101 Switching Protocols\r\n\r\n", Encoding.UTF8.GetString(response));
            Assert.True(EndsWithBytes(response, new byte[] { 0x81, 0x02, 0xAA, 0xBB, 0x88, 0x00 }),
                "target bytes did not land byte-identical on the browser socket");
        }

        [Fact]
        public async Task RefusesWebSocketTunnelWith502WhenOpenFailsOnTheEventServer()
        {
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
                Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = false,
                    StatusCode = 401,
                    ErrorCode = ProxyFetchErrorCodes.HandshakeRefused,
                    ErrorMessage = "the target answered 401 instead of 101",
                });

            var response = await HandleWebSocket(Encoding.UTF8.GetBytes(
                "GET http://10.0.0.5:8080/ws HTTP/1.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n"),
                openTunnel);

            Assert.StartsWith("HTTP/1.1 502 Bad Gateway\r\n", response);
            Assert.Contains("401", response);
        }

        [Fact]
        public async Task RefusesWebSocketUpgradeWith502WhenNoTunnelSeamIsWired()
        {
            // Defensive wiring: a listener without tunnel plumbing refuses outright - the
            // upgrade is never smuggled through the HTTP forward path and never taken direct.
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(
                "GET http://10.0.0.5:8080/ws HTTP/1.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n")))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(input, output, Allowlist, query => Task.FromResult(new ProxyFetchResult { Success = true }), message => { });
                Assert.StartsWith("HTTP/1.1 502 Bad Gateway", Encoding.UTF8.GetString(output.ToArray()));
            }
        }

        [Fact]
        public async Task ClosesTheTunnelHandleWhenTheBrowserSocketEnds()
        {
            var handle = new FakeTunnelHandle();
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
                Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = true,
                    Handle = handle,
                    HandshakeBytes = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\n\r\n"),
                });

            await HandleWebSocket(Encoding.UTF8.GetBytes(
                "GET http://10.0.0.5:8080/ws HTTP/1.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n"),
                openTunnel);

            Assert.True(handle.IsClosed);
            Assert.NotNull(handle.ClosedReason);
        }

        [Fact]
        public async Task EndsTheConnectionPromptlyWhenTheTunnelClosesOnTheTargetSide()
        {
            // Half-open reclaim: the target closed its end (handle finished) while the browser
            // kept pushing bytes - the flow must return so the listener can close the socket
            // instead of parking a task forever.
            var handle = new FakeTunnelHandle();
            var browser = new EndlessFrameSocket(Encoding.UTF8.GetBytes(
                "GET http://10.0.0.5:8080/ws HTTP/1.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n" +
                "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: kEy==\r\n\r\n"));
            Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel = request =>
                Task.FromResult(new ProxyTunnelOpenOutcome
                {
                    Success = true,
                    Handle = handle,
                    HandshakeBytes = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\n\r\n"),
                });

            using (var output = new MemoryStream())
            {
                var flow = ProxyForwardConnection.HandleAsync(
                    browser, output, Allowlist,
                    query => Task.FromResult(new ProxyFetchResult { Success = true }),
                    message => { },
                    null,
                    openTunnel);

                handle.Close("target closed");
                await Task.WhenAny(flow, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(flow.IsCompleted, "the flow stayed parked after the target side closed");
            }

            Assert.True(handle.IsClosed);
        }

        private static async Task<string> HandleWebSocket(byte[] requestBytes, Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel) =>
            Encoding.UTF8.GetString(await HandleWebSocketBytes(requestBytes, openTunnel));

        private static async Task<byte[]> HandleWebSocketBytes(byte[] requestBytes, Func<ProxyForwardedRequest, Task<ProxyTunnelOpenOutcome>> openTunnel)
        {
            using (var input = new MemoryStream(requestBytes))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(
                    input, output, Allowlist,
                    query => Task.FromResult(new ProxyFetchResult { Success = true }),
                    message => { },
                    null,
                    openTunnel);
                return output.ToArray();
            }
        }

        /// <summary>
        /// In-memory stand-in for the messaging-backed tunnel handle.
        /// </summary>
        private sealed class FakeTunnelHandle : ProxyTunnelHandle
        {
            internal readonly List<byte[]> Sent = new List<byte[]>();
            internal string ClosedReason;

            private readonly Queue<byte[]> _fromTarget = new Queue<byte[]>();
            private TaskCompletionSource<byte[]> _waiter;
            private bool _closed;

            internal override bool IsClosed => _closed;

            internal void PushFromTarget(byte[] bytes)
            {
                lock (_fromTarget)
                {
                    _fromTarget.Enqueue(bytes);
                    var waiter = _waiter;
                    _waiter = null;
                    waiter?.TrySetResult(bytes);
                }
            }

            internal override Task SendAsync(byte[] bytes)
            {
                lock (Sent)
                {
                    Sent.Add(bytes);
                }

                return Task.CompletedTask;
            }

            internal override Task<byte[]> NextDataAsync()
            {
                lock (_fromTarget)
                {
                    if (_fromTarget.Count > 0)
                    {
                        return Task.FromResult(_fromTarget.Dequeue());
                    }

                    if (_closed)
                    {
                        return Task.FromResult<byte[]>(null);
                    }

                    _waiter = new TaskCompletionSource<byte[]>();
                    return _waiter.Task;
                }
            }

            internal override void Close(string reason)
            {
                lock (_fromTarget)
                {
                    if (_closed)
                    {
                        return;
                    }

                    _closed = true;
                    ClosedReason = reason;
                    var waiter = _waiter;
                    _waiter = null;
                    waiter?.TrySetResult(null);
                }
            }
        }

        /// <summary>
        /// A browser socket that keeps pushing websocket frame bytes forever (the half-open
        /// case's busy direction); each read completes off-thread so the pump truly yields.
        /// </summary>
        private sealed class EndlessFrameSocket : MemoryStream
        {
            internal EndlessFrameSocket(byte[] buffer) : base(buffer)
            {
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Task.Run(() =>
                {
                    lock (this)
                    {
                        if (Position >= Length)
                        {
                            Position = 0;
                        }

                        return base.Read(buffer, offset, Math.Min(count, 64));
                    }
                });
        }

        /// <summary>Read side and write side are different streams, like a real SslStream tunnel.</summary>
        private sealed class DuplexStream : Stream
        {
            private readonly Stream _readSide;
            private readonly MemoryStream _writeSide;

            internal DuplexStream(Stream readSide, MemoryStream writeSide)
            {
                _readSide = readSide;
                _writeSide = writeSide;
            }

            internal MemoryStream WrittenBuffer => _writeSide;

            public override bool CanRead => _readSide.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _writeSide.Length;
            public override long Position { get => _writeSide.Position; set => throw new NotSupportedException(); }

            public override void Flush() => _writeSide.Flush();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => _readSide.Read(buffer, offset, count);
            public override void Write(byte[] buffer, int offset, int count) => _writeSide.Write(buffer, offset, count);
        }

        private static async Task<string> Handle(
            string request,
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch,
            string[] allowlist = null,
            Action<string> log = null)
        {
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(request)))
            using (var output = new MemoryStream())
            {
                await ProxyForwardConnection.HandleAsync(input, output, allowlist ?? Allowlist, fetch, message => { log?.Invoke(message); });
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        private static async Task<string> HandleTunnel(
            string request,
            string[] allowlist,
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch)
        {
            MemoryStream combined = null;
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(request)))
            using (var output = new MemoryStream())
            {
                // Identity unwrap: the CONNECT head's pipelined leftover plus the rest of the
                // stream becomes the decrypted tunnel stream - exactly the rebase the SslStream
                // wrapper performs in production (the ClientHello can ride the CONNECT segment).
                combined = new MemoryStream();
                await ProxyForwardConnection.HandleAsync(
                    input, output, allowlist, fetch, message => { },
                    (stream, leftover, tunnelOrigin) =>
                    {
                        if (leftover != null && leftover.Length > 0)
                        {
                            combined.Write(leftover, 0, leftover.Length);
                        }

                        var buffer = new byte[4096];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            combined.Write(buffer, 0, read);
                        }

                        combined.Position = 0;
                        return Task.FromResult<Stream>(combined);
                    });

                // The raw socket carries the CONNECT handshake; inner responses live on the
                // tunnel side (this identity seam folds them into the same buffer behind the
                // echoed inner requests).
                return Encoding.UTF8.GetString(output.ToArray()) + Encoding.UTF8.GetString(combined.ToArray());
            }
        }

        private static bool EndsWithBytes(byte[] actual, byte[] expectedTail)
        {
            if (actual.Length < expectedTail.Length)
            {
                return false;
            }

            for (var i = 0; i < expectedTail.Length; i++)
            {
                if (actual[actual.Length - expectedTail.Length + i] != expectedTail[i])
                {
                    return false;
                }
            }

            return true;
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

        /// <summary>Collects what the connection reports for #69 as "reason origin" strings.</summary>
        private sealed class RecordingBlockReporter : IProxyBlockReporter
        {
            public List<string> Reports { get; } = new List<string>();

            public void Report(Uri origin, string reason)
            {
                Reports.Add($"{reason} {ProxyPolicy.CanonicalOrigin(origin)}");
            }
        }
    }
}
