using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// Server-side hardening for the Event Server fetcher (#54): a fetch is only served when its
    /// origin appears in the union of every proxied workspace's admin allowlist, so the fetcher
    /// can no longer be driven at arbitrary targets of the server network. The workspace snapshot
    /// is re-resolved through a caller-supplied seam (the MIP configuration API in production)
    /// and kept across transient resolution failures; before the first successful resolve the
    /// gate is closed.
    /// </summary>
    internal sealed class ProxyServerAllowlist
    {
        private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(60);

        private readonly Func<IEnumerable<Dictionary<string, string>>> _snapshotResolver;
        private readonly Action<string> _log;
        private readonly object _sync = new object();
        private Snapshot _snapshot; // null until the first successful resolve
        private DateTime _nextRefreshUtc = DateTime.MinValue;

        /// <summary>One refresh's union: allowlisted origins, and the flagged subset whose
        /// entries carry the #56 per-target accept-untrusted-certificate override.</summary>
        private sealed class Snapshot
        {
            internal readonly HashSet<string> Origins;
            internal readonly HashSet<string> UntrustedOrigins;

            internal Snapshot(HashSet<string> origins, HashSet<string> untrustedOrigins)
            {
                Origins = origins;
                UntrustedOrigins = untrustedOrigins;
            }
        }

        internal ProxyServerAllowlist(Func<IEnumerable<Dictionary<string, string>>> snapshotResolver, Action<string> log)
        {
            _snapshotResolver = snapshotResolver ?? throw new ArgumentNullException(nameof(snapshotResolver));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// True when the target origin is allowlisted by at least one proxied workspace. Refuses
        /// with an actionable reason when the gate is closed or the origin misses every list.
        /// </summary>
        internal bool IsAllowed(Uri target, out string refuseReason)
        {
            EnsureCurrent();

            lock (_sync)
            {
                if (_snapshot == null)
                {
                    refuseReason = "The proxy fetcher has not been able to read the Configuration of proxied workspaces, so its allowlist gate is closed.";
                    return false;
                }

                var origin = ProxyPolicy.CanonicalOrigin(target);
                if (_snapshot.Origins.Contains(origin))
                {
                    refuseReason = null;
                    return true;
                }

                refuseReason = $"The origin '{origin}' is not in the proxy allowlist of any proxied workspace.";
                return false;
            }
        }

        /// <summary>
        /// Ticket #56: True only when the allowlist entry for THIS origin carries the admin's
        /// accept-untrusted-certificate flag in at least one proxied workspace. An override for
        /// one target never reaches a neighbouring origin, and while the gate is closed (never
        /// resolved) no override exists — validation is the fail-closed default.
        /// </summary>
        internal bool AcceptsUntrustedCertificate(Uri target)
        {
            EnsureCurrent();

            lock (_sync)
            {
                return _snapshot != null &&
                       _snapshot.UntrustedOrigins.Contains(ProxyPolicy.CanonicalOrigin(target));
            }
        }

        private void EnsureCurrent()
        {
            var now = DateTime.UtcNow;
            lock (_sync)
            {
                if (now < _nextRefreshUtc)
                {
                    return;
                }

                _nextRefreshUtc = now.Add(DefaultRefreshInterval);
            }

            Snapshot resolved;
            try
            {
                resolved = BuildUnion(_snapshotResolver());
            }
            catch (Exception exception)
            {
                // Transient config-service failure: keep serving the last known allowlist rather
                // than flapping every proxied page. Never resolved at all stays closed.
                _log($"Proxy allowlist refresh failed: {exception.Message}");
                return;
            }

            lock (_sync)
            {
                _snapshot = resolved;
            }
        }

        private static Snapshot BuildUnion(IEnumerable<Dictionary<string, string>> workspaceProperties)
        {
            var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var untrustedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (workspaceProperties == null)
            {
                return new Snapshot(origins, untrustedOrigins);
            }

            foreach (var properties in workspaceProperties)
            {
                var config = WorkspaceConfiguration.FromProperties(properties);
                var settings = config?.GetFirstWebContentSettings();
                if (settings == null || !settings.ProxyEnabled || settings.ProxyTargets == null)
                {
                    continue;
                }

                foreach (var target in settings.ProxyTargets)
                {
                    if (target != null &&
                        ProxyPolicy.TryNormalizeTargetParts(target.Scheme, target.Host, target.Port, out var origin))
                    {
                        origins.Add(origin);
                        if (target.AcceptUntrusted)
                        {
                            untrustedOrigins.Add(origin);
                        }
                    }
                }
            }

            return new Snapshot(origins, untrustedOrigins);
        }
    }
}
