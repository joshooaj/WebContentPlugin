using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace WebContent.Admin
{
    /// <summary>
    /// #78: why a rule-trigger row is refused. The part-level split (url vs method vs status)
    /// is what lets the grid's Problem cell name the exact member the Event Server's
    /// <see cref="ProxyTriggerPolicy"/> would refuse, instead of one undifferentiated "invalid".
    /// </summary>
    internal enum ProxyTriggerRowError
    {
        None = 0,
        UrlRequired,
        UrlInvalid,
        MethodInvalid,
        StatusInvalid,
    }

    /// <summary>
    /// #78: one rule-trigger row of the Management Client's Proxy tab "Rule triggers" grid — a
    /// URL glob, a method filter, and a response-status filter. This is the single storage-facing
    /// shape of a configured trigger row. Like the header grids above it, the grammar is NOT
    /// duplicated here: a row is valid exactly when <see cref="ProxyTriggerPolicy"/>'s three
    /// normalizers accept its members, so the MC save refusal and the Event Server's snapshot-time
    /// refusal (and its MIP.log audit line) can never disagree about what a legal row is. Pure
    /// (no WPF types) so the unit tests link it and pin validation without the MC shell.
    /// </summary>
    internal sealed class ProxyTriggerEditorRow : INotifyPropertyChanged
    {
        private string _url = string.Empty;
        private string _method = ProxyTriggerPolicy.HostWildcard;
        private string _status = string.Empty;
        private ProxyTriggerRowError _errorCode;
        private string _errorText;
        private ProxyTriggerPolicy.TriggerRow _row;

        public event PropertyChangedEventHandler PropertyChanged;

        public string Url
        {
            get => _url;
            set
            {
                var text = value ?? string.Empty;
                if (text == _url) return;
                _url = text;
                Raise(nameof(Url));
                Validate();
            }
        }

        /// <summary>
        /// Method filter as the combo carries it: the verb tokens the proxy actually relays, or
        /// "*" for any method. The grammar itself accepts any single verb token; the dropdown
        /// deliberately offers only the relayed set because a row naming anything else can never
        /// match real traffic (the fetch path refuses those verbs outright).
        /// </summary>
        public string Method
        {
            get => _method;
            set
            {
                var verb = value ?? string.Empty;
                if (verb == _method) return;
                _method = verb;
                Raise(nameof(Method));
                Validate();
            }
        }

        /// <summary>Status filter: blank = any, "1xx".."5xx" class, or an exact 100-599 code.</summary>
        public string Status
        {
            get => _status;
            set
            {
                var text = value ?? string.Empty;
                if (text == _status) return;
                _status = text;
                Raise(nameof(Status));
                Validate();
            }
        }

        /// <summary>Why this row is refused (<see cref="ProxyTriggerRowError.None"/> when valid
        /// or blank). The XAML row style keys its error highlight off this.</summary>
        public ProxyTriggerRowError ErrorCode
        {
            get => _errorCode;
            private set
            {
                if (value == _errorCode) return;
                _errorCode = value;
                Raise(nameof(ErrorCode));
                Raise(nameof(HasError));
            }
        }

        public bool HasError => _errorCode != ProxyTriggerRowError.None;

        /// <summary>The localized Problem-column text — a view concern the WPF page writes; the
        /// editor logic and the unit tests read <see cref="ErrorCode"/> instead.</summary>
        public string ErrorText
        {
            get => _errorText;
            set
            {
                if (value == _errorText) return;
                _errorText = value;
                Raise(nameof(ErrorText));
            }
        }

        /// <summary>
        /// The fixed XProtect event type every matching trigger raises (read-only Event column).
        /// One event type for the whole plugin (#65 design): per-row granularity lives in the
        /// matcher's context, and the admin finds this exact name in the rule builder.
        /// </summary>
        public string Event => ProxyTriggerPolicy.EventType;

        /// <summary>An untouched row: no url and no status, and the method dropdown left on the
        /// any-method "*". Blank rows are not triggers — they are skipped on save (a status or a
        /// verb without a url is a refusal, not a blank).</summary>
        public bool IsEmpty =>
            (_url ?? string.Empty).Trim().Length == 0 &&
            (_status ?? string.Empty).Trim().Length == 0 &&
            IsAnyMethod(_method);

        internal ProxyTriggerPolicy.TriggerRow Row => _row;

        /// <summary>Renders the row for the save-refusal dialog and the save audit line:
        /// "url=&lt;url&gt; method=&lt;verb|any&gt; status=&lt;filter|any&gt;".</summary>
        public string Describe()
        {
            var url = (_url ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                url = _url ?? string.Empty;
            }

            var method = IsAnyMethod(_method) ? "any" : _method.Trim();
            var status = (_status ?? string.Empty).Trim();
            if (status.Length == 0)
            {
                status = "any";
            }

            return $"url={url} method={method} status={status}";
        }

        /// <summary>
        /// Builds the stored record for this row (valid rows only). The wire keeps the admin's raw
        /// text (the Event Server re-validates on every read); the any-method "*" and a blank
        /// status store as null members so an untouched filter keeps the compact wire shape.
        /// </summary>
        internal bool TryToStoredRecord(out ProxyTriggerConfiguration record)
        {
            record = null;
            if (_row == null)
            {
                return false;
            }

            record = new ProxyTriggerConfiguration
            {
                Url = (_url ?? string.Empty).Trim(),
                Method = IsAnyMethod(_method) ? null : _method.Trim(),
                Status = string.IsNullOrWhiteSpace(_status) ? null : _status.Trim(),
            };
            return true;
        }

        /// <summary>The method token the dropdown stores for a stored row with no method member.</summary>
        internal static string MethodForStored(string method) =>
            IsAnyMethod(method) ? ProxyTriggerPolicy.HostWildcard : method.Trim().ToUpperInvariant();

        internal static bool IsAnyMethod(string method)
        {
            var text = (method ?? string.Empty).Trim();
            return text.Length == 0 || text == ProxyTriggerPolicy.HostWildcard;
        }

        private void Validate()
        {
            _row = null;

            if (IsEmpty)
            {
                ErrorCode = ProxyTriggerRowError.None;
                return;
            }

            // THE grammar gate: the exact normalizers the Event Server's matcher runs on stored
            // rows, member by member so the Problem cell names the failing member. A row this grid
            // saves is a row the fetch path honours; a row it highlights is a row the server would
            // refuse (and audit) anyway.
            var text = (_url ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                ErrorCode = ProxyTriggerRowError.UrlRequired;
                return;
            }

            ProxyTriggerPolicy.TriggerRow parsed;
            string urlError;
            if (!ProxyTriggerPolicy.TryNormalizeUrlPattern(text, out parsed, out urlError))
            {
                ErrorCode = ProxyTriggerRowError.UrlInvalid;
                return;
            }

            string method;
            string methodError;
            if (!ProxyTriggerPolicy.TryNormalizeMethodFilter(_method, out method, out methodError))
            {
                ErrorCode = ProxyTriggerRowError.MethodInvalid;
                return;
            }

            int statusExact;
            int statusClass;
            string statusError;
            if (!ProxyTriggerPolicy.TryNormalizeStatusFilter(_status, out statusExact, out statusClass, out statusError))
            {
                ErrorCode = ProxyTriggerRowError.StatusInvalid;
                return;
            }

            parsed.RawMethod = _method;
            parsed.RawStatus = _status;
            parsed.Method = method;
            parsed.StatusExact = statusExact;
            parsed.StatusClass = statusClass;
            _row = parsed;
            ErrorCode = ProxyTriggerRowError.None;
        }

        private void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// #78: the row collection behind the Proxy tab's "Rule triggers" grid, and the place that
    /// turns rows into stored <see cref="ProxyTriggerConfiguration"/> records. An invalid row
    /// refuses the whole save by name (never silently dropped), and rows keep configuration order —
    /// the Event Server evaluates them top-down and one fetch raises at most the FIRST matching
    /// row's event, so reordering is a real configuration change, here and on the server.
    /// </summary>
    internal sealed class ProxyTriggerEditor
    {
        public ObservableCollection<ProxyTriggerEditorRow> Rows { get; } = new ObservableCollection<ProxyTriggerEditorRow>();

        public int Count => Rows.Count;

        public ProxyTriggerEditorRow AddBlankRow()
        {
            var row = new ProxyTriggerEditorRow();
            Rows.Add(row);
            return row;
        }

        public bool Remove(ProxyTriggerEditorRow row) => row != null && Rows.Remove(row);

        public void Clear() => Rows.Clear();

        /// <summary>
        /// Renders stored rows into grid rows. Rows too malformed for the grammar (the Event Server
        /// would refuse them anyway) or unrenderable by the method dropdown (a verb the proxy never
        /// relays can never match) are skipped rather than rendered as poisoned rows — the count is
        /// returned so the caller can log them, matching the allowlist and header grids.
        /// </summary>
        public int Load(IEnumerable<ProxyTriggerConfiguration> stored)
        {
            Rows.Clear();
            if (stored == null)
            {
                return 0;
            }

            var dropped = 0;
            foreach (var record in stored)
            {
                ProxyTriggerPolicy.TriggerRow parsed;
                string error;
                if (record == null || !ProxyTriggerPolicy.TryBuildRow(record, out parsed, out error) || !CanRenderMethod(record.Method))
                {
                    dropped++;
                    continue;
                }

                Rows.Add(new ProxyTriggerEditorRow
                {
                    Url = record.Url ?? string.Empty,
                    Method = ProxyTriggerEditorRow.MethodForStored(record.Method),
                    Status = record.Status ?? string.Empty,
                });
            }

            return dropped;
        }

        /// <summary>
        /// Turns the current rows into the records a Save writes. Blank rows are skipped; any
        /// invalid row refuses the whole build, with a description per invalid row so the dialog
        /// can name the row the highlighted grid already shows. Configuration order is preserved.
        /// </summary>
        public bool TryBuildStoredTriggers(out List<ProxyTriggerConfiguration> triggers, out List<string> rejectedDescriptions)
        {
            return TryBuildStoredTriggers(Rows, out triggers, out rejectedDescriptions);
        }

        /// <summary>The same build over any row sequence — the static seam the unit tests pin
        /// without needing a live collection.</summary>
        internal static bool TryBuildStoredTriggers(
            IEnumerable<ProxyTriggerEditorRow> rows,
            out List<ProxyTriggerConfiguration> triggers,
            out List<string> rejectedDescriptions)
        {
            triggers = new List<ProxyTriggerConfiguration>();
            rejectedDescriptions = new List<string>();

            foreach (var row in rows)
            {
                if (row == null || row.IsEmpty)
                {
                    continue;
                }

                if (row.ErrorCode != ProxyTriggerRowError.None || !row.TryToStoredRecord(out var record))
                {
                    rejectedDescriptions.Add(row.Describe());
                    continue;
                }

                triggers.Add(record);
            }

            if (rejectedDescriptions.Count > 0)
            {
                // Nothing partial is ever handed back: a refused build yields an empty list, so a
                // caller that ignores the return value still cannot store half a trigger list.
                triggers = new List<ProxyTriggerConfiguration>();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Renders stored records as admin-facing lines for the save's audit log — the MC MIP.log
        /// stays the storage-side view of the grid that does not depend on how WPF surfaces
        /// through accessibility.
        /// </summary>
        internal static string DescribeStoredTriggers(IEnumerable<ProxyTriggerConfiguration> triggers)
        {
            var parts = new List<string>();
            foreach (var trigger in triggers ?? new List<ProxyTriggerConfiguration>())
            {
                if (trigger == null)
                {
                    continue;
                }

                var method = string.IsNullOrWhiteSpace(trigger.Method) ? "any" : trigger.Method.Trim();
                var status = string.IsNullOrWhiteSpace(trigger.Status) ? "any" : trigger.Status.Trim();
                parts.Add($"url={trigger.Url} method={method} status={status}");
            }

            return string.Join("; ", parts);
        }

        // The method dropdown renders only the any-method "*" plus the verbs the proxy relays; a
        // stored row naming anything else passed the grammar but can never match real traffic, so
        // the grid drops it (with a counted log line) exactly like the header grids drop
        // unrenderable stored rows.
        private static bool CanRenderMethod(string method)
        {
            if (ProxyTriggerEditorRow.IsAnyMethod(method))
            {
                return true;
            }

            var upper = method.Trim().ToUpperInvariant();
            foreach (var verb in ProxyTriggerMethods)
            {
                if (string.Equals(verb, upper, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The method dropdown's verb set — exactly the verbs the proxy fetch path relays
        /// (ProxyForwardPolicy's decision table). A trigger for anything else could never match,
        /// so the grid never offers it. Index 0 is the any-method "*".
        /// </summary>
        internal static readonly string[] ProxyTriggerMethods =
        {
            "*",
            "GET",
            "HEAD",
            "POST",
            "PUT",
            "DELETE",
            "OPTIONS",
            "PATCH",
        };
    }
}
