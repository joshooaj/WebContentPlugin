using System;
using System.IO;

using VideoOS.Platform;
using VideoOS.Platform.Login;

namespace WebContent.Client
{
    /// <summary>
    /// Resolves the WebView2 user data folder for the Smart Client, keyed to the currently
    /// logged-on XProtect user so that separate XProtect identities do not share browser
    /// profile data even when they log in from the same Windows account.
    ///
    /// This provides data <em>separation</em>: a different XProtect user starts with a
    /// clean profile instead of inheriting another user's history, cookies and saved
    /// credentials. It does not provide cryptographic isolation, because WebView2/Chromium
    /// encrypts protected data with Windows DPAPI scoped to the Windows logon. Within a
    /// single Windows account the DPAPI trust boundary is shared, so the Windows account is
    /// the security boundary. See <c>docs/adr/</c> for the full decision.
    /// </summary>
    internal static class BrowserProfileIdentity
    {
        /// <summary>
        /// Derives the filesystem profile key for the current XProtect login identity.
        /// Returns <see cref="string.Empty"/> when no identity can be resolved (e.g. during
        /// design-time or before login is established).
        /// </summary>
        internal static string GetCurrentUserKey()
        {
            try
            {
                var settings = LoginSettingsCache.GetLoginSettings(EnvironmentManager.Instance.MasterSite);
                if (settings != null)
                {
                    return BrowserProfileKey.DeriveUserKey(settings.UserIdentity, settings.FullyQualifiedUserName, settings.UserName);
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(BrowserProfileIdentity) + "." + nameof(GetCurrentUserKey), ex);
            }

            return string.Empty;
        }

        /// <summary>
        /// Derives the VMS-server leg of the proxied boundary (ADR-0004) from the MasterSite
        /// this client logged into. Returns <see cref="string.Empty"/> when the server leg
        /// cannot be resolved; <see cref="GetProxiedUserDataFolder"/> then uses the placeholder
        /// server segment and logs the degradation.
        /// </summary>
        internal static string GetCurrentServerKey()
        {
            try
            {
                var serverUri = EnvironmentManager.Instance != null && EnvironmentManager.Instance.MasterSite != null
                    ? EnvironmentManager.Instance.MasterSite.ServerId != null
                        ? EnvironmentManager.Instance.MasterSite.ServerId.Uri
                        : null
                    : null;
                if (serverUri != null)
                {
                    return BrowserProfileKey.DeriveServerKey(serverUri.Host, serverUri.Port);
                }
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.ExceptionHandler(nameof(BrowserProfileIdentity) + "." + nameof(GetCurrentServerKey), ex);
            }

            return string.Empty;
        }

        /// <summary>
        /// Returns the WebView2 <c>UserDataFolder</c> for a PROXIED browser profile: keyed to the
        /// full proxied boundary (Windows user, implicit in the per-user root, plus XProtect
        /// identity, plus VMS server) per ADR-0004, so two Smart Clients logged into different
        /// servers never share a proxied profile or fetch channel even when the identity value
        /// itself collides. The NON-proxied folder (<see cref="GetUserDataFolder"/>) stays as it
        /// is; ADR-0004 scopes the server leg to proxied state.
        /// </summary>
        internal static string GetProxiedUserDataFolder(Guid browserId)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                BrowserProfileKey.DataFolderRootName);

            var userKey = GetCurrentUserKey();
            var serverKey = GetCurrentServerKey();
            if (string.IsNullOrWhiteSpace(serverKey))
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(BrowserProfileIdentity),
                    $"Could not resolve the VMS server for proxied browser {browserId}; its profile lands under the " +
                    $"'{BrowserProfileKey.ServerFolderPrefix}{BrowserProfileKey.UnknownServerKey}' placeholder segment. " +
                    "Proxied profiles on different VMS servers are not separated until the server is known.");
            }

            return BrowserProfileKey.ComposeProxiedProfilePath(root, userKey, serverKey, browserId);
        }

        /// <summary>
        /// Returns the WebView2 <c>UserDataFolder</c> to use, scoped to the current XProtect
        /// user. Falls back to the shared root folder (with a logged warning) when the
        /// identity is unavailable, e.g. before login is established.
        /// </summary>
        internal static string GetUserDataFolder()
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                BrowserProfileKey.DataFolderRootName);

            var key = GetCurrentUserKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(BrowserProfileIdentity),
                    $"Could not resolve the XProtect login identity; using the shared browser data folder {root}. " +
                    "Separate XProtect users will not be isolated until login is established.");
                return root;
            }

            return Path.Combine(root, key);
        }
    }
}
