using System;
using System.Collections.Generic;
using System.Linq;

namespace WebContent.Client
{
    /// <summary>
    /// Plain-data description of an admin-defined workspace read from the server. Decoupled from
    /// VideoOS so the selection logic can be unit-tested. The source properties are the raw
    /// property dictionary of the workspace <c>Item</c>.
    /// </summary>
    internal sealed class WorkspaceDefinition
    {
        public WorkspaceDefinition(Guid id, string name, IDictionary<string, string> sourceProperties)
        {
            Id = id;
            Name = name ?? string.Empty;
            SourceProperties = sourceProperties ?? new Dictionary<string, string>();
        }

        public Guid Id { get; }

        public string Name { get; }

        public IDictionary<string, string> SourceProperties { get; }

        /// <summary>
        /// The home address the administrator configured: the JSON blob's first WebContent item,
        /// falling back to the legacy HomeAddress/Address properties.
        /// </summary>
        public string HomeAddress
        {
            get
            {
                var settings = WorkspaceConfiguration.GetFirstWebContentSettings(SourceProperties);
                if (settings != null)
                {
                    return settings.HomeAddress;
                }

                return WebContentConfiguration.GetString(SourceProperties, string.Empty, "HomeAddress", "Address");
            }
        }

        /// <summary>
        /// A workspace can become a Smart Client tab only when it has a display name and a
        /// configured home address.
        /// </summary>
        public bool IsProvisionable
        {
            get { return !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(HomeAddress); }
        }

        /// <summary>
        /// A workspace is shown as a tab when it has a display name, even if its configuration is
        /// broken (ticket #9): the tab then carries a WorkSpaceNotification describing the
        /// configuration error instead of silently disappearing.
        /// </summary>
        public bool IsDisplayable
        {
            get { return !string.IsNullOrWhiteSpace(Name); }
        }

        /// <summary>
        /// Why this definition has no usable browser configuration, or <see cref="WorkspaceConfigError.None"/>
        /// when it is healthy (ticket #9).
        /// </summary>
        public WorkspaceConfigError ConfigurationError
        {
            get { return WorkspaceConfiguration.DetectError(SourceProperties); }
        }

        /// <summary>
        /// Returns the definitions the current user may view, in admin order. A definition must be
        /// displayable (see <see cref="IsDisplayable"/>) and its id must be present in
        /// <paramref name="permittedIds"/>; since ticket #9 a broken configuration does not hide
        /// the tab, it only classifies the defect via <see cref="ConfigurationError"/>.
        /// </summary>
        public static List<WorkspaceDefinition> SelectVisible(
            IReadOnlyList<WorkspaceDefinition> definitions,
            IReadOnlyCollection<Guid> permittedIds)
        {
            var result = new List<WorkspaceDefinition>();
            if (definitions == null || permittedIds == null)
            {
                return result;
            }

            foreach (var definition in definitions)
            {
                if (definition != null && definition.IsDisplayable && permittedIds.Contains(definition.Id))
                {
                    result.Add(definition);
                }
            }

            return result;
        }
    }
}
