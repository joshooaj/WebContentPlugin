using System;

using VideoOS.Platform;
using VideoOS.Platform.Util;

namespace WebContent.Admin
{
    /// <summary>
    /// Runs the workspace item's SecurityActions edit-rights check ('Manage Web Content
    /// Workspace', backed by GENERIC_WRITE) against the current Management Client user for the
    /// #74 Status tab gate. <see cref="SecurityAccess.CheckPermission(Item, string)"/> throws
    /// when access is denied; every throw — denial or a failed lookup — is reported as
    /// not-granted so the caller hides the tab (fail closed).
    /// </summary>
    internal static class WorkspaceManagePermissionEvaluator
    {
        public static WorkspaceManagePermission Check(Item item)
        {
            if (item == null)
            {
                return WorkspaceManagePermission.LookupFailed;
            }

            try
            {
                SecurityAccess.CheckPermission(item, WebContent.Client.WorkspacePermission.Manage);
                return WorkspaceManagePermission.Granted;
            }
            catch (Exception ex)
            {
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(WorkspaceManagePermissionEvaluator),
                    $"Workspace '{item.Name}' (FQID: {item.FQID}) does not grant the Manage security action to this user (or the permission check failed); the Status tab stays hidden. {ex.Message}");
                return WorkspaceManagePermission.Denied;
            }
        }
    }
}