using System;

namespace WebContent.Admin
{
    /// <summary>
    /// Ticket #75: the Management Client's "Export proxy certificate" button decision as one
    /// pure value. The page only saves bytes when <see cref="ExportAvailable"/> is true and
    /// renders <see cref="FailureCode"/> otherwise — so a missing or unreadable CA never
    /// produces the empty .cer file the ticket names as the failure to avoid. All logic lives
    /// here, unit tested without WPF or MIP.
    /// </summary>
    internal sealed class ProxyCaExportView
    {
        /// <summary>True only when a real, decodable certificate DER came back.</summary>
        public bool ExportAvailable { get; set; }

        /// <summary>
        /// Which failure the page must name (a <see cref="ProxyFetchErrorCodes"/> value): an
        /// absent/expired CA reads differently from an unanswered pull, which reads differently
        /// from a store fault.
        /// </summary>
        public string FailureCode { get; set; }

        /// <summary>The certificate DER to Save As; null unless <see cref="ExportAvailable"/>.</summary>
        public byte[] DerBytes { get; set; }

        public string Thumbprint { get; set; }

        public DateTime NotBeforeUtc { get; set; }

        public DateTime NotAfterUtc { get; set; }
    }

    /// <summary>
    /// Ticket #75: response payload → view transform. Pure and defensive: a "successful" frame
    /// whose DER is blank, un-base64-able, or empty decodes to a failure, never to a zero-byte
    /// file on the admin's disk.
    /// </summary>
    internal static class ProxyCaExportViewBuilder
    {
        internal static ProxyCaExportView Build(ProxyCaExportResponsePayload payload)
        {
            var view = new ProxyCaExportView();
            if (payload == null || !payload.Success)
            {
                view.ExportAvailable = false;
                view.FailureCode = payload == null || string.IsNullOrWhiteSpace(payload.ErrorCode)
                    ? ProxyFetchErrorCodes.ServerUnavailable
                    : payload.ErrorCode;
                return view;
            }

            byte[] der;
            try
            {
                der = string.IsNullOrEmpty(payload.AuthorityDerBase64)
                    ? null
                    : Convert.FromBase64String(payload.AuthorityDerBase64);
            }
            catch (FormatException)
            {
                der = null;
            }

            if (der == null || der.Length == 0)
            {
                // A success frame with no usable certificate is a broken answer, not a licence
                // to write an empty file.
                view.ExportAvailable = false;
                view.FailureCode = ProxyFetchErrorCodes.Error;
                return view;
            }

            view.ExportAvailable = true;
            view.DerBytes = der;
            view.Thumbprint = payload.AuthorityThumbprint;
            view.NotBeforeUtc = payload.NotBeforeUtc;
            view.NotAfterUtc = payload.NotAfterUtc;
            return view;
        }
    }
}
