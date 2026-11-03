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
        /// Returns the definition entries that must be written to the view item's property store:
        /// everything whose current value differs from the admin-defined value. For
        /// workspace-provisioned view items the admin definition is authoritative, so stale
        /// layout values are overwritten rather than preserved.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> ComputeSeed(
            Func<string, string> currentProvider,
            IReadOnlyDictionary<string, string> definition)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (currentProvider == null || definition == null)
            {
                return result;
            }

            foreach (var entry in definition)
            {
                if (!string.Equals(currentProvider(entry.Key), entry.Value, StringComparison.Ordinal))
                {
                    result.Add(entry);
                }
            }

            return result;
        }
    }
}
