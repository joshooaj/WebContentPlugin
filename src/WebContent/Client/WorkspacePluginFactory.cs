using System;
using System.Collections.Generic;
using VideoOS.Platform;
using VideoOS.Platform.Client;

namespace WebContent.Client
{
    /// <summary>
    /// Turns admin-defined workspace configurations read from the server into a Smart Client
    /// workspace plugin. Ticket #7 provisions exactly one tab from the first provisionable
    /// configuration; multi-workspace support is a follow-up. The pure selection and property
    /// building live in <see cref="WorkspaceDefinition"/> and <see cref="WorkspaceProperties"/>
    /// so they can be unit-tested without the VideoOS SDK.
    /// </summary>
    internal sealed class WorkspacePluginFactory
    {
        /// <summary>
        /// Returns the plugin for the first provisionable workspace in server order, or null when
        /// the read succeeded but no workspace has both a display name and a configured home
        /// address.
        /// </summary>
        public WorkSpacePlugin CreateFirstProvisionableWorkSpacePlugin(IReadOnlyList<Item> workspaces)
        {
            if (workspaces == null || workspaces.Count == 0)
            {
                return null;
            }

            var definitions = new List<WorkspaceDefinition>(workspaces.Count);
            foreach (var workspace in workspaces)
            {
                definitions.Add(new WorkspaceDefinition(
                    workspace.FQID.ObjectId,
                    workspace.Name,
                    workspace.Properties));
            }

            var selected = WorkspaceDefinition.SelectFirstProvisionable(definitions);
            if (selected == null)
            {
                return null;
            }

            var properties = WorkspaceProperties.Build(
                selected.Id,
                selected.Name,
                selected.SourceProperties,
                Strings.HomeAddress);

            return new WebContentWorkSpacePlugin(selected.Id, selected.Name, properties);
        }
    }
}
