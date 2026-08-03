using System;
using System.Text;

namespace WebContent.Client
{
    /// <summary>
    /// Pure helpers that turn an XProtect login identity into a stable, filesystem-safe
    /// browser profile key. Kept free of any SDK/WPF dependency so the logic can be unit
    /// tested in isolation. The plugin resolves the live identity via
    /// <see cref="BrowserProfileIdentity"/> and hands the raw identity fields into these
    /// functions.
    /// </summary>
    internal static class BrowserProfileKey
    {
        /// <summary>
        /// Root directory name under <c>%APPDATA%</c> that hosts the per-XProtect-user
        /// WebView2 user data folders used by the plugin.
        /// </summary>
        internal const string DataFolderRootName = "SmartClient.WebContentData";

        /// <summary>
        /// Derives a stable, distinct profile key for a given XProtect login identity.
        /// Prefers the stable user-identity value, then the fully-qualified user name,
        /// then the plain user name. Returns <see cref="string.Empty"/> when none of the
        /// identity fields are available so callers can fall back to a neutral folder.
        /// </summary>
        internal static string DeriveUserKey(string userIdentity, string fullyQualifiedUserName, string userName)
        {
            return Sanitize(FirstNonEmpty(userIdentity, fullyQualifiedUserName, userName));
        }

        /// <summary>
        /// Returns the first non-whitespace candidate, or <c>null</c> if they are all
        /// blank.
        /// </summary>
        private static string FirstNonEmpty(string first, string second, string third)
        {
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }

            if (!string.IsNullOrWhiteSpace(second))
            {
                return second;
            }

            if (!string.IsNullOrWhiteSpace(third))
            {
                return third;
            }

            return null;
        }

        /// <summary>
        /// Normalizes an identity value into a single, filesystem-friendly path segment.
        /// Letters, digits, <c>.</c>, <c>-</c> and <c>_</c> are preserved; every other
        /// character (including path separators, spaces and braces) becomes <c>_</c>.
        /// This prevents an identity value from escaping into other folders or devices.
        /// </summary>
        internal static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            foreach (var ch in value.Trim())
            {
                sb.Append(IsSafe(ch) ? ch : '_');
            }

            return sb.ToString();
        }

        private static bool IsSafe(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_';
        }
    }
}