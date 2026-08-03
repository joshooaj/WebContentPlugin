using System;
using System.Collections.Generic;

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
        /// Returns the first provisionable definition in server order, or null when none qualify.
        /// </summary>
        public static WorkspaceDefinition SelectFirstProvisionable(IReadOnlyList<WorkspaceDefinition> definitions)
        {
            if (definitions == null)
            {
                return null;
            }

            foreach (var definition in definitions)
            {
                if (definition != null && definition.IsProvisionable)
                {
                    return definition;
                }
            }

            return null;
        }
    }
}
