namespace WebContent.Admin
{
    /// <summary>
    /// Outcome of the workspace item's SecurityActions 'Manage' (edit-rights) check for the
    /// currently logged-in Management Client user (ticket #74). The action key is
    /// <see cref="WebContent.Client.WorkspacePermission.Manage"/> — the 'Manage Web Content
    /// Workspace' action declared on the workspace <c>ItemNode</c>.
    /// </summary>
    internal enum WorkspaceManagePermission
    {
        /// <summary>The security system explicitly granted the action to this user.</summary>
        Granted,

        /// <summary>The security system denied the action (CheckPermission threw).</summary>
        Denied,

        /// <summary>No item context to check against, or the permission lookup itself failed.</summary>
        LookupFailed,
    }

    /// <summary>
    /// Tab visibility decisions for the tabbed workspace config page (ticket #74). Pure so the
    /// unit suite pins the posture without the platform security stack.
    /// </summary>
    internal static class WorkspaceTabPolicy
    {
        /// <summary>
        /// The Status tab renders only on an explicit Granted verdict. Denial, a missing item,
        /// or a failed lookup all hide the whole tab — the same fail-closed posture as the Smart
        /// Client's <see cref="WebContent.Client.WorkspacePermissionEvaluator"/>.
        /// </summary>
        public static bool ShowStatusTab(WorkspaceManagePermission permission)
        {
            return permission == WorkspaceManagePermission.Granted;
        }
    }
}
