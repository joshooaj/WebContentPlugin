using System;

namespace WebContent.ProxyCa
{
    /// <summary>
    /// Ticket #77: the auditable decision core of the MSI's deployment-CA provisioning
    /// helper (ADR-0003 as amended by #64/#77). The MSI schedules this exe as a deferred
    /// custom action (runs as SYSTEM) when the Event Server service exists on the machine
    /// AND the admin ticked the proxy opt-in on the install options page. The core then,
    /// in order:
    ///
    /// 1. drives the plugin's OWN load-or-create path (via the injected gateway) so the
    ///    CA written into LocalMachine\Personal is byte-for-byte what the Event Server
    ///    plugin later loads by - fixed common name, fixed machine key container, reuse
    ///    of an existing valid CA untouched; and
    /// 2. grants the Event Server service's own logon account read access to the CA
    ///    private key (same grant the legacy provision script makes), because a CA
    ///    created under SYSTEM is otherwise unreadable by the restricted service account
    ///    and every leaf mint would die with Access is denied.
    ///
    /// Exit codes (the custom action is Return="check", so non-zero fails the install):
    ///   0 = CA loaded-or-created and the service-account key grant verified/applied,
    ///   1 = usage error (nothing was touched),
    ///   2 = provisioning or the key grant failed - loud by design: an opted-in proxy
    ///       without a usable CA is the epic's "failure is loud, explained, fixable"
    ///       posture, so the install rolls back rather than shipping a half-set-up
    ///       deployment.
    /// </summary>
    public static class ProxyCaCore
    {
        public const int ExitDone = 0;
        public const int ExitUsage = 1;
        public const int ExitProvisionFailed = 2;

        /// <summary>
        /// Fixed drop location the MSI installs to (INSTALLFOLDER is pinned by the setup
        /// harvest, AGENTS.md) - and the reflection target for the plugin's load-or-create.
        /// A deferred exe custom action cannot read installer properties, so the constant
        /// is deliberate, exactly like the legacy script's default parameter.
        /// </summary>
        public const string DefaultPluginDllPath =
            @"C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin\WebContent.dll";

        /// <summary>
        /// The deployment authority CN. Kept equal to ProxyLoopbackTls.AuthorityCommonName
        /// (pinned by InstallerProxyCaGuardTests); used only to FIND the CA for the key
        /// grant - creation itself runs through the plugin's own code, which owns the name.
        /// </summary>
        public const string AuthorityCommonName = "XProtect WebContent Plugin Loopback Proxy";

        public const string Usage =
            "usage: WebContentProxyCa provision <service-name> [plugin-dll-path]  " +
            "(loads or creates the deployment proxy CA through the plugin's own code path, " +
            "then grants the service's logon account read access to its private key)";

        /// <summary>
        /// Validates the argument vector before anything is touched: the "provision" verb,
        /// a non-blank service name with no registry-path characters, and an optional
        /// non-blank plugin path override (the MSI always runs without the override, using
        /// the fixed drop; manual/legacy runs may pass one).
        /// </summary>
        public static bool TryParse(string[] args, out string serviceName, out string pluginDllPath)
        {
            serviceName = null;
            pluginDllPath = null;
            if (args == null || (args.Length != 2 && args.Length != 3))
            {
                return false;
            }

            if (!string.Equals((args[0] ?? string.Empty).Trim(), "provision", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            serviceName = (args[1] ?? string.Empty).Trim();
            if (serviceName.Length == 0 ||
                serviceName.IndexOfAny(new[] { '\\', '/', '=', ':' }) >= 0 ||
                serviceName.IndexOfAny(new[] { '\0' }) >= 0)
            {
                // The name flows into a registry subkey path; refuse anything that could
                // escape it. The MSI's authored argument always passes.
                return false;
            }

            if (args.Length == 3)
            {
                pluginDllPath = (args[2] ?? string.Empty).Trim();
                if (pluginDllPath.Length == 0)
                {
                    return false;
                }
            }
            else
            {
                pluginDllPath = DefaultPluginDllPath;
            }

            return true;
        }

        /// <summary>
        /// Maps the service's SCM ObjectName registry value to the account principal that
        /// must be able to read the CA private key. Missing value or "LocalSystem" is the
        /// SCM's built-in SYSTEM account; the two restricted built-ins are normalized to
        /// their qualified names; anything else (a managed service account, a domain user)
        /// flows through verbatim.
        /// </summary>
        public static string MapServiceAccount(string objectName)
        {
            var name = (objectName ?? string.Empty).Trim();
            if (name.Length == 0 || name.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
            {
                return @"NT AUTHORITY\SYSTEM";
            }

            if (name.EndsWith("NetworkService", StringComparison.OrdinalIgnoreCase))
            {
                return @"NT AUTHORITY\NetworkService";
            }

            if (name.EndsWith("LocalService", StringComparison.OrdinalIgnoreCase))
            {
                return @"NT AUTHORITY\LocalService";
            }

            return name;
        }

        /// <summary>
        /// Executes the provisioning flow against the injected seams. Every refusal is
        /// logged; the exit code is the only channel the MSI Return="check" sees.
        /// </summary>
        public static int Run(
            string[] args,
            IServiceAccountReader serviceAccounts,
            IAuthorityGateway authorityGateway,
            IPrivateKeyAccessGrant keyGrant,
            Action<string> log)
        {
            if (serviceAccounts == null)
            {
                throw new ArgumentNullException(nameof(serviceAccounts));
            }

            if (authorityGateway == null)
            {
                throw new ArgumentNullException(nameof(authorityGateway));
            }

            if (keyGrant == null)
            {
                throw new ArgumentNullException(nameof(keyGrant));
            }

            if (log == null)
            {
                log = _ => { };
            }

            string serviceName;
            string pluginDllPath;
            if (!TryParse(args, out serviceName, out pluginDllPath))
            {
                log(Usage);
                return ExitUsage;
            }

            var account = MapServiceAccount(serviceAccounts.ReadObjectName(serviceName));
            log("Event Server service '" + serviceName + "' runs as " + account);

            try
            {
                authorityGateway.Provision(pluginDllPath, log);
            }
            catch (Exception exception)
            {
                log("deployment proxy CA provisioning FAILED: " + exception.Message);
                return ExitProvisionFailed;
            }

            if (!keyGrant.EnsureServiceAccountCanReadKey(account, log))
            {
                log("deployment proxy CA private-key access for " + account + " could not be confirmed - " +
                    "leaf minting would fail at runtime, failing the install instead.");
                return ExitProvisionFailed;
            }

            log("deployment proxy CA is ready: " + AuthorityCommonName);
            return ExitDone;
        }
    }

    /// <summary>Reads a service's SCM ObjectName (its logon account); null when absent.</summary>
    public interface IServiceAccountReader
    {
        string ReadObjectName(string serviceName);
    }

    /// <summary>
    /// Loads or creates the deployment CA through the plugin's own code (the real
    /// implementation reflects into the installed WebContent.dll). Throwing means the
    /// CA half failed: the core maps that to the install-failing exit.
    /// </summary>
    public interface IAuthorityGateway
    {
        void Provision(string pluginDllPath, Action<string> log);
    }

    /// <summary>
    /// Verifies (and when needed applies) the service account's Read access to the CA
    /// private key. Reuse of an existing CA is untouched certificate-wise: the grant is
    /// idempotent (add only when absent), the same posture the provision script always
    /// had, so an upgrade re-run leaves both the certificate AND an already-correct DACL
    /// byte-identical.
    /// </summary>
    public interface IPrivateKeyAccessGrant
    {
        bool EnsureServiceAccountCanReadKey(string account, Action<string> log);
    }
}
