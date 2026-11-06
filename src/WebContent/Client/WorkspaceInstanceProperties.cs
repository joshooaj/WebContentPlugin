using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace WebContent.Client
{
    /// <summary>
    /// Session-scoped hand-off of admin-defined workspace properties from the workspace plugin to
    /// the provisioned view item. The dictionary passed to <c>InsertViewItemPlugin</c> is not
    /// readable through <c>ViewItemManager.GetProperty</c> (the platform samples themselves pass an
    /// empty dictionary), so without this registry every property read on a workspace tab falls
    /// back to the built-in defaults. Entries are registered per Smart Client session and keyed
    /// by the view item's InstanceId; they are overwritten on re-registration rather than removed,
    /// which also keeps a reclaimed view item's browser reachable for re-attach paths.
    /// </summary>
    internal static class WorkspaceInstanceProperties
    {
        private static readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<string, string>> _byInstanceId =
            new ConcurrentDictionary<Guid, IReadOnlyDictionary<string, string>>();

        public static void Register(Guid instanceId, IDictionary<string, string> properties)
        {
            if (instanceId == Guid.Empty || properties == null)
            {
                return;
            }

            _byInstanceId[instanceId] = new Dictionary<string, string>(properties);
        }

        public static bool TryGet(Guid instanceId, out IReadOnlyDictionary<string, string> properties)
        {
            properties = null;
            return instanceId != Guid.Empty && _byInstanceId.TryGetValue(instanceId, out properties);
        }

        /// <summary>
        /// Returns the definition entries that must be written to the view item's property store.
        /// Ticket #71 policy (see <see cref="WorkspaceSettingLocks"/>): identity keys and the
        /// security-relevant locked keys (home URL, proxy posture, allowlist) are ALWAYS enforced
        /// when they differ, so a stored per-instance override of one - from an older build or a
        /// hand edit - can never reach runtime. Cosmetic/navigation keys only receive their
        /// initial value from the definition, or are re-initialised when <paramref
        /// name="definitionChanged"/> signals the admin edited the definition; after that the
        /// Smart Client user's per-instance choice wins.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> ComputeSeed(
            Func<string, string> currentProvider,
            IReadOnlyDictionary<string, string> definition,
            bool definitionChanged = false)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (currentProvider == null || definition == null)
            {
                return result;
            }

            foreach (var entry in definition)
            {
                if (WorkspaceSettingLocks.ShouldWriteSeedValue(entry.Key, currentProvider(entry.Key), entry.Value, definitionChanged))
                {
                    result.Add(entry);
                }
            }

            return result;
        }
    }
}
