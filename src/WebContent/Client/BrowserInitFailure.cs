using System;

namespace WebContent.Client
{
    /// <summary>
    /// #87 interim contract (ADR-0004 point 5): when a PROXIED browser's CoreWebView2
    /// initialization fails, the view shows an honest in-view ownership notice instead of a
    /// blank WebView, and the MIP log records one line naming the profile-ownership cause and
    /// the browser id. The failure shape: the boundary profile folder is deterministic per
    /// proxied boundary while every process starts its loopback listener on an ephemeral port
    /// and bakes that port into the browser arguments, and WebView2 refuses to initialize a
    /// shared user-data folder whose options differ from the process that started the browser
    /// process - so the second Smart Client session on one boundary fails exactly here.
    /// A NON-proxied init failure gets no presentation from this class: its error surface is
    /// unchanged (#87 out of scope), and enabling the second session to actually join is the
    /// separate enablement work, not this contract.
    ///
    /// Pure: no WebView2, no filesystem, no process - the classifier decision, the one log
    /// line, and the message choice (the shared <see cref="ProxyErrorPage"/> wording layer,
    /// the distinct CategoryProfileOwned cause) are unit-testable; the wiring guards pin the
    /// call sites.
    /// </summary>
    internal static class BrowserInitFailure
    {
        /// <summary>What the view presents and the log records for one failed initialization.</summary>
        internal sealed class Presentation
        {
            internal string Category { get; }
            internal string CauseLabel { get; }
            internal string Headline { get; }
            internal string Explanation { get; }

            /// <summary>The support-grade line under the friendly text: browser id + verbatim technical message.</summary>
            internal string Detail { get; }

            /// <summary>The ONE MIP log line for this failure: profile-ownership cause, browser id, technical message.</summary>
            internal string LogLine { get; }

            internal Presentation(string category, string causeLabel, string headline, string explanation, string detail, string logLine)
            {
                Category = category;
                CauseLabel = causeLabel;
                Headline = headline;
                Explanation = explanation;
                Detail = detail;
                LogLine = logLine;
            }
        }

        /// <summary>
        /// The classifier: a failed initialization on a PROXIED browser presents the profile-
        /// ownership contract; anything else returns false and the caller keeps its existing
        /// log-only surface. The owning process cannot be named (nothing is claimed on disk
        /// until the #95 join wiring), so the wording says "another session ... on this
        /// computer" honestly and points at the reload remedy.
        /// </summary>
        internal static bool TryPresent(bool proxied, Guid browserId, string initializationExceptionMessage, out Presentation presentation)
        {
            presentation = null;
            if (!proxied)
            {
                return false;
            }

            var category = ProxyErrorPage.CategoryProfileOwned;
            var technical = string.IsNullOrWhiteSpace(initializationExceptionMessage)
                ? "no initialization exception was reported"
                : initializationExceptionMessage.Trim();

            var logLine =
                $"CoreWebView2 initialization failed for proxied browser {browserId}: the most likely cause is profile " +
                "ownership - another Smart Client session on this machine already initialized this browser's boundary profile folder with its own loopback proxy options, and WebView2 refuses a shared user-data folder whose options differ. " +
                $"InitializationException: {technical}. " +
                "The view shows an in-view ownership notice instead of a blank browser; Refresh (or re-selecting the view) re-attempts creation through the WebView re-creation path, which succeeds once the owning session exits.";

            presentation = new Presentation(
                category,
                ProxyErrorPage.CauseLabel(category),
                ProxyErrorPage.Headline(category),
                ProxyErrorPage.Explanation(category),
                $"Browser {browserId:D}. {technical}",
                logLine);
            return true;
        }
    }
}
