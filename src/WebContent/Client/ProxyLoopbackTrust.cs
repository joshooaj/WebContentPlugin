using System;
using System.Security.Cryptography.X509Certificates;

namespace WebContent.Client
{
    /// <summary>
    /// Read-only trust posture for the deployment CA behind proxied https tunnels (#56,
    /// ADR-0003). The CA is trusted by a one-time elevated import into the machine's trusted-root
    /// store - never by this plugin: a non-elevated Smart Client must not (and crypt32 will not
    /// silently) write trust stores. When the CA is absent the tunnels still serve and the
    /// browser shows its own certificate error; the caller's log carries the exact import step.
    /// </summary>
    internal static class ProxyLoopbackTrust
    {
        /// <summary>
        /// True when a certificate with this thumbprint sits in a trusted-root store the browser
        /// consults: LocalMachine\Root first (the documented deployment path), CurrentUser\Root
        /// second (covers a legacy or manually per-user import).
        /// </summary>
        internal static bool IsRootTrusted(string thumbprint, Action<string> log)
        {
            if (string.IsNullOrEmpty(thumbprint))
            {
                return false;
            }

            return FindInRoot(thumbprint, StoreLocation.LocalMachine, log) || FindInRoot(thumbprint, StoreLocation.CurrentUser, log);
        }

        private static bool FindInRoot(string thumbprint, StoreLocation location, Action<string> log)
        {
            try
            {
                using (var store = new X509Store(StoreName.Root, location))
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
                }
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log($"WebContent proxy: reading the {location} trusted-root store failed: {exception.Message}");
                }

                return false;
            }
        }
    }
}
