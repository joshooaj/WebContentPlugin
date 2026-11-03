using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// One workspace item as the header-policy store sees it: the item id fetch payloads are
    /// attributed to, the display name the audit lines carry, and the raw property dictionary the
    /// <c>WorkspaceConfig</c> blob lives in. Built by the Event Server plugin's composition root
    /// from the MIP Configuration API; tests hand-build it. Same shape as
    /// <see cref="ProxyWorkspaceCapSource"/> on purpose.
    /// </summary>
    internal sealed class ProxyWorkspaceHeaderSource
    {
        internal string ItemId;
        internal string Name;
        internal Dictionary<string, string> Properties;
    }

    /// <summary>
    /// Ticket #86: the Event Server's per-workspace proxy header policies for BOTH legs of the
    /// fetch. The snapshot policy mirrors <see cref="ProxyResponseCapStore"}: the workspace
    /// snapshot is re-resolved through a caller-supplied seam at most every 60 s and a transient
    /// configuration failure keeps serving the last-known-good rules.
    ///
    /// Rules are validated at snapshot time through <see cref="ProxyHeaderPolicy"/>: a row whose
    /// name is forbidden or whose grammar fails is REFUSED (dropped from the snapshot) with one
    /// audit line per refused row per refresh naming the workspace and the raw text — that is the
    /// runtime guardrail the acceptance asks for: a hand-edited stored configuration can never
    /// put <c>Host:</c> or <c>Content-Length:</c> on the wire, it only earns a log line.
    /// </summary>
    internal sealed class ProxyHeaderPolicyStore
    {
        private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(60);

        private readonly Func<IEnumerable<ProxyWorkspaceHeaderSource>> _snapshotResolver;
        private readonly Action<string> _log;
        private readonly object _sync = new object();
        private Dictionary<string, WorkspaceHeaderPolicy> _snapshot; // null until the first successful resolve
        private DateTime _nextRefreshUtc = DateTime.MinValue;

        internal ProxyHeaderPolicyStore(Func<IEnumerable<ProxyWorkspaceHeaderSource>> snapshotResolver, Action<string> log)
        {
            _snapshotResolver = snapshotResolver ?? throw new ArgumentNullException(nameof(snapshotResolver));
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// The effective header policy for the workspace a fetch is attributed to. Unknown item
        /// and the never-resolved store both answer an empty policy (plain pass-through besides
        /// the unconditional hop-by-hop stripping) — a configuration fault never changes what is
        /// served beyond the no-policy posture.
        /// </summary>
        internal WorkspaceHeaderPolicy Resolve(string workspaceItemId)
        {
            EnsureCurrent();

            lock (_sync)
            {
                WorkspaceHeaderPolicy policy;
                if (_snapshot == null || string.IsNullOrEmpty(workspaceItemId) ||
                    !_snapshot.TryGetValue(workspaceItemId, out policy))
                {
                    return WorkspaceHeaderPolicy.Empty;
                }

                return policy;
            }
        }

        /// <summary>
        /// Force one resolve at composition time (the Event Server plugin's Init): refused stored
        /// rows are audited at startup rather than at an arbitrary first fetch, and the counts
        /// show what the configuration actually loaded.
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
                var configured = 0;
                if (_snapshot != null)
                {
                    foreach (var pair in _snapshot)
                    {
                        if (pair.Value.RequestRules.Count > 0 || pair.Value.ResponseRules.Count > 0)
                        {
                            configured++;
                        }
                    }
                }

                _log($"Workspace header policies refreshed: {configured} workspace(s) with header rules.");
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

            Dictionary<string, WorkspaceHeaderPolicy> resolved;
            try
            {
                resolved = BuildSnapshot(_snapshotResolver());
            }
            catch (Exception exception)
            {
                // Transient config-service failure: keep serving the last known policies. Never
                // resolved at all just keeps every workspace at the no-policy posture.
                _log($"Proxy header policy refresh failed: {exception.Message}");
                return;
            }

            lock (_sync)
            {
                _snapshot = resolved;
            }
        }

        private Dictionary<string, WorkspaceHeaderPolicy> BuildSnapshot(IEnumerable<ProxyWorkspaceHeaderSource> workspaces)
        {
            var snapshot = new Dictionary<string, WorkspaceHeaderPolicy>(StringComparer.OrdinalIgnoreCase);
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

                // Tolerant read: no WorkspaceConfig blob, no WebContent item, or no header
                // sections (every pre-#86 definition) is simply "no policy" for that leg.
                var settings = WorkspaceConfiguration.GetFirstWebContentSettings(workspace.Properties);
                if (settings == null)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(workspace.Name) ? workspace.ItemId : workspace.Name;
                var request = BuildRules(name, workspace.ItemId, "request", settings.ProxyRequestHeaders);
                var response = BuildRules(name, workspace.ItemId, "response", settings.ProxyResponseHeaders);
                if (request.Count == 0 && response.Count == 0)
                {
                    continue;
                }

                snapshot[workspace.ItemId] = new WorkspaceHeaderPolicy
                {
                    RequestRules = request,
                    ResponseRules = response,
                };
            }

            return snapshot;
        }

        private List<ProxyHeaderPolicy.HeaderRule> BuildRules(string workspaceName, string itemId, string leg, List<ProxyHeaderRuleConfiguration> rows)
        {
            var rules = new List<ProxyHeaderPolicy.HeaderRule>();
            if (rows == null)
            {
                return rules;
            }

            foreach (var row in rows)
            {
                if (row == null)
                {
                    continue;
                }

                ProxyHeaderPolicy.HeaderRule rule;
                string error;
                if (!ProxyHeaderPolicy.TryBuildRule(row.Name, row.Value, row.Remove, out rule, out error))
                {
                    // One audit line per refused row per refresh - the runtime guardrail is
                    // observable by name, mirroring how over-ceiling caps are audited.
                    var raw = (row.Remove ? "-" : string.Empty) + (row.Name ?? string.Empty) +
                              (row.Remove || string.IsNullOrEmpty(row.Value) ? string.Empty : "=" + row.Value);
                    _log($"Proxy {leg} header rule for workspace '{workspaceName}' ({itemId}) refused: {error} (stored: '{raw}')");
                    continue;
                }

                rules.Add(rule);
            }

            return rules;
        }
    }

    /// <summary>
    /// The validated, ready-to-apply header policy for one workspace: request-leg rules and
    /// response-leg rules, both in configuration order.
    /// </summary>
    internal sealed class WorkspaceHeaderPolicy
    {
        internal static readonly WorkspaceHeaderPolicy Empty = new WorkspaceHeaderPolicy
        {
            RequestRules = new List<ProxyHeaderPolicy.HeaderRule>(),
            ResponseRules = new List<ProxyHeaderPolicy.HeaderRule>(),
        };

        internal List<ProxyHeaderPolicy.HeaderRule> RequestRules { get; set; }
        internal List<ProxyHeaderPolicy.HeaderRule> ResponseRules { get; set; }
    }
}
