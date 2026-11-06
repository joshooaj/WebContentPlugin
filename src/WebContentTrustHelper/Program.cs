using System;
using System.Security.Cryptography.X509Certificates;

namespace WebContent.TrustHelper
{
    /// <summary>
    /// Ticket #72: the single entry point of the bundled elevated trust helper
    /// (WebContentTrustHelper.exe). The Smart Client plugin starts this ONLY through the runas
    /// verb, so its process is already elevated (UAC consent shown) and crypt32's own modal
    /// trusted-root warning rides the interactive <c>store.Add</c>. Both dialogs are deliberate
    /// and unsuppressed (the ticket's accepted UX cost); this process never suppresses either,
    /// never reads the "yes" checkbox itself, and imports exactly one thumbprint-verified DER.
    ///
    /// It is a separate exe precisely because the Smart Client host is an ordinary user process:
    /// a non-elevated plugin must not (and crypt32 will not silently) write trust stores. This
    /// file is NOT compiled into the test project; the auditable decisions live in
    /// <see cref="TrustHelperCore"/>, which the tests link and pin.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (!TrustHelperCore.TryParseArguments(args, out var derFilePath, out var expectedThumbprint, out var error))
            {
                Console.Error.WriteLine("WebContent proxy CA trust helper: " + error);
                return TrustHelperCore.ExitUsage;
            }

            Console.WriteLine("WebContent proxy CA trust helper: importing the deployment CA into LocalMachine\\Root (elevated, crypt32's warning will prompt).");
            try
            {
                return TrustHelperCore.Run(derFilePath, expectedThumbprint, ImportIntoLocalMachineRoot, Console.Out);
            }
            catch (Exception exception)
            {
                // Belt-and-braces: an unexpected fault must still exit loudly, never partially
                // trust anything. The plugin re-checks the root stores itself regardless.
                Console.Error.WriteLine("WebContent proxy CA trust helper: unexpected failure: " + exception.Message);
                return TrustHelperCore.ExitImportFailed;
            }
        }

        /// <summary>
        /// The one store write this program can perform. A non-elevated process fails on the
        /// ReadWrite open (<see cref="TrustHelperCore.ExitStoreUnavailable"/>), which is the
        /// negative-path shape the live probe pins unattended; the interactive success path is a
        /// human step (two OS dialogs, no unattended automation).
        /// </summary>
        private static int ImportIntoLocalMachineRoot(X509Certificate2 certificate)
        {
            var opened = false;
            try
            {
                using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    opened = true;
                    var already = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
                    if (already.Count > 0)
                    {
                        Console.WriteLine("already trusted: the certificate is present in LocalMachine\\Root; nothing changed.");
                        return TrustHelperCore.ExitOk;
                    }

                    store.Add(certificate);
                    Console.WriteLine("imported: certificate " + certificate.Thumbprint + " is now in LocalMachine\\Root.");
                    return TrustHelperCore.ExitOk;
                }
            }
            catch (Exception exception)
            {
                // Access denied without elevation -> the store was never even openable
                // (ExitStoreUnavailable, the negative-path shape the live probe pins unattended);
                // a failure after opening means crypt32's warning was declined or the write itself
                // failed (ExitImportFailed). Either way: loud, non-zero, and the plugin's own
                // re-check confirms the store state regardless of what this process claims.
                Console.Error.WriteLine(opened
                    ? "trusted-root write failed (was the crypt32 root-store warning declined?): " + exception.Message
                    : "cannot open LocalMachine\\Root for write (not elevated?): " + exception.Message);
                return opened ? TrustHelperCore.ExitImportFailed : TrustHelperCore.ExitStoreUnavailable;
            }
        }
    }
}
