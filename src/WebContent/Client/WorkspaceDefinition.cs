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
        /// Returns the definitions the current user may view, in admin order. A definition must be
        /// provisionable (see <see cref="IsProvisionable"/>) and its id must be present in
        /// <paramref name="permittedIds"/>; everything else is silently skipped (ticket #8).
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
                if (definition != null && definition.IsProvisionable && permittedIds.Contains(definition.Id))
                {
                    result.Add(definition);
                }
            }

            return result;
        }
    }
}
