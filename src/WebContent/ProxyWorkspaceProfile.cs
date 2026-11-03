using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// Per-view-item proxy state for browser-profile creation (#54): the admin-owned ProxyEnabled
    /// toggle plus the canonical ProxyTargets property the #52 seeding path publishes to the view
    /// item. An enabled proxy always produces a profile — with no usable targets the listener
    /// still fronts the browser and refuses everything, which is the "never direct" posture.
    /// #73: the canonical entries may be wildcard patterns, and the profile carries the VMS's own
    /// host names so the listener's matcher applies the wildcard SSRF floor identically to the
    /// Event Server's gate.
    /// </summary>
    internal sealed class ProxyWorkspaceProfile
    {
        /// <summary>Canonical origins/patterns the loopback listener may forward to; may be empty.</summary>
        internal IReadOnlyList<string> CanonicalTargets { get; private set; }

        /// <summary>#73: VMS host names the wildcard SSRF floor is applied against; may be empty.</summary>
        internal IReadOnlyList<string> VmsFloorHosts { get; private set; }

        /// <summary>The workspace view item this profile was resolved for (#69 diagnostics
        /// attribution); empty for designer/standalone paths that have no workspace item.</summary>
        internal string WorkspaceItemId { get; private set; }

        /// <summary>Fingerprint of the admin definition that seeded the view (#69 freshness
        /// context on diagnostics payloads); empty for standalone items.</summary>
        internal string WorkspaceFingerprint { get; private set; }

        internal static bool TryCreate(bool proxyEnabled, string canonicalTargetsProperty, string workspaceItemId, string workspaceFingerprint, out ProxyWorkspaceProfile profile)
        {
            return TryCreate(proxyEnabled, canonicalTargetsProperty, workspaceItemId, workspaceFingerprint, out profile, null);
        }

        /// <param name="vmsFloorHosts">#73: the VMS host names the host code resolved from
        /// EnvironmentManager; null/empty just means the matcher's syntax floor (IPs, localhost).</param>
        internal static bool TryCreate(
            bool proxyEnabled,
            string canonicalTargetsProperty,
            string workspaceItemId,
            string workspaceFingerprint,
            out ProxyWorkspaceProfile profile,
            IEnumerable<string> vmsFloorHosts)
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
                // #73: TryNormalizeTarget accepts both the exact-origin shape and wildcard
                // patterns, so a seeded list carrying "https://*.tail" survives seeding intact.
                if (ProxyPolicy.TryNormalizeTarget(entry, out var normalized) && seen.Add(normalized))
                {
                    origins.Add(normalized);
                }
            }

            var floorHosts = new List<string>();
            var seenFloor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in vmsFloorHosts ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(host) && seenFloor.Add(host.Trim().ToLowerInvariant()))
                {
                    floorHosts.Add(host.Trim().ToLowerInvariant());
                }
            }

            // An enabled proxy always fronts the browser: an empty usable list refuses everything,
            // which keeps the "never direct" promise for corrupt or half-configured definitions.
            profile = new ProxyWorkspaceProfile
            {
                CanonicalTargets = origins,
                VmsFloorHosts = floorHosts,
                WorkspaceItemId = workspaceItemId ?? string.Empty,
                WorkspaceFingerprint = workspaceFingerprint ?? string.Empty,
            };
            return true;
        }
    }
}
