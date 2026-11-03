namespace WebContent.Client
{
    /// <summary>
    /// Security action keys declared on the workspace <c>ItemNode</c> (see
    /// <see cref="WebContentPluginDefinition"/>). The display names 'View Web Content Workspace'
    /// and 'Manage Web Content Workspace' are bound to GENERIC_READ / GENERIC_WRITE because the
    /// Management Server rejects adding brand-new action keys to an already-defined MIP kind.
    /// </summary>
    internal static class WorkspacePermission
    {
        /// <summary>The action a user needs to see a workspace as a Smart Client tab.</summary>
        public const string View = "GENERIC_READ";

        /// <summary>The action a user needs to manage a workspace from the Management Client.</summary>
        public const string Manage = "GENERIC_WRITE";
    }
}
