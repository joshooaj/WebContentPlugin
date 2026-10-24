using System;
using System.Collections.Generic;
using VideoOS.Platform;
using VideoOS.Platform.Client;

namespace WebContent.Client
{
    /// <summary>
    /// Turns admin-defined workspace configurations read from the server into one Smart Client
    /// workspace plugin per workspace (ticket #8). Workspaces the current user cannot VIEW are
    /// silently skipped, and the remaining tabs are created in admin-defined server order. The
    /// pure selection and property building live in <see cref="WorkspaceDefinition"/> and
    /// <see cref="WorkspaceProperties"/> so they can be unit-tested without the VideoOS SDK.
    /// </summary>
    internal sealed class WorkspacePluginFactory
    {
        private readonly WorkspacePermissionEvaluator _permissionEvaluator = new WorkspacePermissionEvaluator();

        /// <summary>
        /// Returns one plugin per admin-defined workspace the current user may VIEW, in the admin-defined
        /// order, or an empty list when the read succeeded but no workspace qualifies.
        /// </summary>
        public IReadOnlyList<WorkSpacePlugin> CreateWorkSpacePlugins(IReadOnlyList<Item> workspaces)
        {
            if (workspaces == null || workspaces.Count == 0)
            {
                return Array.Empty<WorkSpacePlugin>();
            }

            var definitions = new List<WorkspaceDefinition>(workspaces.Count);
            foreach (var workspace in workspaces)
            {
                definitions.Add(new WorkspaceDefinition(
                    workspace.FQID.ObjectId,
                    workspace.Name,
                    workspace.Properties));
            }

            var permittedIds = _permissionEvaluator.GetViewableWorkspaceIds(workspaces);
            var visible = WorkspaceDefinition.SelectVisible(definitions, permittedIds);

            var plugins = new List<WorkSpacePlugin>(visible.Count);
            foreach (var definition in visible)
            {
                var properties = WorkspaceProperties.Build(
                    definition.Id,
                    definition.Name,
                    definition.SourceProperties,
                    Strings.HomeAddress);

                plugins.Add(new WebContentWorkSpacePlugin(definition.Id, definition.Name, properties, definition.ConfigurationError));
            }

            return plugins;
        }
    }
}
