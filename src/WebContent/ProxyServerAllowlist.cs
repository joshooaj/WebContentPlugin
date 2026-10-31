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
        private HashSet<string> _origins; // null until the first successful resolve
        private DateTime _nextRefreshUtc = DateTime.MinValue;

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
                if (_origins == null)
                {
                    refuseReason = "The proxy fetcher has not been able to read the Configuration of proxied workspaces, so its allowlist gate is closed.";
                    return false;
                }

                var origin = ProxyPolicy.CanonicalOrigin(target);
                if (_origins.Contains(origin))
                {
                    refuseReason = null;
                    return true;
                }

                refuseReason = $"The origin '{origin}' is not in the proxy allowlist of any proxied workspace.";
                return false;
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

            HashSet<string> resolved;
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
                _origins = resolved;
            }
        }

        private static HashSet<string> BuildUnion(IEnumerable<Dictionary<string, string>> workspaceProperties)
        {
            var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (workspaceProperties == null)
            {
                return origins;
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
                    }
                }
            }

            return origins;
        }
    }
}
