using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Server-side transport for the proxy-fetch contract (#53; write verbs, request headers,
    /// and request bodies since #55): replays one proxied browser request
    /// from the server network and enforces the ticket's bounds — a connect budget, a per-read stall
    /// budget, and a maximum response size — independent of which runtime hosts the plugin (the
    /// .NET Framework HttpWebRequest timeouts double as native bounds, but the explicit
    /// task-budget races below make the behavior identical everywhere).
    ///
    /// A target HTTP error status (404 and friends) is still a successful fetch: the proxy
    /// relays the target's status line, headers, and page. So is a 3xx: redirects and their
    /// Set-Cookie headers ride back to the browser untouched (#55 — the session cookie lives in
    /// the browser, never in the VMS server: UseCookies and AllowAutoRedirect are both off).
    /// </summary>
    internal sealed class ProxyHttpFetcher
    {
        /// <summary>
        /// Replay the request carried by the query under the given limits, validating the target's
        /// TLS certificate normally (#56 default). Never throws: transport outcomes are reported in
        /// the returned ProxyFetchResult so framing/logging can react to each failure class.
        /// </summary>
        internal Task<ProxyFetchResult> FetchAsync(ProxyFetchQuery query, ProxyFetchLimits limits)
        {
            return FetchAsync(query, limits, ProxyFetchTlsMode.ValidateNormally);
        }

        /// <summary>
        /// Replay the request with the given TLS decision for the https leg to the target (#56).
        /// AcceptUntrusted comes from the target's own allowlist entry — the caller scopes it per
        /// origin — and is applied ONLY to this request's own callback, never to the process-wide
        /// ServicePointManager and never to any other target.
        /// </summary>
        internal async Task<ProxyFetchResult> FetchAsync(ProxyFetchQuery query, ProxyFetchLimits limits, ProxyFetchTlsMode tlsMode)
        {
            var target = query == null ? null : query.Uri;
            if (!ProxyPolicy.IsHttpUri(target))
            {
                return Failure(ProxyFetchErrorCodes.BadRequest, "The proxy-fetch target must be an http(s) url.");
            }

            var request = (HttpWebRequest)WebRequest.Create(target);
            request.Method = string.IsNullOrWhiteSpace(query.Method) ? "GET" : query.Method.Trim().ToUpperInvariant();
            request.UserAgent = "CascadiaWebContentPlugin/MIP-proxy-fetcher";

            // #56: certificate validation on the target leg. Default is the runtime's full chain
            // validation; a callback that always returns false when it rejects records WHY the
            // fetch died (the exception class differs across runtimes, the callback firing does
            // not), so the refusal can name the certificate instead of a vague transport error.
            var tlsDecision = new TlsDecision(tlsMode);
            if (target.Scheme == Uri.UriSchemeHttps)
            {
                request.ServerCertificateValidationCallback = tlsDecision.Evaluate;
            }

            // #55 passthrough rules: the browser owns the session. A 3xx and its Set-Cookie values
            // relay back to the browser instead of being followed or stored here, so no target
            // credential ever persists on the VMS server. UseCookies only exists on the .NET
            // Framework host (and matters there: it auto-creates a CookieContainer that would
            // swallow Set-Cookie); the compat shim is cookie-less by construction.
            request.AllowAutoRedirect = false;
            try
            {
                var useCookies = typeof(HttpWebRequest).GetProperty("UseCookies");
                if (useCookies != null && useCookies.CanWrite)
                {
                    useCookies.SetValue(request, false);
                }
            }
            catch (Exception)
            {
                // Property may be unsupported or read-only on compat stacks.
            }

            try
            {
                // Vendor device servers often stall ~3 s waiting for it; browsers only send it
                // when they choose to, and that choice is relayed as a header instead.
                request.ServicePoint.Expect100Continue = false;
            }
            catch (Exception)
            {
            }

            ApplyRequestHeaders(request, query.Headers);

            // On the .NET Framework host these are native enforcement; elsewhere the explicit
            // budgets below enforce the same bounds. The properties sit a full second past the
            // budgets so native enforcement can never race (and pre-empt) the budgeted checks.
            try
            {
                request.Timeout = limits.ConnectTimeoutMs + 1000;
                request.ReadWriteTimeout = limits.ReadTimeoutMs + 1000;
            }
            catch (Exception)
            {
                // Property may be unsupported on compat stacks; the budgets cover enforcement.
            }

            if (query.Body != null && query.Body.Length > 0)
            {
                var writeFailure = await WriteRequestBodyAsync(request, query.Body, target, limits, tlsDecision);
                if (writeFailure != null)
                {
                    return writeFailure;
                }
            }

            HttpWebResponse response;
            try
            {
                var responseTask = request.GetResponseAsync();
                var winner = await Task.WhenAny(responseTask, Task.Delay(limits.ConnectTimeoutMs));
                if (winner != responseTask)
                {
                    try
                    {
                        request.Abort();
                    }
                    catch (Exception)
                    {
                    }

                    return Failure(ProxyFetchErrorCodes.Timeout, $"No response headers from {ProxyPolicy.CanonicalOrigin(target)} within {limits.ConnectTimeoutMs} ms.");
                }

                response = (HttpWebResponse)await responseTask;
            }
            catch (WebException webException) when (webException.Response is HttpWebResponse errorResponse)
            {
                // The target answered with an error status: relay it like any other response.
                response = errorResponse;
            }
            catch (WebException webException)
            {
                if (tlsDecision.Rejected)
                {
                    return TlsValidationFailure(target);
                }

                return MapWebException(webException, target, limits);
            }
            catch (IOException ioException)
            {
                if (tlsDecision.Rejected)
                {
                    return TlsValidationFailure(target);
                }

                // The compat stack surfaces a refused/reset connection as IOException where the
                // .NET Framework host reports WebException(ConnectFailure).
                return Failure(ProxyFetchErrorCodes.Unreachable, $"{ProxyPolicy.CanonicalOrigin(target)}: {ioException.Message}");
            }
            catch (Exception exception)
            {
                if (tlsDecision.Rejected)
                {
                    return TlsValidationFailure(target);
                }

                try
                {
                    request.Abort();
                }
                catch (Exception)
                {
                }

                return Failure(ProxyFetchErrorCodes.Error, $"{ProxyPolicy.CanonicalOrigin(target)}: {exception.Message}");
            }

            using (response)
            {
                return await ReadCappedBodyAsync(response, target, limits);
            }
        }

        /// <summary>
        /// #55: replay the browser's request headers on the fetcher's own request. Property-backed
        /// headers go through their HttpWebRequest properties, connection-level headers are the
        /// fetcher's own to state (never relayed from the wire — a crafted payload cannot make the
        /// VMS server speak hop-by-hop verbs at a target), and .NET Framework header restrictions
        /// degrade to a skip, never a throw.
        /// </summary>
        private static void ApplyRequestHeaders(HttpWebRequest request, List<ProxyFetchHeader> headers)
        {
            if (headers == null)
            {
                return;
            }

            foreach (var header in headers)
            {
                if (header == null || string.IsNullOrEmpty(header.Name) || header.Value == null)
                {
                    continue;
                }

                // A wire-supplied value with line breaks could inject headers into the ES's own
                // request to the target; property setters do not reject these the way
                // WebHeaderCollection does, so the gate lives here.
                if (header.Value.IndexOf('\r') >= 0 || header.Value.IndexOf('\n') >= 0 ||
                    header.Name.IndexOf(':') >= 0)
                {
                    continue;
                }

                try
                {
                    switch (header.Name.ToUpperInvariant())
                    {
                        case "USER-AGENT":
                            request.UserAgent = header.Value;
                            break;
                        case "REFERER":
                            request.Referer = header.Value;
                            break;
                        case "CONTENT-TYPE":
                            request.ContentType = header.Value;
                            break;
                        default:
                            if (TransportBlockedRequestHeaders.Contains(header.Name))
                            {
                                break;
                            }

                            request.Headers[header.Name] = header.Value;
                            break;
                    }
                }
                catch (Exception)
                {
                    // Restricted names or host-stack value validation (e.g. ContentType rejects
                    // non-printables on .NET Framework): skip the single header, never fail the
                    // fetch - FetchAsync's never-throws contract covers every wire-supplied value.
                }
            }
        }

        /// <summary>
        /// Request headers the fetcher never replays regardless of what arrived on the wire: they
        /// describe a connection or body framing the fetcher states itself. Cookie, Authorization,
        /// Content-Type (property), Accept-*, and custom app headers deliberately pass (#55).
        /// </summary>
        private static readonly HashSet<string> TransportBlockedRequestHeaders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Host",
                "Content-Length",
                "Connection",
                "Keep-Alive",
                "Proxy-Connection",
                "Proxy-Authenticate",
                "Proxy-Authorization",
                "TE",
                "Trailer",
                "Transfer-Encoding",
                "Upgrade",
                "Expect",
            };

        private static async Task<ProxyFetchResult> WriteRequestBodyAsync(HttpWebRequest request, byte[] body, Uri target, ProxyFetchLimits limits, TlsDecision tlsDecision)
        {
            request.ContentLength = body.Length;
            try
            {
                var streamTask = request.GetRequestStreamAsync();
                var winner = await Task.WhenAny(streamTask, Task.Delay(limits.ConnectTimeoutMs));
                if (winner != streamTask)
                {
                    try
                    {
                        request.Abort();
                    }
                    catch (Exception)
                    {
                    }

                    return Failure(ProxyFetchErrorCodes.Timeout, $"Could not start sending the request body to {ProxyPolicy.CanonicalOrigin(target)} within {limits.ConnectTimeoutMs} ms.");
                }

                using (var requestStream = await streamTask)
                {
                    await requestStream.WriteAsync(body, 0, body.Length);
                }

                return null;
            }
            catch (WebException webException)
            {
                if (tlsDecision.Rejected)
                {
                    return TlsValidationFailure(target);
                }

                return MapWebException(webException, target, limits);
            }
            catch (Exception exception)
            {
                if (tlsDecision.Rejected)
                {
                    return TlsValidationFailure(target);
                }

                return Failure(ProxyFetchErrorCodes.Error, $"{ProxyPolicy.CanonicalOrigin(target)}: request body not sent: {exception.Message}");
            }
        }

        private static async Task<ProxyFetchResult> ReadCappedBodyAsync(HttpWebResponse response, Uri target, ProxyFetchLimits limits)
        {
            var result = new ProxyFetchResult
            {
                Success = false,
                StatusCode = (int)response.StatusCode,
                StatusText = GetStatusText(response),
                Headers = ReadHeaders(response),
            };

            var body = new MemoryStream();
            var buffer = new byte[16 * 1024];

            try
            {
                using (var stream = response.GetResponseStream())
                {
                    while (true)
                    {
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        var winner = await Task.WhenAny(readTask, Task.Delay(limits.ReadTimeoutMs));
                        if (winner != readTask)
                        {
                            return Failure(ProxyFetchErrorCodes.Timeout, $"Body of {ProxyPolicy.CanonicalOrigin(target)} stalled for more than {limits.ReadTimeoutMs} ms.");
                        }

                        var read = await readTask;
                        if (read == 0)
                        {
                            break;
                        }

                        if (body.Length + read > limits.MaxResponseBytes)
                        {
                            return Failure(ProxyFetchErrorCodes.TooLarge, $"Response body from {ProxyPolicy.CanonicalOrigin(target)} exceeded the {limits.MaxResponseBytes} byte response cap.");
                        }

                        body.Write(buffer, 0, read);
                    }
                }
            }
            catch (WebException webException)
            {
                return MapWebException(webException, target, limits);
            }
            catch (IOException ioException)
            {
                return Failure(ProxyFetchErrorCodes.Error, $"{ProxyPolicy.CanonicalOrigin(target)}: stream failure: {ioException.Message}");
            }

            result.Success = true;
            result.Body = body.ToArray();
            result.BodyLength = body.Length;
            return result;
        }

        private static List<ProxyFetchHeader> ReadHeaders(HttpWebResponse response)
        {
            var headers = new List<ProxyFetchHeader>();
            foreach (var name in response.Headers.AllKeys)
            {
                // One entry per raw value (#55): indexer access joins repeated values with ", ",
                // which corrupts repeated Set-Cookie headers (and the comma inside their Expires
                // dates) — a session cookie that fails to round-trip kills vendor logins.
                string[] values;
                try
                {
                    values = response.Headers.GetValues(name);
                }
                catch (Exception)
                {
                    values = new[] { response.Headers[name] };
                }

                foreach (var value in values ?? new[] { response.Headers[name] })
                {
                    headers.Add(new ProxyFetchHeader { Name = name, Value = value });
                }
            }

            // Content-Type/Content-Encoding live on the response object itself and may be missing
            // from the raw collection on some stacks; carry them explicitly so content headers pass.
            if (!string.IsNullOrEmpty(response.ContentType) && !HasHeader(headers, "Content-Type"))
            {
                headers.Add(new ProxyFetchHeader { Name = "Content-Type", Value = response.ContentType });
            }

            return headers;
        }

        private static bool HasHeader(List<ProxyFetchHeader> headers, string name)
        {
            foreach (var header in headers)
            {
                if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetStatusText(HttpWebResponse response)
        {
            // The wire reason phrase is not publicly exposed on HttpWebResponse, so the status
            // code name ("NotFound") is carried as the human-readable status text. The requester
            // keys off StatusCode; this is display metadata only.
            return response.StatusCode.ToString();
        }

        private static ProxyFetchResult MapWebException(WebException webException, Uri target, ProxyFetchLimits limits)
        {
            switch (webException.Status)
            {
                // A budget breach normally surfaces through the explicit task races above; a
                // native Timeout that still reaches here is reported as a timeout, not confusion.
                case WebExceptionStatus.Timeout:
                    return Failure(ProxyFetchErrorCodes.Timeout, $"{ProxyPolicy.CanonicalOrigin(target)}: {webException.Message}");

                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    return Failure(ProxyFetchErrorCodes.Unreachable, $"{ProxyPolicy.CanonicalOrigin(target)}: {webException.Message}");

                // Compat stacks classify a refused socket as UnknownError; the socket exception
                // inside separates those from genuinely unknown faults.
                case WebExceptionStatus.UnknownError when IsSocketRefusedOrUnreachable(webException):
                    return Failure(ProxyFetchErrorCodes.Unreachable, $"{ProxyPolicy.CanonicalOrigin(target)}: {webException.Message}");

                default:
                    return Failure(ProxyFetchErrorCodes.Error, $"{ProxyPolicy.CanonicalOrigin(target)}: {webException.Message}");
            }
        }

        private static bool IsSocketRefusedOrUnreachable(WebException webException)
        {
            for (var inner = webException.InnerException; inner != null; inner = inner.InnerException)
            {
                if (inner is SocketException socketException &&
                    (socketException.SocketErrorCode == SocketError.ConnectionRefused ||
                     socketException.SocketErrorCode == SocketError.NetworkUnreachable ||
                     socketException.SocketErrorCode == SocketError.HostUnreachable))
                {
                    return true;
                }
            }

            return false;
        }

        private static ProxyFetchResult Failure(string errorCode, string message)
        {
            return new ProxyFetchResult
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = message,
            };
        }

        /// <summary>
        /// #56: the per-request certificate-validation decision. The callback is attached to THIS
        /// HttpWebRequest only — ServicePointManager's process-wide callback is never touched, so a
        /// plugin running inside the Event Server cannot weaken TLS for any other caller, and the
        /// override stays scoped to the one allowlisted origin the caller checked. A rejecting
        /// callback records that fact so the resulting transport exception maps to TlsValidation
        /// no matter which exception shape the host runtime produces.
        /// </summary>
        private sealed class TlsDecision
        {
            private readonly ProxyFetchTlsMode _mode;
            internal bool Rejected;

            internal TlsDecision(ProxyFetchTlsMode mode)
            {
                _mode = mode;
            }

            internal bool Evaluate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
            {
                if (_mode == ProxyFetchTlsMode.AcceptUntrusted)
                {
                    return true;
                }

                if (sslPolicyErrors != SslPolicyErrors.None)
                {
                    Rejected = true;
                }

                return sslPolicyErrors == SslPolicyErrors.None;
            }
        }

        private static ProxyFetchResult TlsValidationFailure(Uri target)
        {
            var origin = ProxyPolicy.CanonicalOrigin(target);
            return Failure(
                ProxyFetchErrorCodes.TlsValidation,
                $"{origin}: the TLS certificate failed validation (untrusted chain, expired, or hostname mismatch). The proxy validates target certificates and refuses this one; enable the per-target 'accept untrusted certificate' override for '{origin}' on the workspace page to relay this device.");
        }
    }
}