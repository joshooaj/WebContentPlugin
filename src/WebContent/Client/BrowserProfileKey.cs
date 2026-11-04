using System;
using System.Globalization;
using System.IO;
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
        /// ADR-0004 (#92): folder-name prefixes of the proxied-boundary path shape
        /// <c>&lt;root&gt;\&lt;user key&gt;\server-&lt;server key&gt;\proxied-&lt;browser id&gt;</c>.
        /// The prefixes keep every segment a single safe name: a <c>server-</c> segment can never
        /// be <c>.</c> or <c>..</c> no matter what the identity values sanitize to, and the
        /// <c>proxied-</c> leaf keeps the shape #94's disk sweep must recognize.
        /// </summary>
        internal const string ServerFolderPrefix = "server-";

        internal const string ProxiedFolderPrefix = "proxied-";

        /// <summary>
        /// Server segment placeholder used when the VMS server leg cannot be resolved (offline,
        /// before login). Processes that all fail to resolve agree on this one placeholder, and
        /// processes that DO resolve the server never share a folder with them.
        /// </summary>
        internal const string UnknownServerKey = "unknown";

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
        /// ADR-0004 (#92): derives the VMS-server leg of the proxied boundary from the
        /// Management Server's DNS name/address and port. Hosts are case-insensitive and the
        /// port distinguishes two VMS instances sharing one host, so the host is lower-cased
        /// and the port is folded in. Returns <see cref="string.Empty"/> when no host is
        /// available; the caller then composes the path with <see cref="UnknownServerKey"/>.
        /// </summary>
        internal static string DeriveServerKey(string serverHost, int serverPort)
        {
            if (string.IsNullOrWhiteSpace(serverHost))
            {
                return string.Empty;
            }

            var key = Sanitize(serverHost.ToLowerInvariant());
            if (serverPort <= 0)
            {
                return key;
            }

            return key + "-" + serverPort.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// ADR-0004 (#92): composes the proxied browser-profile path for one proxied boundary
        /// leg triple: <c>&lt;dataRoot&gt;[\&lt;user key&gt;]\server-&lt;server key&gt;\proxied-&lt;browser id&gt;</c>.
        /// Same (Windows user, XProtect identity, VMS server, browser id) tuple ⇒ identical path;
        /// any leg differing ⇒ a different path. An empty user key falls back to the shared root
        /// level exactly like <see cref="DeriveUserKey"/> callers do; an empty server key uses the
        /// <see cref="UnknownServerKey"/> placeholder so a resolved server never shares a folder
        /// with an unresolved one. Every segment is sanitized, so no identity value escapes its
        /// folder level.
        /// </summary>
        internal static string ComposeProxiedProfilePath(string dataRoot, string userKey, string serverKey, Guid browserId)
        {
            var segments = new System.Collections.Generic.List<string> { dataRoot };

            var user = Sanitize(userKey);
            if (user.Length > 0)
            {
                segments.Add(user);
            }

            var server = Sanitize(serverKey);
            segments.Add(ServerFolderPrefix + (server.Length > 0 ? server : UnknownServerKey));

            // The leaf keeps the existing lowercase-D Guid format so the folder name matches
            // the browser id the plugin logs, and #94's sweep recognizes the proxied- prefix.
            segments.Add(ProxiedFolderPrefix + browserId.ToString("D"));

            return Path.Combine(segments.ToArray());
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
