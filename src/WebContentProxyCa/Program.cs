using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32;

namespace WebContent.ProxyCa
{
    /// <summary>
    /// Ticket #77 entry point: run ProxyCaCore against the real SCM, the installed
    /// plugin assembly, and the real key storage. The MSI streams this exe as a deferred
    /// custom action, so it already runs as SYSTEM with machine-store privilege; it never
    /// elevates or self-checks (same posture as the #82 service helper).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return ProxyCaCore.Run(
                    args,
                    new ServiceControlManagerAccountReader(),
                    new PluginAuthorityGateway(),
                    new MachineKeyReadGrant(),
                    Console.WriteLine);
            }
            catch (Exception ex)
            {
                // Anything that escapes the seams (registry hiccups, store errors) must not
                // silently look like success; Return="check" turns the exit into a rollback.
                Console.Error.WriteLine("deployment proxy CA provisioning failed: " + ex.Message);
                return ProxyCaCore.ExitProvisionFailed;
            }
        }
    }

    /// <summary>
    /// The SCM registry view of a service's logon account (ObjectName). The 64-bit view
    /// is explicit: the service key lives under the real SYSTEM hive regardless of process bitness.
    /// </summary>
    internal sealed class ServiceControlManagerAccountReader : IServiceAccountReader
    {
        public string ReadObjectName(string serviceName)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var serviceKey = baseKey.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + serviceName, writable: false))
            {
                if (serviceKey == null)
                {
                    return null;
                }

                return serviceKey.GetValue("ObjectName") as string;
            }
        }
    }

    /// <summary>
    /// Drives the plugin's own ADR-0003 load-or-create through reflection into the just
    /// installed WebContent.dll - the identical posture the legacy provision script uses,
    /// which is deliberate: the CA the MSI creates is produced by the exact code path the
    /// Event Server plugin itself would use (fixed container, fixed CN, DER minting), so
    /// there is no second certificate recipe to drift. LoadFrom, never an assembly
    /// reference: the MIP SDK types WebContent.dll also references are never enumerated
    /// or linked, only the crypto types are touched.
    /// </summary>
    internal sealed class PluginAuthorityGateway : IAuthorityGateway
    {
        public void Provision(string pluginDllPath, Action<string> log)
        {
            if (!File.Exists(pluginDllPath))
            {
                throw new FileNotFoundException(
                    "plugin assembly not found - the CA action runs after InstallFiles, a missing drop means the install is already broken: " + pluginDllPath,
                    pluginDllPath);
            }

            var assembly = Assembly.LoadFrom(pluginDllPath);

            // The plugin's minting backend is internal; the script reached it the same way.
            var authorityType = assembly.GetType("WebContent.Authority");
            if (authorityType == null)
            {
                throw new InvalidOperationException(
                    "WebContent.Authority is missing from " + pluginDllPath + " - the CA helper and the plugin moved apart; update src/WebContentProxyCa together with the plugin.");
            }

            var loadOrCreate = authorityType.GetMethod(
                "LoadOrCreateDeploymentAuthority",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (loadOrCreate == null)
            {
                throw new InvalidOperationException(
                    "LoadOrCreateDeploymentAuthority is missing from WebContent.Authority - the CA helper and the plugin moved apart; update src/WebContentProxyCa together with the plugin.");
            }

            var authority = loadOrCreate.Invoke(null, new object[] { log });
            if (authority == null)
            {
                throw new InvalidOperationException("LoadOrCreateDeploymentAuthority returned null - refusing to claim success.");
            }

            try
            {
                var certificateProperty = authorityType.GetProperty(
                    "Certificate",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var certificate = certificateProperty == null
                    ? null
                    : certificateProperty.GetValue(authority) as X509Certificate2;
                if (certificate != null)
                {
                    log("authority certificate: " + certificate.Subject + " (thumbprint " + certificate.Thumbprint + ")");
                }
            }
            finally
            {
                // Releases this process's key handles; the container itself persists
                // (PersistKeyInCsp), so the Event Server loads the same CA on first mint.
                var disposable = authority as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Grants (idempotently) the Event Server's logon account Read on the CA private key.
    /// The key file is located through the certificate's own CAPI container handle
    /// (CspKeyContainerInfo.UniqueKeyContainerName is the MachineKeys file name) - the
    /// hash-of-container-name guess the legacy script carried is exactly why that script
    /// needed a creation-time-window fallback; the handle is deterministic instead.
    /// </summary>
    internal sealed class MachineKeyReadGrant : IPrivateKeyAccessGrant
    {
        public bool EnsureServiceAccountCanReadKey(string account, Action<string> log)
        {
            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                // Same find posture as the plugin's loader: fixed CN, unverified, newest
                // valid copy whose private key this minting path can use (CAPI RSA).
                X509Certificate2 chosen = null;
                RSACryptoServiceProvider chosenKey = null;
                var now = DateTime.Now;
                foreach (var certificate in store.Certificates.Find(
                    X509FindType.FindBySubjectName, ProxyCaCore.AuthorityCommonName, false))
                {
                    if (certificate.NotAfter <= now || !certificate.HasPrivateKey)
                    {
                        certificate.Dispose();
                        continue;
                    }

                    RSACryptoServiceProvider key;
                    try
                    {
                        key = certificate.PrivateKey as RSACryptoServiceProvider;
                    }
                    catch (CryptographicException)
                    {
                        certificate.Dispose();
                        continue;
                    }

                    if (key == null)
                    {
                        certificate.Dispose();
                        continue;
                    }

                    if (chosen == null || certificate.NotAfter > chosen.NotAfter)
                    {
                        if (chosen != null)
                        {
                            chosen.Dispose();
                        }

                        chosen = certificate;
                        chosenKey = key;
                        continue;
                    }

                    certificate.Dispose();
                }

                if (chosen == null)
                {
                    log("no valid deployment proxy CA with a usable private key is present in LocalMachine\\Personal - " +
                        "the load-or-create step should have made one; refusing to claim the grant succeeded.");
                    return false;
                }

                using (chosen)
                using (chosenKey)
                {
                    string containerName;
                    try
                    {
                        containerName = chosenKey.CspKeyContainerInfo.UniqueKeyContainerName;
                    }
                    catch (CryptographicException exception)
                    {
                        log("the CA private key's container could not be opened: " + exception.Message);
                        return false;
                    }

                    var keyDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        @"Microsoft\Crypto\RSA\MachineKeys");
                    var keyFilePath = Path.Combine(keyDirectory, containerName);
                    if (!File.Exists(keyFilePath))
                    {
                        log("the CA key file '" + containerName + "' is not under " + keyDirectory + " - cannot grant access.");
                        return false;
                    }

                    var security = File.GetAccessControl(keyFilePath);
                    var accountSid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
                    var alreadyGranted = false;
                    foreach (var entry in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    {
                        var rule = entry as FileSystemAccessRule;
                        if (rule != null &&
                            rule.AccessControlType == AccessControlType.Allow &&
                            rule.IdentityReference.Equals(accountSid) &&
                            (rule.FileSystemRights & FileSystemRights.ReadData) == FileSystemRights.ReadData)
                        {
                            alreadyGranted = true;
                            break;
                        }
                    }

                    if (alreadyGranted)
                    {
                        log("service account " + account + " already holds read access to the CA private key " +
                            "(key file " + containerName + ") - DACL untouched.");
                        return true;
                    }

                    security.AddAccessRule(new FileSystemAccessRule(
                        accountSid, FileSystemRights.Read, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                    File.SetAccessControl(keyFilePath, security);
                    log("granted Read on the CA private key (key file " + containerName + ") to " + account + ".");
                    return true;
                }
            }
        }
    }
}
