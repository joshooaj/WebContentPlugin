using System;
using System.Collections.Concurrent;

namespace WebContent.Client
{
    /// <summary>
    /// Session-scoped store of the last visited URL per workspace view item (ticket #9). Entries
    /// are keyed by the view item's InstanceId and guarded by the admin-definition fingerprint:
    /// when the admin edits the definition the fingerprint changes and the recorded URL becomes
    /// unreadable, so the browser restores the new home URL. Because the store is in-memory, a
    /// Smart Client restart resets every workspace browser to its home URL. Standalone view items
    /// carry no fingerprint and are deliberately not tracked.
    /// </summary>
    internal static class WorkspaceSessionState
    {
        private sealed class SessionEntry
        {
            public string Fingerprint;
            public string Url;
        }

        private static readonly ConcurrentDictionary<Guid, SessionEntry> _byInstanceKey =
            new ConcurrentDictionary<Guid, SessionEntry>();

        public static void Record(Guid instanceKey, string fingerprint, string url)
        {
            if (instanceKey == Guid.Empty || string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _byInstanceKey[instanceKey] = new SessionEntry
            {
                Fingerprint = fingerprint,
                Url = url.Trim(),
            };
        }

        public static string TryGetRecordedUrl(Guid instanceKey, string fingerprint)
        {
            if (instanceKey == Guid.Empty || string.IsNullOrWhiteSpace(fingerprint))
            {
                return null;
            }

            if (!_byInstanceKey.TryGetValue(instanceKey, out var entry))
            {
                return null;
            }

            return string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(entry.Url)
                ? entry.Url
                : null;
        }

        public static void Reset(Guid instanceKey)
        {
            if (instanceKey != Guid.Empty)
            {
                _byInstanceKey.TryRemove(instanceKey, out _);
            }
        }

        /// <summary>
        /// Restore precedence for a view item browser: this session's recorded URL wins; with no
        /// recording (fresh session) the persisted LastAddress is used only when
        /// RememberLastAddress is on; otherwise the home URL.
        /// </summary>
        public static string ResolveRestoreAddress(string recordedUrl, string persistedLastAddress, bool rememberLastAddress, string homeAddress)
        {
            if (!string.IsNullOrWhiteSpace(recordedUrl))
            {
                return recordedUrl;
            }

            if (rememberLastAddress && !string.IsNullOrWhiteSpace(persistedLastAddress))
            {
                return persistedLastAddress;
            }

            return homeAddress;
        }

        public static bool ShouldResetOnDefinitionUpdate(string previousFingerprint, string updatedFingerprint)
        {
            if (string.IsNullOrWhiteSpace(previousFingerprint) || string.IsNullOrWhiteSpace(updatedFingerprint))
            {
                return false;
            }

            return !string.Equals(previousFingerprint, updatedFingerprint, StringComparison.Ordinal);
        }
    }
}
