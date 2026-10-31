using System;
using System.Security.Cryptography.X509Certificates;
using VideoOS.Platform;

namespace WebContent.Client
{
    /// <summary>
    /// Trusted-root store management for the session CA behind proxied https tunnels (#56,
    /// ADR-0002). The CA - and therefore every leaf the loopback listener presents - is trusted
    /// only while the proxied browser lives: installed when a listener with https targets starts,
    /// removed when it stops. Nothing about it survives the browser session.
    ///
    /// Windows shows its root-store consent prompt for programmatic installs into the user's
    /// trusted roots; a declined prompt is a logged failure, not a crash - the browser then shows
    /// its own certificate warning (still fail-closed), which is the acceptable degraded path.
    /// </summary>
    internal static class ProxyLoopbackTrust
    {
        internal static bool Install(X509Certificate2 authorityCertificate)
        {
            if (authorityCertificate == null)
            {
                return false;
            }

            try
            {
                using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    var existing = store.Certificates.Find(
                        X509FindType.FindByThumbprint, authorityCertificate.Thumbprint, false);
                    if (existing.Count > 0)
                    {
                        return true;
                    }

                    store.Add(authorityCertificate);
                }

                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyLoopbackTrust),
                    $"Loopback proxy session CA '{authorityCertificate.Subject}' installed in the user's trusted roots for this browser session.");
                return true;
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyLoopbackTrust),
                    $"Loopback proxy session CA install failed ({exception.Message}); https tunnels will show a browser certificate warning until it succeeds.");
                return false;
            }
        }

        internal static void Remove(X509Certificate2 authorityCertificate)
        {
            if (authorityCertificate == null)
            {
                return;
            }

            try
            {
                using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    var found = store.Certificates.Find(
                        X509FindType.FindByThumbprint, authorityCertificate.Thumbprint, false);
                    foreach (var certificate in found)
                    {
                        store.Remove(certificate);
                    }
                }

                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyLoopbackTrust),
                    $"Loopback proxy session CA '{authorityCertificate.Subject}' removed from the user's trusted roots.");
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyLoopbackTrust),
                    $"Loopback proxy session CA removal failed ({exception.Message}).");
            }
        }
    }
}

