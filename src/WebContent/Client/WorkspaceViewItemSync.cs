using System;
using System.Collections.Generic;

namespace WebContent.Client
{
    /// <summary>
    /// Pure decision helper for <c>WebContentWorkSpacePlugin</c>: every WebContent tab shares one
    /// view item plugin id, so finding a view item at position 0 of the layout item does not prove
    /// it belongs to this workspace definition. The persisted view item is only current when it
    /// carries this workspace's id and the fingerprint of its current definition; anything else
    /// (foreign view item, pre-fingerprint version, admin-edited definition) must be removed and
    /// re-provisioned so the tab name and the rendered content cannot drift apart.
    /// </summary>
    internal static class WorkspaceViewItemSync
    {
        public const string WorkspaceIdKey = "WorkspaceId";
        public const string FingerprintKey = "DefinitionFingerprint";

        public static bool IsCurrent(IDictionary<string, string> persistedProperties, Guid workspaceId, string expectedFingerprint)
        {
            if (persistedProperties == null || string.IsNullOrEmpty(expectedFingerprint))
            {
                return false;
            }

            if (!TryGet(persistedProperties, WorkspaceIdKey, out var persistedId) ||
                !Guid.TryParse(persistedId, out var parsedId) ||
                parsedId != workspaceId)
            {
                return false;
            }

            if (!TryGet(persistedProperties, FingerprintKey, out var persistedFingerprint))
            {
                return false;
            }

            return string.Equals(persistedFingerprint, expectedFingerprint, StringComparison.Ordinal);
        }

        private static bool TryGet(IDictionary<string, string> properties, string key, out string value)
        {
            if (properties.TryGetValue(key, out value))
            {
                return true;
            }

            foreach (var entry in properties)
            {
                if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }
    }
}
