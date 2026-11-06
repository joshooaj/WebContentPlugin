namespace WebContent
{
    /// <summary>
    /// Compile-time stand-in for the auto-generated <c>WebContent.Strings</c> resource
    /// class. The test project links pure production files (<c>WebContentConfiguration.cs</c>,
    /// <c>WorkspaceConfiguration.cs</c>, <c>ProxyCaExportView.cs</c>) that reference resource
    /// members, but it does not embed the .resx. Tests never read these values — where a test
    /// cares about wording it reads <c>Strings.resx</c> directly — so they only exist to
    /// satisfy the compiler. Keep the set aligned with what LINKED files use.
    /// </summary>
    internal static class Strings
    {
        public const string HomeAddress = "https://github.com/joshooaj/WebContentPlugin";

        public const string InvalidAddressMessage = "\"{0}\" is not a valid address. Enter a host name (example.com), a host and port (server:8443), or a full http/https URL.";

        // Used by the linked ProxyCaExportView.cs (ProxyCaExportFailureSentence). The wording
        // itself is pinned against Strings.resx by ProxyCaExportTests.
        public const string ProxyCaExportMissing = "No deployment CA is configured.";

        public const string ProxyCaExportNotConnected = "The proxy fetch service is not connected.";

        public const string ProxyCaExportFailedFormat = "Exporting the deployment CA failed ({0}): {1}";
    }
}
