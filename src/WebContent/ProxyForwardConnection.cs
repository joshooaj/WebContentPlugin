using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// One proxied browser connection handled end to end (#54; write verbs and request bodies
    /// since #55): read the request head from the stream, run the decision table
    /// (<see cref="ProxyForwardPolicy"/>), read the request body per its declared Content-Length,
    /// relay allowlisted requests through the caller-supplied fetch seam (MIP messaging to the
    /// Event Server fetcher in production, a fake in tests), and render the answer back. Head
    /// reading is capped so a chatty or hostile local client cannot pin unbounded memory, and a
    /// request body is only buffered when its declared size fits the request cap.
    /// </summary>
    internal static class ProxyForwardConnection
    {
        /// <summary>Request heads past this size are refused outright: browser requests are far smaller.</summary>
        internal const int MaxHeadBytes = 64 * 1024;

        /// <summary>
        /// Request-body cap (#55): login forms and XHR payloads are kilobytes; a declared body
        /// past this bound is refused with 413 before any of it is taken from the socket.
        /// </summary>
        internal const int MaxRequestBodyBytes = 1024 * 1024;

        internal static Task HandleAsync(
            Stream input,
            Stream output,
            System.Collections.Generic.IEnumerable<string> canonicalTargets,
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch,
            Action<string> log)
        {
            return HandleAsync(input, output, canonicalTargets, fetch, log, null);
        }

        /// <summary>
        /// Optional tunnel-termination seam (#56): given the raw socket stream, any bytes the
        /// head reader already pulled past the CONNECT head (a TLS ClientHello can pipeline into
        /// the same segment), and the allowlisted https origin, wrap the stream in TLS with an
        /// ephemeral local certificate and return the decrypted stream (null when termination is
        /// impossible). The pure flow stays testable with an identity seam; production passes
        /// <c>ProxyLoopbackTls</c>.
        /// </summary>
        internal static async Task HandleAsync(
            Stream input,
            Stream output,
            System.Collections.Generic.IEnumerable<string> canonicalTargets,
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch,
            Action<string> log,
            Func<Stream, byte[], Uri, Task<Stream>> unwrapTunnelTls)
        {
            var head = await ReadRequestHeadAsync(input);
            if (head == null)
            {
                // Chromium pre-opens sockets speculatively and may never write to them: an empty
                // connection is closed quietly, with no response.
                return;
            }

            var disposition = ProxyForwardPolicy.Decide(canonicalTargets, head.Text, out var request, out var rejectReason);
            switch (disposition)
            {
                case ProxyForwardDisposition.BadRequest:
                    log(rejectReason);
                    await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(400, "Bad Request", rejectReason));
                    return;

                case ProxyForwardDisposition.OriginNotAllowed:
                    log(rejectReason);
                    await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(403, "Forbidden", rejectReason));
                    return;

                case ProxyForwardDisposition.MethodNotAllowed:
                    log(rejectReason);
                    await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(405, "Method Not Allowed", rejectReason));
                    return;

                case ProxyForwardDisposition.ConnectTerminate:
                    if (unwrapTunnelTls == null)
                    {
                        // Defensive wiring: no ephemeral-certificate plumbing on this listener. The
                        // tunnel is refused outright - never blind-tunnelled, never taken direct.
                        var reason = $"The CONNECT to '{request.RawTarget}' cannot be terminated by this proxy; https targets are fetched by the VMS server over tunnels this listener terminates.";
                        log(reason);
                        await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(502, "Bad Gateway", reason));
                        return;
                    }

                    log($"Proxy tunnel to '{request.RawTarget}' allowlisted; terminating TLS at the loopback listener.");
                    await WriteAsync(output, Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));

                    Stream tunnel;
                    try
                    {
                        tunnel = await unwrapTunnelTls(input, head.Leftover, request.Uri);
                    }
                    catch (Exception exception)
                    {
                        log($"Proxy TLS termination for '{request.RawTarget}' failed: {exception.Message}");
                        return;
                    }

                    if (tunnel == null)
                    {
                        log($"Proxy TLS termination for '{request.RawTarget}' produced no tunnel; the connection is closed.");
                        return;
                    }

                    try
                    {
                        await ServeTunnelRequestsAsync(tunnel, canonicalTargets, request.Uri, fetch, log);
                    }
                    finally
                    {
                        // The TLS wrapper is ours once termination succeeded; the raw socket stream
                        // stays the caller's (its using-block closes underneath).
                        try { tunnel.Dispose(); } catch { }
                    }

                    return;

                case ProxyForwardDisposition.ConnectRefused:
                    log(rejectReason);
                    await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(502, "Bad Gateway", rejectReason + " The workspace proxy supports http(s) allowlisted targets fetched by the VMS server; https pages need a CONNECT tunnel this proxy does not provide."));
                    return;
            }

            await ServeForwardedRequestAsync(request, head.Leftover, input, output, fetch, log);
        }

        /// <summary>
        /// The request loop inside one terminated tunnel (#56): Chromium pipelines several inner
        /// requests over a single TLS connection, each origin-form against the tunnel origin that
        /// the allowlist already admitted when the tunnel was accepted. After the CONNECT 200 the
        /// browser only reads TLS records, so every inner answer goes back through the tunnel
        /// stream - writing plaintext to the raw socket makes the browser fail the tunnel with a
        /// decryption error (live repro, #56).
        /// </summary>
        private static async Task ServeTunnelRequestsAsync(
            Stream tunnel,
            System.Collections.Generic.IEnumerable<string> canonicalTargets,
            Uri tunnelOrigin,
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch,
            Action<string> log)
        {
            var pending = new byte[0];
            while (true)
            {
                var head = await ReadRequestHeadAsync(tunnel, pending);
                if (head == null)
                {
                    return;
                }

                var disposition = ProxyForwardPolicy.Decide(canonicalTargets, head.Text, tunnelOrigin, out var request, out var rejectReason);
                switch (disposition)
                {
                    case ProxyForwardDisposition.Forward:
                        break;

                    case ProxyForwardDisposition.BadRequest:
                        // A head this broken leaves the stream position untrustworthy: answer and close.
                        log(rejectReason);
                        await WriteAsync(tunnel, ProxyForwardCodec.RenderErrorResponse(400, "Bad Request", rejectReason));
                        return;

                    case ProxyForwardDisposition.MethodNotAllowed:
                        log(rejectReason);
                        await WriteAsync(tunnel, ProxyForwardCodec.RenderErrorResponse(405, "Method Not Allowed", rejectReason));
                        pending = head.Leftover;
                        continue;

                    default:
                        // OriginNotAllowed inside a tunnel means the inner target drifted off the
                        // accepted origin (codec already blocks that) - refuse and keep the tunnel.
                        log(rejectReason ?? $"In-tunnel request '{request?.RawTarget}' refused.");
                        await WriteAsync(tunnel, ProxyForwardCodec.RenderErrorResponse(403, "Forbidden", rejectReason ?? "The in-tunnel request was refused."));
                        pending = head.Leftover;
                        continue;
                }

                var remainder = await ServeForwardedRequestAsync(request, head.Leftover, tunnel, tunnel, fetch, log);
                if (remainder == null)
                {
                    return;
                }

                pending = remainder;
            }
        }

        /// <summary>
        /// Body-resolve, fetch, and render one decided-Forward request. Returns the pipelined
        /// remainder bytes past this request's body for the caller's next request (#56 tunnels),
        /// or null when the connection must close.
        /// </summary>
        private static async Task<byte[]> ServeForwardedRequestAsync(
            ProxyForwardedRequest request,
            byte[] leftover,
            Stream input,
            Stream output,
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch,
            Action<string> log)
        {
            // #55: the request body must reach the fetcher for login POSTs and XHR writes to work.
            var bodyOutcome = await TryResolveRequestBodyAsync(request, leftover, input, log);
            if (bodyOutcome.RefusalPage != null)
            {
                await WriteAsync(output, bodyOutcome.RefusalPage);
                return null;
            }

            var query = new ProxyFetchQuery
            {
                Uri = request.Uri,
                Method = request.Method,
                Headers = ProxyForwardCodec.ToOutboundRequestHeaders(request),
                Body = bodyOutcome.Body,
            };

            ProxyFetchResult result;
            try
            {
                result = await fetch(query);
            }
            catch (Exception exception)
            {
                log($"Proxy fetch for {request.Uri} threw: {exception.Message}");
                await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(502, "Bad Gateway", $"The VMS proxy fetcher failed: {exception.Message}"));
                return null;
            }

            if (result != null && result.Success)
            {
                await WriteAsync(output, ProxyForwardCodec.RenderForwardedResponse(result));
                return bodyOutcome.Remainder;
            }

            var message = result == null ? "The VMS proxy fetcher returned no outcome." : result.ErrorMessage;
            if (result != null && result.ErrorCode == ProxyFetchErrorCodes.Timeout)
            {
                log($"Proxy fetch for {request.Uri} timed out: {message}");
                await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(504, "Gateway Timeout", message));
                return bodyOutcome.Remainder;
            }

            log($"Proxy fetch for {request.Uri} failed (code={result?.ErrorCode}): {message}");
            await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(502, "Bad Gateway", message));
            return bodyOutcome.Remainder;
        }

        /// <summary>
        /// Decide and read the request body (#55). A chunked body carries no declared size to
        /// bound, so it is refused (411); a declared length past the cap is refused before any
        /// body bytes are taken (413); a body that contradicts its declaration is refused (400).
        /// A refusal comes back as an already-rendered page.
        /// </summary>
        private static async Task<RequestBodyOutcome> TryResolveRequestBodyAsync(
            ProxyForwardedRequest request,
            byte[] leftover,
            Stream input,
            Action<string> log)
        {
            string transferEncoding;
            request.Headers.TryGetValue("Transfer-Encoding", out transferEncoding);
            if (!string.IsNullOrEmpty(transferEncoding) &&
                transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var reason = $"The proxy requires a Content-Length on request bodies; the chunked body for '{request.RawTarget}' is refused.";
                log(reason);
                return RequestBodyOutcome.Refuse(ProxyForwardCodec.RenderErrorResponse(411, "Length Required", reason));
            }

            string contentLengthText;
            request.Headers.TryGetValue("Content-Length", out contentLengthText);
            if (string.IsNullOrEmpty(contentLengthText))
            {
                // No body to resolve; any bytes already in hand belong to a pipelined next request.
                return RequestBodyOutcome.Accept(null, leftover);
            }

            int declared;
            if (!int.TryParse(contentLengthText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out declared) || declared < 0)
            {
                var reason = $"The Content-Length '{contentLengthText}' for '{request.RawTarget}' is not a usable size.";
                log(reason);
                return RequestBodyOutcome.Refuse(ProxyForwardCodec.RenderErrorResponse(400, "Bad Request", reason));
            }

            if (declared > MaxRequestBodyBytes)
            {
                var reason = $"The request body for '{request.RawTarget}' declares {declared} bytes, past the proxy's {MaxRequestBodyBytes} byte request cap.";
                log(reason);
                return RequestBodyOutcome.Refuse(ProxyForwardCodec.RenderErrorResponse(413, "Payload Too Large", reason));
            }

            if (declared == 0)
            {
                return RequestBodyOutcome.Accept(null, leftover);
            }

            var buffer = new byte[declared];
            var fromLeftover = Math.Min(leftover.Length, declared);
            var remainder = leftover.Length > declared
                ? new byte[leftover.Length - declared]
                : new byte[0];
            if (fromLeftover > 0)
            {
                Buffer.BlockCopy(leftover, 0, buffer, 0, fromLeftover);
            }

            if (remainder.Length > 0)
            {
                // The client pipelined its next request into the same segment (#56 tunnels):
                // take exactly the declared body, hand the rest to the next request's head read.
                Buffer.BlockCopy(leftover, declared, remainder, 0, remainder.Length);
            }

            var taken = fromLeftover;
            while (taken < declared)
            {
                var read = await input.ReadAsync(buffer, taken, declared - taken);
                if (read <= 0)
                {
                    break;
                }

                taken += read;
            }

            if (taken < declared)
            {
                var reason = $"The request body for '{request.RawTarget}' ended after {taken} bytes, short of its declared {declared}.";
                log(reason);
                return RequestBodyOutcome.Refuse(ProxyForwardCodec.RenderErrorResponse(400, "Bad Request", reason));
            }

            return RequestBodyOutcome.Accept(buffer, remainder);
        }

        /// <summary>
        /// Outcome of request-body resolution: either the bytes plus any pipelined remainder
        /// (#56), or a rendered refusal page.
        /// </summary>
        private sealed class RequestBodyOutcome
        {
            internal readonly byte[] Body;
            internal readonly byte[] Remainder;
            internal readonly byte[] RefusalPage;

            private RequestBodyOutcome(byte[] body, byte[] remainder, byte[] refusalPage)
            {
                Body = body;
                Remainder = remainder ?? new byte[0];
                RefusalPage = refusalPage;
            }

            internal static RequestBodyOutcome Accept(byte[] body, byte[] remainder = null)
            {
                return new RequestBodyOutcome(body, remainder, null);
            }

            internal static RequestBodyOutcome Refuse(byte[] page)
            {
                return new RequestBodyOutcome(null, null, page);
            }
        }

        /// <summary>
        /// The parsed request head plus any body bytes the client pipelined into the same read.
        /// </summary>
        private sealed class RequestHead
        {
            internal string Text;
            internal byte[] Leftover = new byte[0];
        }

        private static Task<RequestHead> ReadRequestHeadAsync(Stream input)
        {
            return ReadRequestHeadAsync(input, null);
        }

        /// <summary>
        /// Read one request head, optionally seeded with bytes the previous request's reader
        /// already pulled past its own boundary (#56: the next pipelined head of a tunnel, or a
        /// ClientHello before TLS termination).
        /// </summary>
        private static async Task<RequestHead> ReadRequestHeadAsync(Stream input, byte[] seed)
        {
            var head = new RequestHead();
            var buffer = new byte[8 * 1024];
            var received = new MemoryStream();
            if (seed != null && seed.Length > 0)
            {
                received.Write(seed, 0, seed.Length);
            }

            while (!ContainsHeadTerminator(received.GetBuffer(), 0, (int)received.Length))
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                received.Write(buffer, 0, read);
                if (received.Length > MaxHeadBytes)
                {
                    return null;
                }
            }

            if (received.Length == 0)
            {
                return null;
            }

            // Head bytes are cut at the header terminator; whatever the client pipelined after it
            // (a POST body that arrived in the same segment) becomes leftover for the body reader
            // (#55). Latin-1 keeps byte values faithful per RFC 7230.
            var bytes = received.ToArray();
            var terminator = IndexOfHeadTerminator(bytes, 0, bytes.Length);
            var textLength = terminator >= 0 ? terminator : bytes.Length;
            var leftoverOffset = terminator >= 0 ? terminator + TerminatorLength(bytes, terminator) : bytes.Length;
            if (leftoverOffset < bytes.Length)
            {
                head.Leftover = new byte[bytes.Length - leftoverOffset];
                Buffer.BlockCopy(bytes, leftoverOffset, head.Leftover, 0, head.Leftover.Length);
            }

            head.Text = Encoding.GetEncoding("ISO-8859-1").GetString(bytes, 0, textLength);
            return head;
        }

        private static bool ContainsHeadTerminator(byte[] bytes, int offset, int length)
        {
            return IndexOfHeadTerminator(bytes, offset, length) >= 0;
        }

        private static int IndexOfHeadTerminator(byte[] bytes, int offset, int length)
        {
            // CRLFCRLF, tolerating bare-LF clients. Rescanned from 0 each round: the leftover
            // split for the request-body reader (#55) needs the terminator's absolute position,
            // and a partially-arrived terminator in one read is only re-found once its tail lands.
            for (var i = offset; i + 1 < length; i++)
            {
                if (bytes[i] == '\n' && bytes[i + 1] == '\n')
                {
                    return i;
                }

                if (i + 3 < length &&
                    bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static int TerminatorLength(byte[] bytes, int terminator)
        {
            return bytes[terminator] == '\r' ? 4 : 2;
        }

        private static async Task WriteAsync(Stream output, byte[] response)
        {
            await output.WriteAsync(response, 0, response.Length);
            await output.FlushAsync();
        }
    }
}
