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