using System;
using System.Collections.Generic;
using System.Linq;
using VideoOS.Platform;
using VideoOS.Platform.Util;

namespace WebContent.Client
{
    /// <summary>
    /// Decides which admin-defined workspace items the current Smart Client user may VIEW, so
    /// <see cref="WorkspacePluginFactory"/> can silently skip the rest. Backed by the MIP security
    /// system: <see cref="SecurityAccess.BuildPermissionList"/> returns the workspaces the current
    /// user can access with the VIEW action. A failed lookup is logged and treated as 'no workspace
    /// is viewable' (fail closed), so content is never leaked to a user whose permissions could not
    /// be verified.
    /// </summary>
    internal sealed class WorkspacePermissionEvaluator
    {
        /// <summary>
        /// Returns the ids of the given workspace items the current user may access with the VIEW
        /// action, or an empty set when the lookup fails.
        /// </summary>
        public IReadOnlyCollection<Guid> GetViewableWorkspaceIds(IReadOnlyList<Item> workspaces)
        {
            if (workspaces == null || workspaces.Count == 0)
            {
                return Array.Empty<Guid>();
            }

            try
            {
                var viewable = SecurityAccess.BuildPermissionList(workspaces.ToList(), WorkspacePermission.View);
                var ids = new HashSet<Guid>();
                foreach (var item in viewable ?? new List<Item>())
                {
                    ids.Add(item.FQID.ObjectId);
                }

                return ids;
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(true, nameof(WorkspacePermissionEvaluator), $"Failed to evaluate workspace VIEW permission for {workspaces.Count} workspace(s). No workspace tabs will be provisioned. {ex.Message}");
                return Array.Empty<Guid>();
            }
        }
    }
}
