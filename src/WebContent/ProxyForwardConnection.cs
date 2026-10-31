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

        internal static async Task HandleAsync(
            Stream input,
            Stream output,
            System.Collections.Generic.IEnumerable<string> canonicalTargets,
            Func<ProxyFetchQuery, Task<ProxyFetchResult>> fetch,
            Action<string> log)
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

                case ProxyForwardDisposition.ConnectRefused:
                    log(rejectReason);
                    await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(502, "Bad Gateway", rejectReason + " The workspace proxy supports http(s) allowlisted targets fetched by the VMS server; https pages need a CONNECT tunnel this proxy does not provide."));
                    return;
            }

            // #55: the request body must reach the fetcher for login POSTs and XHR writes to work.
            var bodyOutcome = await TryResolveRequestBodyAsync(request, head.Leftover, input, log);
            if (bodyOutcome.RefusalPage != null)
            {
                await WriteAsync(output, bodyOutcome.RefusalPage);
                return;
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
                return;
            }

            if (result != null && result.Success)
            {
                await WriteAsync(output, ProxyForwardCodec.RenderForwardedResponse(result));
                return;
            }

            var message = result == null ? "The VMS proxy fetcher returned no outcome." : result.ErrorMessage;
            if (result != null && result.ErrorCode == ProxyFetchErrorCodes.Timeout)
            {
                log($"Proxy fetch for {request.Uri} timed out: {message}");
                await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(504, "Gateway Timeout", message));
                return;
            }

            log($"Proxy fetch for {request.Uri} failed (code={result?.ErrorCode}): {message}");
            await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(502, "Bad Gateway", message));
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
                return RequestBodyOutcome.Accept(null);
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
                return RequestBodyOutcome.Accept(null);
            }

            if (leftover.Length > declared)
            {
                var reason = $"The request body for '{request.RawTarget}' carries more bytes than its declared Content-Length.";
                log(reason);
                return RequestBodyOutcome.Refuse(ProxyForwardCodec.RenderErrorResponse(400, "Bad Request", reason));
            }

            var buffer = new byte[declared];
            var taken = 0;
            if (leftover.Length > 0)
            {
                Buffer.BlockCopy(leftover, 0, buffer, 0, leftover.Length);
                taken = leftover.Length;
            }

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

            return RequestBodyOutcome.Accept(buffer);
        }

        /// <summary>Outcome of request-body resolution: either the bytes or a rendered refusal page.</summary>
        private sealed class RequestBodyOutcome
        {
            internal readonly byte[] Body;
            internal readonly byte[] RefusalPage;

            private RequestBodyOutcome(byte[] body, byte[] refusalPage)
            {
                Body = body;
                RefusalPage = refusalPage;
            }

            internal static RequestBodyOutcome Accept(byte[] body)
            {
                return new RequestBodyOutcome(body, null);
            }

            internal static RequestBodyOutcome Refuse(byte[] page)
            {
                return new RequestBodyOutcome(null, page);
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

        private static async Task<RequestHead> ReadRequestHeadAsync(Stream input)
        {
            var head = new RequestHead();
            var buffer = new byte[8 * 1024];
            var received = new MemoryStream();
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                received.Write(buffer, 0, read);
                if (ContainsHeadTerminator(received.GetBuffer(), 0, (int)received.Length))
                {
                    break;
                }

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
