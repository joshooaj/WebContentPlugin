using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// One proxied browser connection handled end to end (#54): read the request head from the
    /// stream, run the decision table (<see cref="ProxyForwardPolicy"/>), fetch allowlisted GETs
    /// through the caller-supplied fetch seam (MIP messaging to the Event Server in production,
    /// a fake in tests), and render the answer back. Head reading is capped so a chatty or hostile
    /// local client cannot pin unbounded memory on the receiver path.
    /// </summary>
    internal static class ProxyForwardConnection
    {
        /// <summary>Request heads past this size are refused outright: browser requests are far smaller.</summary>
        internal const int MaxHeadBytes = 64 * 1024;

        internal static async Task HandleAsync(
            Stream input,
            Stream output,
            System.Collections.Generic.IEnumerable<string> canonicalTargets,
            Func<string, Task<ProxyFetchResult>> fetch,
            Action<string> log)
        {
            var head = await ReadRequestHeadAsync(input);
            if (head == null)
            {
                // Chromium pre-opens sockets speculatively and may never write to them: an empty
                // connection is closed quietly, with no response.
                return;
            }

            var disposition = ProxyForwardPolicy.Decide(canonicalTargets, head, out var request, out var rejectReason);
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
                    await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(405, "Method Not Allowed", rejectReason + " The workspace proxy only fetches GET."));
                    return;

                case ProxyForwardDisposition.ConnectRefused:
                    log(rejectReason);
                    await WriteAsync(output, ProxyForwardCodec.RenderErrorResponse(502, "Bad Gateway", rejectReason + " The workspace proxy supports http(s) allowlisted targets fetched by the VMS server; https pages need a CONNECT tunnel this proxy does not provide."));
                    return;
            }

            ProxyFetchResult result;
            try
            {
                result = await fetch(request.Uri.AbsoluteUri);
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

        private static async Task<string> ReadRequestHeadAsync(Stream input)
        {
            var buffer = new byte[8 * 1024];
            var head = new MemoryStream();
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                head.Write(buffer, 0, read);
                if (ContainsHeadTerminator(head.GetBuffer(), 0, (int)head.Length))
                {
                    break;
                }

                if (head.Length > MaxHeadBytes)
                {
                    return null;
                }
            }

            if (head.Length == 0)
            {
                return null;
            }

            // Head bytes are cut at the header terminator; request bodies (which the GET-only
            // proxy never forwards) are never read. Latin-1 keeps byte values faithful per RFC 7230.
            var bytes = head.ToArray();
            var terminator = IndexOfHeadTerminator(bytes, 0, bytes.Length);
            var textLength = terminator >= 0 ? terminator : bytes.Length;
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, 0, textLength);
        }

        private static bool ContainsHeadTerminator(byte[] bytes, int offset, int length)
        {
            return IndexOfHeadTerminator(bytes, offset, length) >= 0;
        }

        private static int IndexOfHeadTerminator(byte[] bytes, int offset, int length)
        {
            // CRLFCRLF, tolerating bare-LF clients, searched only against the newly appended range.
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

        private static async Task WriteAsync(Stream output, byte[] response)
        {
            await output.WriteAsync(response, 0, response.Length);
            await output.FlushAsync();
        }
    }
}
