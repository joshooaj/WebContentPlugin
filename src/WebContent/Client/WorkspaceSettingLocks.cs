using System;
using System.Collections.Generic;

namespace WebContent.Client
{
    /// <summary>
    /// Pure decision helper for ticket #71: which settings of a view item that lives inside an
    /// admin-defined (managed) workspace the administrator governs, and how the workspace seed
    /// treats each key when it re-applies the definition.
    ///
    /// Managed-ness is decided from the view item's OWN persisted identity keys: a workspace-
    /// provisioned view item carries the workspace id and the fingerprint of the definition that
    /// seeded it (written by the seeding path, never by the user), a standalone (manually placed)
    /// view item carries neither. Outside managed workspaces nothing here changes anything.
    ///
    /// Locked keys are the security-relevant ones the ticket names: proxy enablement, the
    /// allowlist-derived behavior, and the target URL the workspace pins (the definition builder
    /// always writes HomeAddress). A stored per-instance override of a locked key - left over from
    /// an older build or hand-edited - is overwritten by every seed, so it can never reach runtime.
    /// Cosmetic/navigation keys (toolbar and address-bar visibility, zoom, audio, history,
    /// isolation, dev tools) stay per-instance editable: the seed only supplies their initial
    /// value (when the view item has none yet) or re-initialises them when the admin definition
    /// itself changed; it never reverts a per-instance choice inside an unchanged definition.
    /// </summary>
    internal static class WorkspaceSettingLocks
    {
        /// <summary>
        /// Identity/sync keys, never per-instance choices: the seeding and stale-item detection
        /// compare them, so the definition must always win.
        /// </summary>
        private static readonly HashSet<string> IdentityKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            WorkspaceViewItemSync.WorkspaceIdKey,
            "WorkspaceName",
            WorkspaceViewItemSync.FingerprintKey,
            "InstanceId",
        };

        /// <summary>
        /// Security-relevant definition keys a Smart Client user may never override per instance
        /// (#71). ProxyEnabled/ProxyTargets already render read-only in the properties pane;
        /// HomeAddress is the editable field this policy keeps authoritative. "Address" is the
        /// legacy alias the settings write path maintains beside HomeAddress.
        /// </summary>
        private static readonly HashSet<string> LockedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HomeAddress",
            "Address",
            "ProxyEnabled",
            "ProxyTargets",
        };

        /// <summary>
        /// True when the persisted view-item identity (workspace id + definition fingerprint)
        /// marks it as provisioned by an admin-defined workspace.
        /// </summary>
        public static bool IsManagedWorkspace(string workspaceId, string definitionFingerprint)
        {
            return !string.IsNullOrWhiteSpace(workspaceId) || !string.IsNullOrWhiteSpace(definitionFingerprint);
        }

        public static bool IsIdentityKey(string key)
        {
            return key != null && IdentityKeys.Contains(key);
        }

        public static bool IsLockedKey(string key)
        {
            return key != null && LockedKeys.Contains(key);
        }

        /// <summary>
        /// Seed decision for one definition entry against the view item's currently persisted
        /// value. Identity and locked keys are always enforced when they differ (per-instance
        /// overrides of them are ignored, whatever the UI shows). Cosmetic/navigation keys are
        /// written only when the view item has no value yet (initial seeding) or when the admin
        /// definition just changed (new definition = new defaults); a per-instance override of an
        /// unlocked key inside an unchanged definition survives the seed.
        /// </summary>
        public static bool ShouldWriteSeedValue(string key, string currentValue, string definitionValue, bool definitionChanged)
        {
            if (string.Equals(currentValue, definitionValue, StringComparison.Ordinal))
            {
                return false;
            }

            if (IsIdentityKey(key) || IsLockedKey(key))
            {
                return true;
            }

            return string.IsNullOrEmpty(currentValue) || definitionChanged;
        }
    }
}
