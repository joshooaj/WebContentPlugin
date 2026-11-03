using System;
using System.Net;
using System.Text;

namespace WebContent
{
    /// <summary>
    /// Ticket #66: the friendly refusal page a proxied view renders when the proxy refuses a
    /// request. The wording layer is separated from the plumbing: every distinct refusal cause
    /// carries an explicit cause category (mapped 1:1 onto <see cref="ProxyFetchErrorCodes"/> and
    /// the <see cref="ProxyForwardPolicy"/> refusal dispositions — never parsed out of prose),
    /// and the page presents a friendly headline + that one cause category while the technical
    /// detail (exact origin, the canonical allowlist entry that would admit it, the byte cap,
    /// the verbatim refusal text, the timestamp) sits collapsed behind an expandable section
    /// with a "Copy diagnostics" button. The #58 posture is preserved verbatim: each page names
    /// WHICH side failed (this computer, the VMS server, or the target behind it).
    ///
    /// Fail-closed is unchanged: this class only decorates an already-decided refusal; it never
    /// participates in the decision, and nothing on the page alters what the proxy refuses.
    /// </summary>
    internal static class ProxyErrorPage
    {
        // The distinct cause categories. Each maps 1:1 onto one wire failure code or one
        // forwarder refusal posture; distinct causes are never collapsed into one generic page
        // (pinned by ProxyErrorPageTests over every ProxyFetchErrorCodes value).
        internal const string CategoryNotAllowed = "not-allowed-by-administrator";
        internal const string CategoryResponseTooLarge = "response-too-large";
        internal const string CategoryRequestTooLarge = "request-too-large";
        internal const string CategoryTargetUnreachable = "target-unreachable";
        internal const string CategoryTargetTimeout = "target-timeout";
        internal const string CategoryCertificateUntrusted = "certificate-not-trusted";
        internal const string CategoryAuthorityUnavailable = "proxy-certificate-authority-unavailable";
        internal const string CategoryTunnelFailed = "proxy-tunnel-failure";
        internal const string CategoryComponentUnavailable = "proxy-component-unavailable";
        internal const string CategoryMalformedRequest = "malformed-request";
        internal const string CategoryMethodNotAllowed = "method-not-allowed";
        internal const string CategoryUnknownFailure = "proxy-failure";

        // The side attribution lines (#58: every page names which side failed).
        internal const string DecidedByLocalListener = "this computer (the workspace browser proxy)";
        internal const string DecidedByVmsServer = "the VMS Event Server (the server-side proxy component)";
        internal const string DecidedByLocalAboutVms = "this computer (the VMS proxy component never answered)";

        /// <summary>
        /// The 1:1 map from a wire failure code onto a cause category. Every code names its own
        /// category; codes that are not per-request proxy refusals (unknown, null) map to
        /// nothing and the caller falls back to <see cref="CategoryUnknownFailure"/>.
        /// </summary>
        internal static bool TryCategoryForErrorCode(string errorCode, out string category)
        {
            switch (errorCode)
            {
                case ProxyFetchErrorCodes.NotAllowed:
                    category = CategoryNotAllowed;
                    return true;
                case ProxyFetchErrorCodes.TooLarge:
                    category = CategoryResponseTooLarge;
                    return true;
                case ProxyFetchErrorCodes.Unreachable:
                    category = CategoryTargetUnreachable;
                    return true;
                case ProxyFetchErrorCodes.Timeout:
                    category = CategoryTargetTimeout;
                    return true;
                case ProxyFetchErrorCodes.TlsValidation:
                    category = CategoryCertificateUntrusted;
                    return true;
                case ProxyFetchErrorCodes.NoAuthority:
                    category = CategoryAuthorityUnavailable;
                    return true;
                case ProxyFetchErrorCodes.HandshakeRefused:
                    category = CategoryTunnelFailed;
                    return true;
                case ProxyFetchErrorCodes.ServerUnavailable:
                    category = CategoryComponentUnavailable;
                    return true;
                case ProxyFetchErrorCodes.BadRequest:
                    category = CategoryMalformedRequest;
                    return true;
                case ProxyFetchErrorCodes.Error:
                    category = CategoryUnknownFailure;
                    return true;
                default:
                    category = null;
                    return false;
            }
        }

        /// <summary>The short cause-category chip shown under the headline.</summary>
        internal static string CauseLabel(string category)
        {
            switch (Normalize(category))
            {
                case CategoryNotAllowed: return "Not allowed by the administrator";
                case CategoryResponseTooLarge: return "Response too large (proxy size cap)";
                case CategoryRequestTooLarge: return "Request too large (proxy size cap)";
                case CategoryTargetUnreachable: return "Target unreachable";
                case CategoryTargetTimeout: return "Timeout";
                case CategoryCertificateUntrusted: return "Certificate not trusted";
                case CategoryAuthorityUnavailable: return "Proxy certificate authority unavailable";
                case CategoryTunnelFailed: return "Proxy tunnel failure";
                case CategoryComponentUnavailable: return "VMS proxy component unavailable";
                case CategoryMalformedRequest: return "Invalid request";
                case CategoryMethodNotAllowed: return "Request method not allowed";
                default: return "Proxy failure";
            }
        }

        /// <summary>The friendly headline an ordinary user reads first.</summary>
        internal static string Headline(string category)
        {
            switch (Normalize(category))
            {
                case CategoryNotAllowed: return "This web address is not allowed for this view";
                case CategoryResponseTooLarge: return "The page is larger than the proxy allows";
                case CategoryRequestTooLarge: return "The data you submitted is larger than the proxy allows";
                case CategoryTargetUnreachable: return "The web address could not be reached";
                case CategoryTargetTimeout: return "The web server took too long to answer";
                case CategoryCertificateUntrusted: return "The web server's certificate could not be trusted";
                case CategoryAuthorityUnavailable: return "The proxy is not ready to open secure connections";
                case CategoryTunnelFailed: return "The proxy could not open the connection for this page";
                case CategoryComponentUnavailable: return "The VMS proxy is not running";
                case CategoryMalformedRequest: return "The browser's request could not be understood";
                case CategoryMethodNotAllowed: return "The proxy does not relay this kind of request";
                default: return "The proxy reported a failure";
            }
        }

        /// <summary>
        /// The friendly one-paragraph explanation. The #58 side-attribution fragments the live
        /// rows and admin muscle memory depend on ("VMS server could not reach the target",
        /// "VMS server could not get an answer from the target", "VMS server proxy component is
        /// not available", "never loaded direct from this computer", "exceeded the VMS proxy
        /// response size cap") are carried here deliberately — friendlier wording on top, the
        /// actionable sentence unchanged.
        /// </summary>
        internal static string Explanation(string category)
        {
            switch (Normalize(category))
            {
                case CategoryNotAllowed:
                    return "The workspace proxy only loads web addresses the administrator put on its allowlist, and this one is not on that list. Nothing was loaded outside the proxy.";
                case CategoryResponseTooLarge:
                    return "The target's response exceeded the VMS proxy response size cap and was not relayed. The size cap can be raised per workspace in the Management Client (Proxy tab) up to the protocol ceiling.";
                case CategoryRequestTooLarge:
                    return "The request body exceeded the proxy's request size cap, so nothing was sent on to the target.";
                case CategoryTargetUnreachable:
                    return "The VMS server could not reach the target (connection refused, DNS, or network failure at the VMS server, not at this computer).";
                case CategoryTargetTimeout:
                    return "The VMS server could not get an answer from the target in time. The messaging channel to the VMS server worked; giving up happened on the target leg.";
                case CategoryCertificateUntrusted:
                    return "The VMS server refused the target's TLS certificate because it does not chain to a trusted root there. Whether this target's certificate should be accepted is an administrator's decision, made on the target's allowlist entry.";
                case CategoryAuthorityUnavailable:
                    return "The VMS Event Server could not provide its proxy certificate authority, so the proxy cannot open encrypted connections to this target. An administrator must provision the proxy certificate on the Event Server.";
                case CategoryTunnelFailed:
                    return "The proxy connection this page depends on (the secure-page tunnel or the websocket tunnel) failed to open or was refused before any page data crossed.";
                case CategoryComponentUnavailable:
                    return "The VMS server proxy component is not available. A proxied workspace is never loaded direct from this computer - make sure the Web Content plugin is deployed to the XProtect Event Server and the Event Server service is running.";
                case CategoryMalformedRequest:
                    return "The request that reached the proxy was malformed, so the proxy refused it before deciding anything else.";
                case CategoryMethodNotAllowed:
                    return "The workspace proxy only relays the verbs a browser page generates (GET, HEAD, POST, PUT, DELETE, OPTIONS, PATCH).";
                default:
                    return "The proxy refused this request for a reason that is not one of the named causes. The technical details below name it.";
            }
        }

        /// <summary>
        /// Render the html body for one refusal. The HTTP framing stays in
        /// <see cref="ProxyForwardCodec.RenderErrorResponse"/>; this produces only the document.
        /// </summary>
        internal static string RenderHtml(int statusCode, string reasonPhrase, string category, Details details)
        {
            var safeCategory = Normalize(category);
            var diagnostics = BuildDiagnostics(statusCode, reasonPhrase, safeCategory, details);
            var safeDiagnostics = WebUtility.HtmlEncode(diagnostics);

            var builder = new StringBuilder();
            builder.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            builder.Append($"<title>Web Content proxy: {statusCode}</title>");
            builder.Append("<style>");
            builder.Append("body{margin:0;padding:24px;background:#f3f3f3;color:#1b1b1b;font-family:'Segoe UI',system-ui,sans-serif;font-size:15px;line-height:1.45}");
            builder.Append("main{max-width:640px;margin:0 auto;background:#fff;border:1px solid #d6d6d6;border-radius:8px;padding:28px 32px}");
            builder.Append("h1{margin:8px 0 12px;font-size:22px}");
            builder.Append(".cause{display:inline-block;padding:3px 10px;border-radius:12px;background:#fdf1e7;border:1px solid #e2b98a;color:#6c3c00;font-size:13px;margin:0}");
            builder.Append("p{margin:0 0 12px}");
            builder.Append(".fineprint{color:#5a5a5a;font-size:13px}");
            builder.Append("details{margin-top:18px;border-top:1px solid #e3e3e3;padding-top:12px}");
            builder.Append("summary{cursor:pointer;color:#3a6ea5;font-size:14px}");
            builder.Append("pre{background:#f7f7f7;border:1px solid #e0e0e0;border-radius:4px;padding:12px;white-space:pre-wrap;word-break:break-word;font-size:12.5px}");
            builder.Append("button{padding:5px 12px;border-radius:4px;border:1px solid #b8b8b8;background:#fff;cursor:pointer;font-size:13px}");
            builder.Append("button:hover{background:#f0f0f0}");
            builder.Append("#wdc-copy-note{color:#5a5a5a;font-size:12.5px;margin-left:8px}");
            builder.Append("@media (prefers-color-scheme:dark){body{background:#1f1f1f;color:#e8e8e8}main{background:#2b2b2b;border-color:#3d3d3d}.cause{background:#3a2d1e;border-color:#7a5a33;color:#f0c98a}.fineprint,#wdc-copy-note{color:#a8a8a8}pre{background:#252525;border-color:#3d3d3d}button{background:#333;border-color:#555;color:#e8e8e8}button:hover{background:#3d3d3d}}");
            builder.Append("</style></head><body><main>");

            builder.Append($"<p class=\"cause\">{WebUtility.HtmlEncode(CauseLabel(safeCategory))}</p>");
            builder.Append($"<h1>{WebUtility.HtmlEncode(Headline(safeCategory))}</h1>");
            builder.Append($"<p>{WebUtility.HtmlEncode(Explanation(safeCategory))}</p>");

            if (details != null && !string.IsNullOrWhiteSpace(details.Note))
            {
                builder.Append($"<p>{WebUtility.HtmlEncode(details.Note)}</p>");
            }

            builder.Append("<p class=\"fineprint\">This page was produced by the XProtect Web Content plugin proxy. Nothing was loaded outside the proxy.</p>");

            builder.Append("<details><summary>Technical details for administrators and support</summary>");
            builder.Append($"<pre id=\"wdc-diagnostics\">{safeDiagnostics}</pre>");
            builder.Append("<button id=\"wdc-copy\" type=\"button\">Copy diagnostics</button><span id=\"wdc-copy-note\"></span>");
            builder.Append("</details>");

            builder.Append("</main><script>");
            builder.Append("(function(){var button=document.getElementById('wdc-copy');if(!button){return;}");
            builder.Append("button.addEventListener('click',function(){");
            builder.Append("var pre=document.getElementById('wdc-diagnostics');var note=document.getElementById('wdc-copy-note');");
            builder.Append("function show(ok){note.textContent=ok?'Copied.':'Copy failed - select the text and press Ctrl+C.';}");
            builder.Append("function fallback(){try{var range=document.createRange();range.selectNodeContents(pre);");
            builder.Append("var selection=window.getSelection();selection.removeAllRanges();selection.addRange(range);");
            builder.Append("show(document.execCommand('copy'));}catch(e){show(false);}}");
            builder.Append("if(navigator.clipboard&&navigator.clipboard.writeText){");
            builder.Append("navigator.clipboard.writeText(pre.textContent).then(function(){show(true);},function(){fallback();});");
            builder.Append("}else{fallback();}});})();");
            builder.Append("</script></body></html>");

            return builder.ToString();
        }

        /// <summary>
        /// The plain-text diagnostics block: both what the collapsed section shows and what the
        /// "Copy diagnostics" button copies. Exact origin, method, the canonical allowlist entry
        /// that would admit it, byte cap, verbatim technical message, timestamp, machine.
        /// </summary>
        private static string BuildDiagnostics(int statusCode, string reasonPhrase, string category, Details details)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Web Content proxy diagnostics (status {statusCode} {reasonPhrase})");
            builder.AppendLine($"  When (UTC):        {DateTime.UtcNow.ToString("u")}");
            builder.AppendLine($"  Cause category:    {category} - {CauseLabel(category)}");

            if (details != null)
            {
                if (!string.IsNullOrWhiteSpace(details.DecidedBy))
                {
                    builder.AppendLine($"  Decided by:        {details.DecidedBy}");
                }

                var request = string.IsNullOrWhiteSpace(details.Method)
                    ? (details.RequestUri != null ? details.RequestUri.AbsoluteUri : details.RawTarget)
                    : $"{details.Method} {(details.RequestUri != null ? details.RequestUri.AbsoluteUri : details.RawTarget)}";
                if (!string.IsNullOrWhiteSpace(request))
                {
                    builder.AppendLine($"  Request:           {request}");
                }

                if (details.RequestUri != null)
                {
                    builder.AppendLine($"  Target origin:     {ProxyPolicy.CanonicalOrigin(details.RequestUri)}");
                }

                if (!string.IsNullOrWhiteSpace(details.SuggestedAllowlistEntry))
                {
                    builder.AppendLine($"  Allowlist entry that would admit this origin: {details.SuggestedAllowlistEntry}");
                }

                if (details.ByteCap.HasValue)
                {
                    builder.AppendLine($"  Proxy byte cap:    {details.ByteCap.Value}");
                }

                if (!string.IsNullOrWhiteSpace(details.Note))
                {
                    builder.AppendLine($"  Context:           {details.Note}");
                }

                if (!string.IsNullOrWhiteSpace(details.TechnicalMessage))
                {
                    builder.AppendLine($"  Technical message: {details.TechnicalMessage}");
                }
            }

            builder.Append($"  Client machine:    {Environment.MachineName}");
            return builder.ToString();
        }

        private static string Normalize(string category)
        {
            return string.IsNullOrWhiteSpace(category) ? CategoryUnknownFailure : category;
        }

        /// <summary>
        /// The factual context a refusal page carries: what was requested, which side decided,
        /// and the actionable numbers an admin needs (canonical allowlist entry, byte cap) plus
        /// the verbatim technical message. All fields optional - a page with no details still
        /// renders its cause category cleanly.
        /// </summary>
        internal sealed class Details
        {
            internal string Method;
            internal Uri RequestUri;
            internal string RawTarget;
            internal string DecidedBy;
            internal string Note;
            internal string SuggestedAllowlistEntry;
            internal long? ByteCap;
            internal string TechnicalMessage;

            /// <summary>
            /// Seed from the parsed request plus the refusal's verbatim technical message and the
            /// deciding side - the three things every refusal page must carry.
            /// </summary>
            internal static Details ForRequest(ProxyForwardedRequest request, string technicalMessage, string decidedBy)
            {
                return new Details
                {
                    Method = request == null ? null : request.Method,
                    RequestUri = request == null ? null : request.Uri,
                    RawTarget = request == null ? null : request.RawTarget,
                    TechnicalMessage = technicalMessage,
                    DecidedBy = decidedBy,
                };
            }

            /// <summary>The canonical exact entry an admin could add to admit this origin.</summary>
            internal Details WithSuggestedAllowlistEntry()
            {
                if (RequestUri != null && ProxyPolicy.IsHttpUri(RequestUri))
                {
                    SuggestedAllowlistEntry = ProxyPolicy.CanonicalOrigin(RequestUri);
                }

                return this;
            }
        }
    }
}
