using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// Per-view-item proxy state for browser-profile creation (#54): the admin-owned ProxyEnabled
    /// toggle plus the canonical ProxyTargets property the #52 seeding path publishes to the view
    /// item. An enabled proxy always produces a profile — with no usable targets the listener
    /// still fronts the browser and refuses everything, which is the "never direct" posture.
    /// </summary>
    internal sealed class ProxyWorkspaceProfile
    {
        /// <summary>Canonical origins the loopback listener may forward to; may be empty.</summary>
        internal IReadOnlyList<string> CanonicalTargets { get; private set; }

        internal static bool TryCreate(bool proxyEnabled, string canonicalTargetsProperty, out ProxyWorkspaceProfile profile)
        {
            profile = null;
            if (!proxyEnabled)
            {
                return false;
            }

            var origins = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (canonicalTargetsProperty ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ProxyPolicy.TryNormalizeTarget(entry, out var normalized) && seen.Add(normalized))
                {
                    origins.Add(normalized);
                }
            }

            // An enabled proxy always fronts the browser: an empty usable list refuses everything,
            // which keeps the "never direct" promise for corrupt or half-configured definitions.
            profile = new ProxyWorkspaceProfile { CanonicalTargets = origins };
            return true;
        }
    }
}
