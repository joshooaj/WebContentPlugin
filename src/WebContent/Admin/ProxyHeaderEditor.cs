using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace WebContent.Admin
{
    /// <summary>
    /// #86: one header-policy row of the Management Client's Proxy tab "Advanced: headers" grids —
    /// a header name, a value, and a strip checkbox. This is the single storage-facing shape of a
    /// configured header rule for BOTH legs (request and response). Like the allowlist grid above
    /// it, the grammar is NOT duplicated here: a row is valid exactly when
    /// <see cref="ProxyHeaderPolicy.ValidateRule"/> accepts it, so the MC save refusal and the
    /// Event Server's snapshot-time refusal (and its MIP.log audit line) can never disagree about
    /// what a legal row is. Pure (no WPF types) so the unit tests link it and pin validation
    /// without the MC shell.
    /// </summary>
    internal sealed class ProxyHeaderEditorRow : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _value = string.Empty;
        private bool _remove;
        private ProxyHeaderRuleError _errorCode;
        private string _errorText;
        private ProxyHeaderPolicy.HeaderRule _rule;

        public event PropertyChangedEventHandler PropertyChanged;

        public string Name
        {
            get => _name;
            set
            {
                var name = value ?? string.Empty;
                if (name == _name) return;
                _name = name;
                Raise(nameof(Name));
                Validate();
            }
        }

        public string Value
        {
            get => _value;
            set
            {
                var text = value ?? string.Empty;
                if (text == _value) return;
                _value = text;
                Raise(nameof(Value));
                Validate();
            }
        }

        /// <summary>Strip checkbox: the header never passes this leg, whatever the wire carried.</summary>
        public bool Remove
        {
            get => _remove;
            set
            {
                if (value == _remove) return;
                _remove = value;
                Raise(nameof(Remove));
                Validate();
            }
        }

        /// <summary>Why this row is refused (<see cref="ProxyHeaderRuleError.None"/> when valid
        /// or blank). The XAML row style keys its error highlight off this.</summary>
        public ProxyHeaderRuleError ErrorCode
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

        public bool HasError => _errorCode != ProxyHeaderRuleError.None;

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

        /// <summary>An untouched row: no name and no value. Blank rows are not rules — they are
        /// skipped on save (a value or strip tick without a name is a refusal, not a blank).</summary>
        public bool IsEmpty =>
            (_name ?? string.Empty).Trim().Length == 0 &&
            (_value ?? string.Empty).Trim().Length == 0 &&
            !_remove;

        internal ProxyHeaderPolicy.HeaderRule Rule => _rule;

        /// <summary>Renders the row for the save-refusal dialog and the save audit line:
        /// "X-Y: v" for add/override, "-X" for a strip row.</summary>
        public string Describe()
        {
            var name = (_name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                name = _name ?? string.Empty;
            }

            return _remove
                ? "-" + name
                : name + "=" + (_value ?? string.Empty);
        }

        /// <summary>Builds the stored record for this row (valid rows only).</summary>
        internal bool TryToStoredRecord(out ProxyHeaderRuleConfiguration record)
        {
            record = null;
            if (_rule == null)
            {
                return false;
            }

            record = new ProxyHeaderRuleConfiguration
            {
                Name = _rule.Name,
                Value = _rule.Value,
                Remove = _rule.Remove,
            };
            return true;
        }

        private void Validate()
        {
            _rule = null;

            if (IsEmpty)
            {
                ErrorCode = ProxyHeaderRuleError.None;
                return;
            }

            // THE grammar gate: exactly what the Event Server's snapshot build accepts, so a row
            // this grid can save is a row the fetch path will honour — and a row it highlights is
            // a row the server would refuse anyway (forbidden names, CR/LF values, junk tokens).
            var error = ProxyHeaderPolicy.ValidateRule(_name, _value, _remove);
            if (error != ProxyHeaderRuleError.None)
            {
                ErrorCode = error;
                return;
            }

            ProxyHeaderPolicy.TryBuildRule(_name, _value, _remove, out _rule);
            ErrorCode = ProxyHeaderRuleError.None;
        }

        private void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// #86: the row collection behind one "Advanced: headers" grid, and the place that turns rows
    /// into stored <see cref="ProxyHeaderRuleConfiguration"/> records. An invalid row refuses the
    /// whole save by name (never silently dropped), rows keep configuration order (for one header
    /// name, the LAST rule wins — the same order the Event Server applies), and blanks are skipped.
    /// </summary>
    internal sealed class ProxyHeaderEditor
    {
        public ObservableCollection<ProxyHeaderEditorRow> Rows { get; } = new ObservableCollection<ProxyHeaderEditorRow>();

        public int Count => Rows.Count;

        public ProxyHeaderEditorRow AddBlankRow()
        {
            var row = new ProxyHeaderEditorRow();
            Rows.Add(row);
            return row;
        }

        public bool Remove(ProxyHeaderEditorRow row) => row != null && Rows.Remove(row);

        public void Clear() => Rows.Clear();

        /// <summary>
        /// Renders stored rows into grid rows. Rows too malformed to round-trip are skipped rather
        /// than rendered as poisoned rows — the count is returned so the caller can log them,
        /// matching how the allowlist grid handles corrupt stored records.
        /// </summary>
        public int Load(IEnumerable<ProxyHeaderRuleConfiguration> stored)
        {
            Rows.Clear();
            if (stored == null)
            {
                return 0;
            }

            var dropped = 0;
            foreach (var record in stored)
            {
                if (record == null || ProxyHeaderPolicy.ValidateRule(record.Name, record.Value, record.Remove) != ProxyHeaderRuleError.None)
                {
                    dropped++;
                    continue;
                }

                Rows.Add(new ProxyHeaderEditorRow
                {
                    Name = record.Name,
                    Value = record.Remove ? string.Empty : record.Value ?? string.Empty,
                    Remove = record.Remove,
                });
            }

            return dropped;
        }

        /// <summary>
        /// Turns the current rows into the records a Save writes. Blank rows are skipped; any
        /// invalid row refuses the whole build, with a description per invalid row so the dialog
        /// can name the row the highlighted grid already shows.
        /// </summary>
        public bool TryBuildStoredRules(out List<ProxyHeaderRuleConfiguration> rules, out List<string> rejectedDescriptions)
        {
            return TryBuildStoredRules(Rows, out rules, out rejectedDescriptions);
        }

        /// <summary>The same build over any row sequence — the static seam the unit tests pin
        /// without needing a live collection.</summary>
        internal static bool TryBuildStoredRules(
            IEnumerable<ProxyHeaderEditorRow> rows,
            out List<ProxyHeaderRuleConfiguration> rules,
            out List<string> rejectedDescriptions)
        {
            rules = new List<ProxyHeaderRuleConfiguration>();
            rejectedDescriptions = new List<string>();

            foreach (var row in rows)
            {
                if (row == null || row.IsEmpty)
                {
                    continue;
                }

                if (row.ErrorCode != ProxyHeaderRuleError.None || !row.TryToStoredRecord(out var record))
                {
                    rejectedDescriptions.Add(row.Describe());
                    continue;
                }

                rules.Add(record);
            }

            return rejectedDescriptions.Count == 0;
        }

        /// <summary>
        /// Renders stored records as admin-facing lines for the save's audit log — the MC MIP.log
        /// stays the storage-side view of the grids that does not depend on how WPF surfaces
        /// through accessibility.
        /// </summary>
        internal static string DescribeStoredRules(IEnumerable<ProxyHeaderRuleConfiguration> rules)
        {
            var parts = new List<string>();
            foreach (var rule in rules ?? new List<ProxyHeaderRuleConfiguration>())
            {
                if (rule == null)
                {
                    continue;
                }

                parts.Add(rule.Remove
                    ? "-" + rule.Name
                    : rule.Name + "=" + rule.Value);
            }

            return string.Join("; ", parts);
        }
    }
}
