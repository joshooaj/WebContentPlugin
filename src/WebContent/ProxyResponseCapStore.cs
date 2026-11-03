using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// One workspace item as the response-cap store sees it: the item id fetch payloads are
    /// attributed to, the display name the audit lines carry, and the raw property dictionary the
    /// <c>WorkspaceConfig</c> blob lives in. Built by the Event Server plugin's composition root
    /// from the MIP Configuration API; tests hand-build it.
    /// </summary>
    internal sealed class ProxyWorkspaceCapSource
    {
        internal string ItemId;
        internal string Name;
        internal Dictionary<string, string> Properties;
    }

    /// <summary>
    /// Ticket #85: the Event Server's per-workspace max-response-bytes caps. The snapshot policy
    /// mirrors <see cref="ProxyServerAllowlist"/> and <see cref="ProxyTriggerMatcher"/>: the
    /// workspace snapshot is re-resolved through a caller-supplied seam at most every 60 s and a
    /// transient configuration failure keeps serving the last-known-good caps. Unlike the
    /// allowlist there is no closed posture here: a workspace with no configured cap - every
    /// definition saved before #85, an unknown item, or a store that never resolved - means the
    /// 5 MB default, never a refusal. A configuration fault must not change what is served
    /// beyond the default posture.
    ///
    /// Stored values clamp on read: 0/absent/negative is the default, anything past the fixed
    /// 20 MB ceiling (<see cref="ProxyFetchLimits.MaxAllowedResponseBytes"/>) is the ceiling, so
    /// no workspace can obtain a cap above 20 MB regardless of what its stored config claims.
    /// An oversized stored value gets one audit line per refresh naming the workspace - that is
    /// the server-side proof the acceptance asks for, visible in the Event Server MIP log.
    /// </summary>
    internal sealed class ProxyResponseCapStore
    {
        private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(60);

        private readonly Func<IEnumerable<ProxyWorkspaceCapSource>> _snapshotResolver;
        private readonly Action<string> _log;
        private readonly object _sync = new object();
        private Dictionary<string, int> _snapshot; // null until the first successful resolve
        private DateTime _nextRefreshUtc = DateTime.MinValue;

        internal ProxyResponseCapStore(Func<IEnumerable<ProxyWorkspaceCapSource>> snapshotResolver, Action<string> log)
        {
            _snapshotResolver = snapshotResolver ?? throw new ArgumentNullException(nameof(snapshotResolver));
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// The effective fetch-response cap in bytes for the workspace a fetch is attributed to.
        /// Unknown item, no stored cap, a malformed (0/negative) value, and the never-resolved
        /// store all answer the 5 MB default; a stored cap answers itself clamped to the ceiling.
        /// </summary>
        internal int ResolveEffectiveBytes(string workspaceItemId)
        {
            EnsureCurrent();

            lock (_sync)
            {
                int stored;
                if (_snapshot == null ||
                    string.IsNullOrEmpty(workspaceItemId) ||
                    !_snapshot.TryGetValue(workspaceItemId, out stored))
                {
                    return ProxyFetchLimits.DefaultMaxResponseBytes;
                }

                return ProxyFetchLimits.NormalizeResponseCapBytes(stored);
            }
        }

        /// <summary>
        /// Force one resolve at composition time (the Event Server plugin's Init): oversized
        /// hand-edited values are audited at startup rather than at an arbitrary first fetch,
        /// and the counts show what the configuration actually loaded.
        /// </summary>
        internal void Prime()
        {
            lock (_sync)
            {
                _nextRefreshUtc = DateTime.MinValue;
            }

            EnsureCurrent();

            lock (_sync)
            {
                var capped = 0;
                if (_snapshot != null)
                {
                    foreach (var pair in _snapshot)
                    {
                        if (pair.Value > 0)
                        {
                            capped++;
                        }
                    }
                }

                _log($"Workspace response caps refreshed: {capped} capped workspace(s); default {ProxyFetchLimits.DefaultMaxResponseBytes} bytes, ceiling {ProxyFetchLimits.MaxAllowedResponseBytes} bytes.");
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

            Dictionary<string, int> resolved;
            try
            {
                resolved = BuildSnapshot(_snapshotResolver());
            }
            catch (Exception exception)
            {
                // Transient config-service failure: keep serving the last known caps. Never
                // resolved at all just keeps every workspace at the default posture.
                _log($"Proxy response cap refresh failed: {exception.Message}");
                return;
            }

            lock (_sync)
            {
                _snapshot = resolved;
            }
        }

        private Dictionary<string, int> BuildSnapshot(IEnumerable<ProxyWorkspaceCapSource> workspaces)
        {
            var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (workspaces == null)
            {
                return snapshot;
            }

            foreach (var workspace in workspaces)
            {
                if (workspace == null || string.IsNullOrEmpty(workspace.ItemId))
                {
                    continue;
                }

                // Tolerant read: no WorkspaceConfig blob, no WebContent item, or no cap member
                // (every pre-#85 definition) is simply "the default" - no snapshot entry, no
                // audit line.
                var settings = WorkspaceConfiguration.GetFirstWebContentSettings(workspace.Properties);
                var stored = settings == null ? 0 : settings.ProxyMaxResponseBytes;
                if (stored <= 0)
                {
                    continue;
                }

                if (stored > ProxyFetchLimits.MaxAllowedResponseBytes)
                {
                    // One audit line per oversized workspace per refresh - the server-side clamp
                    // is observable by name, mirroring how refused trigger rows are audited.
                    var name = string.IsNullOrWhiteSpace(workspace.Name) ? workspace.ItemId : workspace.Name;
                    _log($"Proxy response cap for workspace '{name}' ({workspace.ItemId}) exceeds the {ProxyFetchLimits.MaxAllowedResponseBytes} byte ceiling ({stored} stored); serving the ceiling.");
                    stored = ProxyFetchLimits.MaxAllowedResponseBytes;
                }

                snapshot[workspace.ItemId] = stored;
            }

            return snapshot;
        }
    }
}
