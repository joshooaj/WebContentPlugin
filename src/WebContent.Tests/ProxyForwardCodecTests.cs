using System;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #54: the loopback forwarding proxy parses the absolute-form HTTP requests Chromium
    /// sends to a proxy ("GET http://host/path HTTP/1.1"). These tests pin the pure head-parsing
    /// seam the socket listener feeds; the socket itself is verified live (SC-1).
    /// </summary>
    public class ProxyForwardCodecTests
    {
        [Fact]
        public void ParsesAbsoluteFormGetRequestWithHeaders()
        {
            var head =
                "GET http://10.0.0.5:8080/monitor/live?cam=2 HTTP/1.1\r\n" +
                "Host: 10.0.0.5:8080\r\n" +
                "Accept: text/html\r\n" +
                "\r\n";

            var parsed = ProxyForwardCodec.TryParseRequestHead(head, out var request, out var rejectReason);

            Assert.True(parsed, rejectReason);
            Assert.Equal("GET", request.Method);
            Assert.Equal("http://10.0.0.5:8080/monitor/live?cam=2", request.Uri.AbsoluteUri);
            Assert.Equal("text/html", request.Headers["Accept"]);
        }

        [Fact]
        public void RefusesOriginFormRequestLineInsteadOfGuessingAnOrigin()
        {
            var head = "GET /index.html HTTP/1.1\r\nHost: 10.0.0.5:8080\r\n\r\n";

            var parsed = ProxyForwardCodec.TryParseRequestHead(head, out var request, out var rejectReason);

            Assert.False(parsed);
            Assert.Null(request);
            Assert.Contains("/index.html", rejectReason);
        }

        [Fact]
        public void RefusesNonHttpSchemeTargets()
        {
            // net472 Uri.TryCreate accepts path- and scheme-like junk as file:/gopher: uris, so
            // the parser itself must gate on http(s), not defer the whole scheme check.
            var head = "GET ftp://fileserver/share/report.pdf HTTP/1.1\r\n\r\n";

            var parsed = ProxyForwardCodec.TryParseRequestHead(head, out var request, out var rejectReason);

            Assert.False(parsed);
            Assert.Null(request);
            Assert.Contains("ftp", rejectReason);
        }

        [Fact]
        public void RecognizesConnectRequestsByTheirAuthorityTarget()
        {
            // Chromium opens https routes with CONNECT host:port. The codec parses it out of the
            // head so the decision layer can refuse it as a refused tunnel (502), not a 400.
            var head = "CONNECT portal.example.com:443 HTTP/1.1\r\nProxy-Connection: keep-alive\r\n\r\n";

            var parsed = ProxyForwardCodec.TryParseRequestHead(head, out var request, out var rejectReason);

            Assert.True(parsed, rejectReason);
            Assert.Equal("CONNECT", request.Method);
            Assert.Equal("portal.example.com:443", request.RawTarget);
            Assert.Null(request.Uri);
        }

        [Fact]
        public void RendersForwardedResponseWithStatusAndContentHeaders()
        {
            // AC3: a reassembled chunk stream becomes a forwarded response with the target's
            // status code and content headers. Body framing is the proxy's own Content-Length.
            var result = new ProxyFetchResult
            {
                Success = true,
                StatusCode = 200,
                StatusText = "OK",
                Headers = new System.Collections.Generic.List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Content-Type", Value = "text/html" },
                    new ProxyFetchHeader { Name = "Server", Value = "device-httpd/1.2" },
                },
                BodyLength = 15,
                Body = System.Text.Encoding.UTF8.GetBytes("<h1>device</h1>"),
            };

            var rendered = System.Text.Encoding.UTF8.GetString(ProxyForwardCodec.RenderForwardedResponse(result));

            Assert.StartsWith("HTTP/1.1 200 OK\r\n", rendered);
            Assert.Contains("Content-Type: text/html\r\n", rendered);
            Assert.Contains("Server: device-httpd/1.2\r\n", rendered);
            Assert.Contains("Content-Length: 15\r\n", rendered);
            Assert.Contains("\r\n\r\n<h1>device</h1>", rendered);
        }

        [Fact]
        public void DropsHopByHopHeadersAndStaleFramingFromForwardedResponses()
        {
            // Hop-by-hop headers describe the fetcher's own connection, not the browser's; a
            // stale Content-Length from the target would corrupt body framing after reassembly.
            var result = new ProxyFetchResult
            {
                Success = true,
                StatusCode = 200,
                StatusText = "OK",
                Headers = new System.Collections.Generic.List<ProxyFetchHeader>
                {
                    new ProxyFetchHeader { Name = "Connection", Value = "keep-alive" },
                    new ProxyFetchHeader { Name = "Transfer-Encoding", Value = "chunked" },
                    new ProxyFetchHeader { Name = "Proxy-Connection", Value = "keep-alive" },
                    new ProxyFetchHeader { Name = "Content-Length", Value = "999" },
                    new ProxyFetchHeader { Name = "Content-Type", Value = "image/jpeg" },
                },
                BodyLength = 3,
                Body = new byte[] { 0xFF, 0xD8, 0x00 },
            };

            var rendered = System.Text.Encoding.UTF8.GetString(ProxyForwardCodec.RenderForwardedResponse(result));

            Assert.DoesNotContain("keep-alive", rendered);
            Assert.DoesNotMatch("(?im)^Proxy-Connection:", rendered);
            Assert.DoesNotMatch("(?im)^Transfer-Encoding:", rendered);
            Assert.Contains("Content-Length: 3\r\n", rendered);
            Assert.DoesNotContain("Content-Length: 999", rendered);
        }

        [Fact]
        public void RendersErrorResponseWithProxyExplanationBody()
        {
            // Refusals (403 not allowlisted, 405 non-GET, 502 refused tunnel) carry an html body
            // the browser can show, framed by the proxy itself.
            var rendered = System.Text.Encoding.UTF8.GetString(
                ProxyForwardCodec.RenderErrorResponse(403, "Forbidden", "The origin 'http://evil.test' is not allowlisted."));

            Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", rendered);
            Assert.Contains("Content-Type: text/html; charset=utf-8\r\n", rendered);
            Assert.Contains("The origin &#39;http://evil.test&#39; is not allowlisted.", rendered);
            Assert.Matches("(?i)Connection: close", rendered);
        }
    }
}
