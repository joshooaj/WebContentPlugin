using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// One workspace item as the trigger store sees it: the item id the fetch payloads attribute
    /// traffic to, the display name the raised event carries, and the raw property dictionary the
    /// <c>WorkspaceConfig</c> blob lives in. Built by the Event Server plugin's composition root
    /// from the MIP Configuration API; tests hand-build it.
    /// </summary>
    internal sealed class ProxyWorkspaceTriggerSource
    {
        internal string ItemId;
        internal string Name;
        internal Dictionary<string, string> Properties;
    }

    /// <summary>
    /// Ticket #65: the Event Server's per-workspace proxy-trigger row store. Mirrors
    /// <see cref="ProxyServerAllowlist"/>'s resilience policy exactly: the workspace snapshot is
    /// re-resolved through a caller-supplied seam at most every 60 s, a transient configuration
    /// failure keeps serving the last-known-good rows, and before the first successful resolve
    /// there is nothing to match against — every fetch evaluates as no-match (closed). Unlike the
    /// allowlist, closed here means "no events", never "no traffic": a configuration fault must
    /// not change what is served, only what is reported.
    ///
    /// Rows parse through <see cref="ProxyTriggerPolicy"/>; a row outside the grammar is refused
    /// per refresh with one actionable audit line and never matches. Workspaces whose definition
    /// carries no <c>proxyTriggers</c> section — every definition saved before #65 — load as
    /// "no rows" without an audit line: the readers are tolerant of its absence by design.
    /// </summary>
    internal sealed class ProxyTriggerMatcher
    {
        private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(60);

        private readonly Func<IEnumerable<ProxyWorkspaceTriggerSource>> _snapshotResolver;
        private readonly Action<string> _log;
        private readonly object _sync = new object();
        private Dictionary<string, WorkspaceTriggers> _snapshot; // null until the first successful resolve
        private DateTime _nextRefreshUtc = DateTime.MinValue;

        private sealed class WorkspaceTriggers
        {
            internal string Name;
            internal readonly List<ProxyTriggerPolicy.TriggerRow> Rows = new List<ProxyTriggerPolicy.TriggerRow>();
        }

        /// <summary>One match's outcome: the triggering workspace and row, enough context for the
        /// raised event's fixed properties and the audit line.</summary>
        internal sealed class TriggerMatch
        {
            internal string WorkspaceItemId;
            internal string WorkspaceName;
            internal string RowDescription;
        }

        internal ProxyTriggerMatcher(Func<IEnumerable<ProxyWorkspaceTriggerSource>> snapshotResolver, Action<string> log)
        {
            _snapshotResolver = snapshotResolver ?? throw new ArgumentNullException(nameof(snapshotResolver));
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Evaluates one served fetch (url + method + actual response status) against the trigger
        /// rows of the workspace the request was attributed to. The first matching row wins — a
        /// fetch raises at most one event. No workspace id, an unconfigured workspace, rows the
        /// admin never defined, and the pre-first-resolve closed state all answer no-match.
        /// </summary>
        internal bool TryMatch(string workspaceItemId, Uri url, string method, int statusCode, out TriggerMatch match)
        {
            match = null;

            if (string.IsNullOrEmpty(workspaceItemId) || statusCode <= 0)
            {
                return false;
            }

            EnsureCurrent();

            lock (_sync)
            {
                if (_snapshot == null)
                {
                    return false;
                }

                WorkspaceTriggers triggers;
                if (!_snapshot.TryGetValue(workspaceItemId, out triggers) || triggers.Rows.Count == 0)
                {
                    return false;
                }

                foreach (var row in triggers.Rows)
                {
                    if (row.Matches(url, method, statusCode))
                    {
                        match = new TriggerMatch
                        {
                            WorkspaceItemId = workspaceItemId,
                            WorkspaceName = triggers.Name,
                            RowDescription = ProxyTriggerPolicy.Describe(row),
                        };
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Force one resolve at composition time (the Event Server plugin's Init): bad rows are
        /// audited in the log at startup rather than at an arbitrary first fetch, and the row
        /// counts below show what the configuration actually loaded.
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
                var rows = 0;
                var workspaces = 0;
                if (_snapshot != null)
                {
                    foreach (var pair in _snapshot)
                    {
                        if (pair.Value.Rows.Count > 0)
                        {
                            workspaces++;
                            rows += pair.Value.Rows.Count;
                        }
                    }
                }

                _log($"Workspace rows refreshed: {rows} row(s) across {workspaces} workspace(s).");
            }
        }

        /// <summary>Snapshot for diagnostics/tests: workspace item ids with rows loaded right now.</summary>
        internal IReadOnlyList<string> WorkspaceIdsWithRows()
        {
            EnsureCurrent();

            lock (_sync)
            {
                var ids = new List<string>();
                if (_snapshot != null)
                {
                    foreach (var pair in _snapshot)
                    {
                        if (pair.Value.Rows.Count > 0)
                        {
                            ids.Add(pair.Key);
                        }
                    }
                }

                return ids;
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

            Dictionary<string, WorkspaceTriggers> resolved;
            try
            {
                resolved = BuildSnapshot(_snapshotResolver());
            }
            catch (Exception exception)
            {
                // Transient config-service failure: keep serving the last known rows. Never
                // resolved at all just stays closed (no events).
                _log($"Proxy trigger row refresh failed: {exception.Message}");
                return;
            }

            lock (_sync)
            {
                _snapshot = resolved;
            }
        }

        private Dictionary<string, WorkspaceTriggers> BuildSnapshot(IEnumerable<ProxyWorkspaceTriggerSource> workspaces)
        {
            var snapshot = new Dictionary<string, WorkspaceTriggers>(StringComparer.OrdinalIgnoreCase);
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

                // Tolerant read: no WorkspaceConfig blob, no WebContent item, or no proxyTriggers
                // section (every pre-#65 definition) is simply "no rows".
                var settings = WorkspaceConfiguration.GetFirstWebContentSettings(workspace.Properties);
                var triggers = settings == null ? null : settings.ProxyTriggers;
                if (triggers == null || triggers.Count == 0)
                {
                    continue;
                }

                var entry = GetOrAdd(snapshot, workspace);
                foreach (var trigger in triggers)
                {
                    ProxyTriggerPolicy.TriggerRow row;
                    string error;
                    if (ProxyTriggerPolicy.TryBuildRow(trigger, out row, out error))
                    {
                        entry.Rows.Add(row);
                    }
                    else
                    {
                        // One audit line per refused row per refresh - actionable by workspace and
                        // raw text, mirroring how the #81 MC grid refuses saves by name.
                        _log($"Proxy trigger row refused for workspace '{workspace.Name}' ({workspace.ItemId}): {error}. Raw: url='{trigger.Url}' method='{trigger.Method}' status='{trigger.Status}'.");
                    }
                }
            }

            return snapshot;
        }

        private static WorkspaceTriggers GetOrAdd(Dictionary<string, WorkspaceTriggers> snapshot, ProxyWorkspaceTriggerSource workspace)
        {
            WorkspaceTriggers entry;
            if (!snapshot.TryGetValue(workspace.ItemId, out entry))
            {
                entry = new WorkspaceTriggers { Name = workspace.Name ?? workspace.ItemId };
                snapshot[workspace.ItemId] = entry;
            }

            return entry;
        }
    }
}