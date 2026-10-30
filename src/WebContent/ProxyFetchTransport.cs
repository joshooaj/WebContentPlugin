using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Server-side transport for the proxy-fetch contract (#53): performs one HTTP(S) GET from
    /// the server network and enforces the ticket's bounds — a connect budget, a per-read stall
    /// budget, and a maximum response size — independent of which runtime hosts the plugin (the
    /// .NET Framework HttpWebRequest timeouts double as native bounds, but the explicit
    /// task-budget races below make the behavior identical everywhere).
    ///
    /// A target HTTP error status (404 and friends) is still a successful fetch: the proxy
    /// relays the target's status line, headers, and page.
    /// </summary>
    internal sealed class ProxyHttpFetcher
    {
        /// <summary>
        /// GET the target under the given limits. Never throws: transport outcomes are reported in
        /// the returned ProxyFetchResult so framing/logging can react to each failure class.
        /// </summary>
        internal async Task<ProxyFetchResult> FetchAsync(Uri target, ProxyFetchLimits limits)
        {
            if (!ProxyPolicy.IsHttpUri(target))
            {
                return Failure(ProxyFetchErrorCodes.BadRequest, "The proxy-fetch target must be an http(s) url.");
            }

            var request = (HttpWebRequest)WebRequest.Create(target);
            request.Method = "GET";
            request.UserAgent = "CascadiaWebContentPlugin/MIP-proxy-fetcher";

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
                return MapWebException(webException, target, limits);
            }
            catch (IOException ioException)
            {
                // The compat stack surfaces a refused/reset connection as IOException where the
                // .NET Framework host reports WebException(ConnectFailure).
                return Failure(ProxyFetchErrorCodes.Unreachable, $"{ProxyPolicy.CanonicalOrigin(target)}: {ioException.Message}");
            }
            catch (Exception exception)
            {
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
                headers.Add(new ProxyFetchHeader { Name = name, Value = response.Headers[name] });
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
    }
}
