using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace WebContent.Admin
{
    /// <summary>
    /// #81: why a grid row's entry is refused. The row model carries the code; the WPF page
    /// turns it into localized text for the inline Problem column and the save-refusal dialog.
    /// The grammar itself is NOT duplicated here: an entry is valid exactly when
    /// <see cref="ProxyPolicy.TryNormalizeTargetParts"/> accepts it, so the editor, the three
    /// gates, and the stored shape can never disagree about what a legal entry is.
    /// </summary>
    internal enum ProxyTargetRowError
    {
        None = 0,
        HostRequired = 1,
        PortInvalid = 2,
        HostOrPatternInvalid = 3,
    }

    /// <summary>
    /// #81: one allowlist row of the Management Client's structured Proxy-tab grid — the
    /// scheme dropdown value, the wildcard-aware host, the optional port, and the per-entry
    /// accept-untrusted checkbox. This is the single storage-facing shape of an allowlist entry:
    /// the free-text "origin + accept-untrusted token" line (#52/#56) is gone from the UI, and
    /// the flag lives on the row exactly where the stored record and the gates keep it.
    /// Pure (no WPF types) so the unit tests link it and pin validation without the MC shell.
    /// </summary>
    internal sealed class ProxyTargetEditorRow : INotifyPropertyChanged
    {
        /// <summary>The scheme the dropdown offers; the canonical set ProxyPolicy accepts.</summary>
        internal static readonly string[] Schemes = { Uri.UriSchemeHttp, Uri.UriSchemeHttps };

        /// <summary>A blank row starts on https — the same default the pre-#73 bare-host
        /// shorthand took, so an admin who only types a host means what they meant before.</summary>
        internal const string DefaultScheme = "https";

        private string _scheme = DefaultScheme;
        private string _host = string.Empty;
        private string _portText = string.Empty;
        private bool _acceptUntrusted;
        private ProxyTargetRowError _errorCode;
        private string _canonicalEntry;
        private string _errorText;

        public event PropertyChangedEventHandler PropertyChanged;

        public string Scheme
        {
            get => _scheme;
            set
            {
                var scheme = string.Equals(value, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    ? Uri.UriSchemeHttp
                    : Uri.UriSchemeHttps;
                if (scheme == _scheme) return;
                _scheme = scheme;
                Raise(nameof(Scheme));
                Validate();
            }
        }

        public string Host
        {
            get => _host;
            set
            {
                var host = value ?? string.Empty;
                if (host == _host) return;
                _host = host;
                Raise(nameof(Host));
                Validate();
            }
        }

        public string PortText
        {
            get => _portText;
            set
            {
                var port = value ?? string.Empty;
                if (port == _portText) return;
                _portText = port;
                Raise(nameof(PortText));
                Validate();
            }
        }

        public bool AcceptUntrusted
        {
            get => _acceptUntrusted;
            set
            {
                if (value == _acceptUntrusted) return;
                _acceptUntrusted = value;
                Raise(nameof(AcceptUntrusted));
            }
        }

        /// <summary>Why this row's entry is refused (<see cref="ProxyTargetRowError.None"/> when
        /// it is valid or blank). The XAML row style keys its error highlight off this.</summary>
        public ProxyTargetRowError ErrorCode
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

        public bool HasError => _errorCode != ProxyTargetRowError.None;

        /// <summary>The localized Problem-column text. Purely a view concern: the WPF page
        /// formats <see cref="ErrorCode"/> from <c>Strings</c> and writes it here; the editor
        /// logic and the unit tests never read it.</summary>
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

        /// <summary>An untouched row: no host and no port. Blank rows are not entries — they are
        /// skipped on save (and a blank host with a port typed in is a refusal, not a blank).</summary>
        public bool IsEmpty =>
            (_host ?? string.Empty).Trim().Length == 0 && (_portText ?? string.Empty).Trim().Length == 0;

        /// <summary>The full-host escape hatch: this row, if it saves, lets the Event Server
        /// fetch ANY host on its scheme (subject to the matcher's SSRF floor) — the page shows
        /// its warning banner while any row is one.</summary>
        public bool IsFullHostWildcard => (_host ?? string.Empty).Trim() == ProxyPolicy.HostWildcard;

        /// <summary>The canonical "scheme://pattern[:port]" the row stands for once valid;
        /// null while invalid or blank. Duplicate rows collapse on this string at save.</summary>
        internal string CanonicalEntry => _canonicalEntry;

        /// <summary>Renders the row the way the pre-#81 probe records wrote allowlist lines, so
        /// probe readbacks and log lines keep their shape: "scheme://host[:port] accept-untrusted".</summary>
        public string Describe()
        {
            var origin = _canonicalEntry ?? $"{_scheme}://{_host}{(_portText.Trim().Length > 0 ? ":" + _portText.Trim() : string.Empty)}";
            return ProxyPolicy.DescribeTargetEntry(origin, _acceptUntrusted);
        }

        /// <summary>Builds the stored record for this row (valid rows only): the canonical form
        /// split back into the structured scheme/pattern/port shape #73 writes, plus the flag.</summary>
        internal bool TryToStoredRecord(out ProxyTargetConfiguration record)
        {
            record = null;
            if (_canonicalEntry == null ||
                !ProxyPolicy.TrySplitCanonicalEntry(_canonicalEntry, out var scheme, out var pattern, out var port))
            {
                return false;
            }

            record = new ProxyTargetConfiguration
            {
                Scheme = scheme,
                Pattern = pattern,
                Port = port,
                AcceptUntrusted = _acceptUntrusted,
            };
            return true;
        }

        private void Validate()
        {
            _canonicalEntry = null;

            if (IsEmpty)
            {
                ErrorCode = ProxyTargetRowError.None;
                return;
            }

            if ((_host ?? string.Empty).Trim().Length == 0)
            {
                ErrorCode = ProxyTargetRowError.HostRequired;
                return;
            }

            var portText = (_portText ?? string.Empty).Trim();
            var port = 0;
            if (portText.Length > 0 &&
                (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 0 || port > 65535))
            {
                ErrorCode = ProxyTargetRowError.PortInvalid;
                return;
            }

            // THE grammar gate: exactly what the shared matcher's canonicalizer accepts, so an
            // entry the grid can save is an entry every gate can speak. Mid-label wildcards,
            // paths, stray spaces — everything outside the grammar lands here.
            if (!ProxyPolicy.TryNormalizeTargetParts(_scheme, _host, port, out var canonical))
            {
                ErrorCode = ProxyTargetRowError.HostOrPatternInvalid;
                return;
            }

            _canonicalEntry = canonical;
            ErrorCode = ProxyTargetRowError.None;
        }

        private void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// #81: the row collection behind the Management Client's structured allowlist grid, and the
    /// one place that turns rows into stored <see cref="ProxyTargetConfiguration"/> records. It
    /// replaces the free-text line parsing the old txtProxyTargets box pushed through
    /// <c>WorkspaceItemManager</c>: every entry the grid shows is editable, an invalid row is
    /// refused at save by name (never silently dropped), duplicates collapse onto one canonical
    /// record with any flagged duplicate flagging the entry (the #56/#73 posture carried over),
    /// and <see cref="Load"/> accepts both stored shapes forever (#73: legacy <c>host</c>
    /// records render into rows exactly like structured ones).
    /// </summary>
    internal sealed class ProxyAllowlistEditor
    {
        public ObservableCollection<ProxyTargetEditorRow> Rows { get; } = new ObservableCollection<ProxyTargetEditorRow>();

        public int Count => Rows.Count;

        /// <summary>Any row carrying the full-host escape hatch — the page's warning banner key.</summary>
        public bool HasFullHostWildcard
        {
            get
            {
                foreach (var row in Rows)
                {
                    if (row.IsFullHostWildcard)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public ProxyTargetEditorRow AddBlankRow()
        {
            var row = new ProxyTargetEditorRow();
            Rows.Add(row);
            return row;
        }

        public bool Remove(ProxyTargetEditorRow row) => row != null && Rows.Remove(row);

        public void Clear() => Rows.Clear();

        /// <summary>
        /// Renders stored records into rows. Invalid stored records (corrupt scheme/host/port)
        /// are skipped rather than rendered as poisoned rows — the count is returned so the
        /// caller can log them, matching what the old text-box fill did with the same records.
        /// </summary>
        public int Load(IEnumerable<ProxyTargetConfiguration> stored)
        {
            Rows.Clear();
            if (stored == null)
            {
                return 0;
            }

            var dropped = 0;
            foreach (var record in stored)
            {
                if (record == null ||
                    !ProxyPolicy.TryNormalizeTargetParts(record.Scheme, record.EffectivePattern, record.Port, out var canonical) ||
                    !ProxyPolicy.TrySplitCanonicalEntry(canonical, out var scheme, out var pattern, out var port))
                {
                    dropped++;
                    continue;
                }

                Rows.Add(new ProxyTargetEditorRow
                {
                    Scheme = scheme,
                    Host = pattern,
                    PortText = port == 0 ? string.Empty : port.ToString(CultureInfo.InvariantCulture),
                    AcceptUntrusted = record.AcceptUntrusted,
                });
            }

            return dropped;
        }

        /// <summary>
        /// Turns the current rows into the records a Save writes. Blank rows are skipped; any
        /// invalid row refuses the whole build, with a human description per invalid row so the
        /// dialog can name the entry the highlighted grid row already shows. Duplicates collapse
        /// onto one record and any flagged duplicate flags the entry (unchanged from #56/#73).
        /// The "proxy enabled with no targets" refusal stays with the caller: it is a policy
        /// about the toggle, not about the rows.
        /// </summary>
        public bool TryBuildStoredTargets(out List<ProxyTargetConfiguration> targets, out List<string> rejectedDescriptions)
        {
            return TryBuildStoredTargets(Rows, out targets, out rejectedDescriptions);
        }

        /// <summary>The same build over any row sequence — the static seam the unit tests pin
        /// without needing a live collection.</summary>
        internal static bool TryBuildStoredTargets(
            IEnumerable<ProxyTargetEditorRow> rows,
            out List<ProxyTargetConfiguration> targets,
            out List<string> rejectedDescriptions)
        {
            targets = new List<ProxyTargetConfiguration>();
            rejectedDescriptions = new List<string>();
            var order = new List<string>();
            var flagsByEntry = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (row == null || row.IsEmpty)
                {
                    continue;
                }

                if (row.ErrorCode != ProxyTargetRowError.None)
                {
                    rejectedDescriptions.Add(row.Describe());
                    continue;
                }

                if (!row.TryToStoredRecord(out var record))
                {
                    rejectedDescriptions.Add(row.Describe());
                    continue;
                }

                var entry = row.CanonicalEntry;
                if (!flagsByEntry.TryGetValue(entry, out var flagged))
                {
                    order.Add(entry);
                    flagged = false;
                }

                flagsByEntry[entry] = flagged || row.AcceptUntrusted;
            }

            if (rejectedDescriptions.Count > 0)
            {
                return false;
            }

            foreach (var entry in order)
            {
                if (!ProxyPolicy.TrySplitCanonicalEntry(entry, out var scheme, out var pattern, out var port))
                {
                    rejectedDescriptions.Add(entry);
                    continue;
                }

                targets.Add(new ProxyTargetConfiguration
                {
                    Scheme = scheme,
                    Pattern = pattern,
                    Port = port,
                    AcceptUntrusted = flagsByEntry[entry],
                });
            }

            return rejectedDescriptions.Count == 0;
        }

        /// <summary>
        /// Renders stored records as the admin-facing lines that produced them, for the save's audit
        /// log line. The MC log is the only storage view that does not depend on how the grid happens
        /// to surface through UIA, and live on #81 that difference mattered: a row the harness had
        /// set to http came back from the reloaded grid as https, and nothing in the log said which
        /// of the two the storage actually held. Unparseable records are named as such rather than
        /// dropped, so the line always describes what is really stored.
        /// </summary>
        internal static string DescribeStoredTargets(IEnumerable<ProxyTargetConfiguration> targets)
        {
            var parts = new List<string>();
            foreach (var target in targets ?? new List<ProxyTargetConfiguration>())
            {
                if (target == null)
                {
                    continue;
                }

                if (ProxyPolicy.TryNormalizeTargetParts(target.Scheme, target.EffectivePattern, target.Port, out var canonical))
                {
                    parts.Add(ProxyPolicy.DescribeTargetEntry(canonical, target.AcceptUntrusted));
                }
                else
                {
                    parts.Add($"{target.Scheme}://{target.EffectivePattern}:{target.Port} (unparseable)");
                }
            }

            return string.Join("; ", parts);
        }
    }
}
