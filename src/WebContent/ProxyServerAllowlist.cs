using System;
using System.Collections.Generic;
using System.Net.Security;

namespace WebContent
{
    /// <summary>
    /// Server-side hardening for the Event Server fetcher (#54): a fetch is only served when its
    /// origin appears in the union of every proxied workspace's admin allowlist, so the fetcher
    /// can no longer be driven at arbitrary targets of the server network. The workspace snapshot
    /// is re-resolved through a caller-supplied seam (the MIP configuration API in production)
    /// and kept across transient resolution failures; before the first successful resolve the
    /// gate is closed. Since #73 the union carries structured pattern entries and every decision
    /// goes through the one shared <see cref="ProxyPolicy"/> matcher - including the wildcard
    /// SSRF floor, fed with the VMS's own host names through a second seam. This gate is also
    /// the minting gate the leaf issuer (<see cref="ProxyLeafIssuer"/>) consults, so fetch and
    /// mint stay one policy.
    /// </summary>
    internal sealed class ProxyServerAllowlist
    {
        private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(60);

        private readonly Func<IEnumerable<Dictionary<string, string>>> _snapshotResolver;
        private readonly Func<IEnumerable<string>> _vmsFloorHostsResolver;
        private readonly Action<string> _log;
        private readonly object _sync = new object();
        private Snapshot _snapshot; // null until the first successful resolve
        private IReadOnlyList<string> _floorHosts = new string[0];
        private DateTime _nextRefreshUtc = DateTime.MinValue;

        /// <summary>One refresh's union: allowlisted canonical entries (exact origins or #73
        /// wildcard patterns), and the flagged subset whose entries carry the #56 per-target
        /// accept-untrusted-certificate override.</summary>
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
            : this(snapshotResolver, log, null)
        {
        }

        /// <param name="vmsFloorHostsResolver">#73: supplies the VMS's own host names (Management
        /// Server / API Gateway) that wildcard entries must never match. A resolver failure
        /// keeps the last known floor rather than silently widening every wildcard.</param>
        internal ProxyServerAllowlist(
            Func<IEnumerable<Dictionary<string, string>>> snapshotResolver,
            Action<string> log,
            Func<IEnumerable<string>> vmsFloorHostsResolver)
        {
            _snapshotResolver = snapshotResolver ?? throw new ArgumentNullException(nameof(snapshotResolver));
            _vmsFloorHostsResolver = vmsFloorHostsResolver;
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// True when the target origin is allowlisted by at least one proxied workspace - through
        /// the shared #73 matcher, so an exact entry matches exactly and a wildcard entry matches
        /// with the SSRF floor applied. Refuses with an actionable reason when the gate is closed
        /// or the origin misses every list.
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

                if (ProxyPolicy.IsRequestAllowed(_snapshot.Origins, target, _floorHosts))
                {
                    refuseReason = null;
                    return true;
                }

                var origin = ProxyPolicy.CanonicalOrigin(target);
                refuseReason = $"The origin '{origin}' is not in the proxy allowlist of any proxied workspace.";
                return false;
            }
        }

        /// <summary>
        /// Ticket #56: True only when the allowlist entry for THIS origin carries the admin's
        /// accept-untrusted-certificate flag in at least one proxied workspace. An override for
        /// one target never reaches a neighbouring origin, and while the gate is closed (never
        /// resolved) no override exists — validation is the fail-closed default. #73: the entry
        /// may be a wildcard, and the same floor applies - a flagged wildcard never relaxes
        /// validation for a floored host.
        /// </summary>
        internal bool AcceptsUntrustedCertificate(Uri target)
        {
            EnsureCurrent();

            lock (_sync)
            {
                return _snapshot != null &&
                       ProxyPolicy.IsRequestAllowed(_snapshot.UntrustedOrigins, target, _floorHosts);
            }
        }

        /// <summary>
        /// Ticket #61: the certificate-validation posture for the Event Server's wss tunnel leg
        /// (see <see cref="ProxyTunnelServer"/>). Returns the validation callback to attach to
        /// THIS tunnel's own SslStream: the accept-untrusted posture (#56) only when the allowlist
        /// entry for exactly this origin carries the flag — the identical scoped decision the fetch
        /// path makes — and null otherwise, which is SslStream's default FULL chain validation,
        /// the exact #60 production posture. A flag for one target never reaches a neighbouring
        /// origin, a non-https tunnel target, the browser-side loopback termination, or the
        /// process-wide ServicePointManager. A closed gate, an unresolved config, or version skew
        /// that hides the flag all resolve to null: fail-closed.
        /// </summary>
        internal RemoteCertificateValidationCallback TunnelTlsValidationCallback(Uri target)
        {
            if (target == null || target.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            if (!AcceptsUntrustedCertificate(target))
            {
                return null;
            }

            return (sender, certificate, chain, sslPolicyErrors) => true;
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

            // #73: the wildcard floor. A resolver failure keeps the previous floor - widening
            // wildcards on a transient fault would be the one direction this must never flap.
            if (_vmsFloorHostsResolver != null)
            {
                try
                {
                    var hosts = new List<string>();
                    foreach (var host in _vmsFloorHostsResolver() ?? new string[0])
                    {
                        if (!string.IsNullOrWhiteSpace(host))
                        {
                            hosts.Add(host.Trim().ToLowerInvariant());
                        }
                    }

                    _floorHosts = hosts;
                }
                catch (Exception exception)
                {
                    _log($"Proxy allowlist VMS host floor refresh failed: {exception.Message}");
                }
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
                    // #73: EffectivePattern is the one reader seam - legacy host records and new
                    // pattern records canonicalize through the identical path.
                    if (target != null &&
                        ProxyPolicy.TryNormalizeTargetParts(target.Scheme, target.EffectivePattern, target.Port, out var origin))
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