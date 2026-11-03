using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// Ticket #88: the view-layer refusal for https requests an off-allowlist origin would carry.
    ///
    /// Root cause this answers: Chromium transports every https target to the loopback listener
    /// as a CONNECT, and Chromium treats any non-2xx CONNECT answer as a tunnel refusal and drops
    /// its body outright. The forwarder's 403 + friendly page (which it still writes for a refused
    /// CONNECT, pinned by ProxyForwardConnectionTests) therefore never reaches the screen - the
    /// browser renders its native ERR_TUNNEL_CONNECTION_FAILED page instead. Only absolute-form
    /// plain-http requests can ride an HTTP response, so #58/#66 pages were reachable for http
    /// refusals only.
    ///
    /// The fix refuses at the browser layer instead of styling the CONNECT answer: a proxied
    /// WebView2 consults this gate through its WebResourceRequested event, BEFORE Chromium opens
    /// any CONNECT. A refused https request gets the same friendly 403 page as the http posture,
    /// committed as the document at the requested URL - history, address bar, and status code all
    /// match the http row. The decision uses the ONE shared <see cref="ProxyPolicy"/> matcher with
    /// the same entries and the same VMS floor the listener gates on (pinned by
    /// ProxyAllowlistGateAgreementTests), so the view can never disagree with the proxy.
    ///
    /// What deliberately stays on the old path: plain http (absolute-form - the proxy's own 403
    /// page already reaches the screen) and ws:// / wss:// upgrades (WebResourceRequested does not
    /// surface websocket requests, so those refusals keep the CONNECT posture and the native page
    /// - documented in README + verification matrix). Fail-closed is untouched: this class only
    /// answers refusals locally; it never widens what may load, and the CONNECT pinning still
    /// forbids any direct route.
    /// </summary>
    internal static class ProxyViewRefusal
    {
        /// <summary>
        /// True when this https request's origin is outside the workspace allowlist - the view
        /// must then answer the refusal itself. Non-https uris (http rides the proxy's
        /// absolute-form path) and allowlisted origins answer false and reach the network unchanged.
        /// On refusal, <paramref name="rejectReason"/> carries the verbatim technical sentence and
        /// <paramref name="refusalHtml"/> the #66 friendly 403 page (NotAllowed cause chip, the
        /// exact origin, and the canonical https entry that would admit it).
        /// </summary>
        internal static bool TryRefuseHttpsRequest(
            IEnumerable<string> canonicalTargets,
            IEnumerable<string> vmsFloorHosts,
            Uri requestUri,
            string method,
            out string rejectReason,
            out string refusalHtml)
        {
            rejectReason = null;
            refusalHtml = null;

            // The CONNECT posture only exists for https; every other scheme already reaches a
            // proxy-rendered page (or never enters a proxied browser at all).
            if (requestUri == null || !string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (ProxyPolicy.IsRequestAllowed(canonicalTargets, requestUri, vmsFloorHosts))
            {
                return false;
            }

            // The SAME sentence shape the forward gate writes for an http refusal, so the
            // diagnostics read identically whichever layer decided the refusal.
            rejectReason = $"The request origin '{ProxyPolicy.CanonicalOrigin(requestUri)}' is not in the workspace proxy allowlist.";

            var details = new ProxyErrorPage.Details
            {
                Method = string.IsNullOrWhiteSpace(method) ? null : method,
                RequestUri = requestUri,
                RawTarget = requestUri.AbsoluteUri,
                TechnicalMessage = rejectReason + " This https request cannot ride the loopback proxy's 403 page (Chromium tunnels https as CONNECT and discards a refused CONNECT's body), so the browser view answered the refusal at the view layer before any CONNECT was opened.",
                DecidedBy = ProxyErrorPage.DecidedByLocalListener,
            }.WithSuggestedAllowlistEntry();

            refusalHtml = ProxyErrorPage.RenderHtml(403, "Forbidden", ProxyErrorPage.CategoryNotAllowed, details);
            return true;
        }
    }
}
