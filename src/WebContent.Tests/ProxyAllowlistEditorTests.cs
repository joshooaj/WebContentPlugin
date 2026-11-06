using System;
using System.Collections.Generic;
using System.Linq;

using WebContent.Admin;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #81: the structured allowlist grid's row model and its save-time build. The grid
    /// is the MC's only allowlist editor now, so the invariants the old free-text path pinned
    /// (grammar, canonicalization, duplicate collapse with flag-wins, the two stored shapes)
    /// must hold here, plus the #81-specific ones: inline row flags for refused entries, the
    /// full-host-wildcard detection that keys the warning banner, blank-row skipping, and the
    /// "legacy workspace opens and saves back to the structured shape without losing entries or
    /// flags" acceptance round-trip.
    /// </summary>
    public class ProxyAllowlistEditorTests
    {
        // ---- row validation: the shared grammar decides, never a UI-side copy ----------------

        [Theory]
        [InlineData("https", "example.com", "", "None")]
        [InlineData("https", "Example.COM", "", "None")]
        [InlineData("http", "*.foo.bar.com", "", "None")]
        [InlineData("http", "*", "", "None")]
        [InlineData("https", "10.0.0.5", "8443", "None")]
        [InlineData("https", "example.com", "443", "None")]   // scheme default = valid, canonicalises away
        [InlineData("https", "[::1]", "8443", "None")]        // exact IPv6 stays storable (floor only hits wildcards)
        [InlineData("https", "foo.*.bar.com", "", "HostOrPatternInvalid")]
        [InlineData("https", "*x.foo.com", "", "HostOrPatternInvalid")]
        [InlineData("https", "*.", "", "HostOrPatternInvalid")]
        [InlineData("https", "example.com/path", "", "HostOrPatternInvalid")]
        [InlineData("https", "exa mple.com", "", "HostOrPatternInvalid")]
        [InlineData("http", "example.com", "0", "None")]
        [InlineData("http", "example.com", "65536", "PortInvalid")]
        [InlineData("http", "example.com", "8o8o", "PortInvalid")]
        [InlineData("http", "example.com", "-1", "PortInvalid")]
        [InlineData("http", "", "8080", "HostRequired")]      // port typed, host blank: refusal, not blank
        public void RowValidationFollowsTheSharedGrammar(string scheme, string host, string port, string expectedName)
        {
            var expected = (ProxyTargetRowError)System.Enum.Parse(typeof(ProxyTargetRowError), expectedName);
            var row = new ProxyTargetEditorRow { Scheme = scheme, Host = host, PortText = port };
            Assert.Equal(expected, row.ErrorCode);
            Assert.Equal(expected != ProxyTargetRowError.None, row.HasError);
        }

        [Fact]
        public void BlankRowsAreNotEntries()
        {
            var row = new ProxyTargetEditorRow();
            Assert.True(row.IsEmpty);
            Assert.Equal(ProxyTargetRowError.None, row.ErrorCode);

            row.PortText = " ";
            row.Host = " ";
            Assert.True(row.IsEmpty);

            row.Host = "example.com";
            Assert.False(row.IsEmpty);
        }

        [Fact]
        public void ValidRowsCanonicalizeIntoTheSharedEntryForm()
        {
            var upper = new ProxyTargetEditorRow { Scheme = "HTTPS", Host = "Example.COM", PortText = "8443" };
            Assert.Equal("https://example.com:8443", upper.CanonicalEntry);

            var defaultPort = new ProxyTargetEditorRow { Scheme = "https", Host = "example.com", PortText = "443" };
            Assert.Equal("https://example.com", defaultPort.CanonicalEntry);

            var wildcard = new ProxyTargetEditorRow { Scheme = "http", Host = "*.FOO.bar.com" };
            Assert.Equal("http://*.foo.bar.com", wildcard.CanonicalEntry);
        }

        [Fact]
        public void OnlyTheLoneStarIsAFullHostWildcard()
        {
            Assert.True(new ProxyTargetEditorRow { Host = "*" }.IsFullHostWildcard);
            Assert.True(new ProxyTargetEditorRow { Host = " * " }.IsFullHostWildcard);   // trimmed like the canonicalizer trims
            Assert.False(new ProxyTargetEditorRow { Host = "*.foo.com" }.IsFullHostWildcard);
            Assert.False(new ProxyTargetEditorRow { Host = "" }.IsFullHostWildcard);
        }

        // ---- stored-shape <-> row translation --------------------------------------------------

        [Fact]
        public void ValidRowsWriteTheStructuredRecordShape()
        {
            var row = new ProxyTargetEditorRow { Scheme = "http", Host = "*.foo.com", PortText = "8080", AcceptUntrusted = true };
            Assert.True(row.TryToStoredRecord(out var record));
            Assert.Equal("http", record.Scheme);
            Assert.Equal("*.foo.com", record.Pattern);
            Assert.Equal(8080, record.Port);
            Assert.True(record.AcceptUntrusted);
            Assert.Null(record.Host);   // #73: writers emit the pattern member, never the legacy host
        }

        [Fact]
        public void LoadRendersBothStoredShapesForever()
        {
            var editor = new ProxyAllowlistEditor();
            var dropped = editor.Load(new List<ProxyTargetConfiguration>
            {
                // Legacy #52 record: exact host member, port, flag.
                new ProxyTargetConfiguration { Scheme = "https", Host = "10.0.0.5", Port = 8443, AcceptUntrusted = true },
                // Structured #73 record: pattern member (wildcard).
                new ProxyTargetConfiguration { Scheme = "http", Pattern = "*.cam.example", Port = 0 },
                // Corrupt: no host at all -> dropped, never rendered as a poisoned row.
                new ProxyTargetConfiguration { Scheme = "https", Port = 443 },
                // Corrupt: bogus scheme -> dropped.
                new ProxyTargetConfiguration { Scheme = "ftp", Host = "example.com" },
            });

            Assert.Equal(2, dropped);
            Assert.Equal(2, editor.Count);

            Assert.Equal("https", editor.Rows[0].Scheme);
            Assert.Equal("10.0.0.5", editor.Rows[0].Host);
            Assert.Equal("8443", editor.Rows[0].PortText);
            Assert.True(editor.Rows[0].AcceptUntrusted);

            Assert.Equal("http", editor.Rows[1].Scheme);
            Assert.Equal("*.cam.example", editor.Rows[1].Host);
            Assert.Equal(string.Empty, editor.Rows[1].PortText);   // 0 renders as the empty (scheme-default) cell
            Assert.False(editor.Rows[1].AcceptUntrusted);
            Assert.Equal(ProxyTargetRowError.None, editor.Rows[0].ErrorCode);
            Assert.Equal(ProxyTargetRowError.None, editor.Rows[1].ErrorCode);
        }

        [Fact]
        public void LegacyWorkspaceOpensAndSavesBackToTheStructuredShapeWithoutLosingEntriesOrFlags()
        {
            // The #81 acceptance round-trip, end to end through the editor.
            var legacy = new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "https", Host = "10.0.0.5", Port = 8443, AcceptUntrusted = true },
                new ProxyTargetConfiguration { Scheme = "https", Host = "httpforever.com", Port = 0, AcceptUntrusted = false },
                new ProxyTargetConfiguration { Scheme = "http", Host = "10.0.0.9", Port = 80, AcceptUntrusted = true },
            };

            var editor = new ProxyAllowlistEditor();
            Assert.Equal(0, editor.Load(legacy));
            Assert.True(editor.TryBuildStoredTargets(out var saved, out var rejected));
            Assert.Empty(rejected);

            Assert.Equal(3, saved.Count);
            Assert.All(saved, record => Assert.Null(record.Host));     // every re-saved record is the new shape
            Assert.Equal("10.0.0.5", saved[0].Pattern);
            Assert.Equal(8443, saved[0].Port);
            Assert.True(saved[0].AcceptUntrusted);
            Assert.Equal("httpforever.com", saved[1].Pattern);
            Assert.Equal(0, saved[1].Port);
            Assert.False(saved[1].AcceptUntrusted);
            Assert.Equal("10.0.0.9", saved[2].Pattern);
            Assert.Equal(0, saved[2].Port);                            // :80 canonicalises to the http default
            Assert.True(saved[2].AcceptUntrusted);
        }

        // ---- save-time build -------------------------------------------------------------------

        [Fact]
        public void InvalidRowsRefuseTheBuildByNameAndNeverVanish()
        {
            var editor = new ProxyAllowlistEditor();
            editor.AddBlankRow();
            editor.Rows[0].Host = "good.example.com";
            editor.AddBlankRow();
            editor.Rows[1].Host = "foo.*.bar.com";
            editor.AddBlankRow();
            editor.Rows[2].Scheme = "http";
            editor.Rows[2].Host = "ok.example.com";
            editor.Rows[2].PortText = "99999";

            Assert.False(editor.TryBuildStoredTargets(out var saved, out var rejected));
            Assert.Empty(saved);
            Assert.Equal(2, rejected.Count);
            Assert.Contains("foo.*.bar.com", rejected[0], StringComparison.Ordinal);
            Assert.Contains("http://ok.example.com:99999", rejected[1], StringComparison.Ordinal);
        }

        [Fact]
        public void BlankRowsAreSkippedAndOrderSurvives()
        {
            var editor = new ProxyAllowlistEditor();
            editor.AddBlankRow();
            editor.Rows[0].Host = "one.example.com";
            editor.AddBlankRow();                       // untouched blank
            editor.AddBlankRow();
            editor.Rows[2].Host = "two.example.com";

            Assert.True(editor.TryBuildStoredTargets(out var saved, out var rejected));
            Assert.Empty(rejected);
            Assert.Equal(2, saved.Count);
            Assert.Equal("one.example.com", saved[0].Pattern);
            Assert.Equal("two.example.com", saved[1].Pattern);
        }

        [Fact]
        public void DuplicateRowsCollapseAndAnyFlaggedDuplicateFlagsTheEntry()
        {
            var rows = new List<ProxyTargetEditorRow>
            {
                new ProxyTargetEditorRow { Scheme = "https", Host = "Example.com" },
                new ProxyTargetEditorRow { Scheme = "https", Host = "example.com", AcceptUntrusted = true },
                new ProxyTargetEditorRow { Scheme = "https", Host = "example.com", PortText = "443" },
            };

            Assert.True(ProxyAllowlistEditor.TryBuildStoredTargets(rows, out var saved, out var rejected));
            Assert.Empty(rejected);
            Assert.Single(saved);
            Assert.Equal("example.com", saved[0].Pattern);
            Assert.True(saved[0].AcceptUntrusted);
        }

        [Fact]
        public void HasFullHostWildcardTracksTheRows()
        {
            var editor = new ProxyAllowlistEditor();
            Assert.False(editor.HasFullHostWildcard);

            editor.AddBlankRow().Host = "example.com";
            Assert.False(editor.HasFullHostWildcard);

            var hatch = editor.AddBlankRow();
            hatch.Host = "*";
            editor.Rows[1].Scheme = "http";
            Assert.True(editor.HasFullHostWildcard);

            editor.Remove(hatch);
            Assert.False(editor.HasFullHostWildcard);
        }

        [Fact]
        public void RemovingAndClearingRowsLeavesASavableEmptyList()
        {
            var editor = new ProxyAllowlistEditor();
            editor.AddBlankRow().Host = "one.example.com";
            editor.AddBlankRow().Host = "two.example.com";
            editor.Clear();

            Assert.Equal(0, editor.Count);
            Assert.True(editor.TryBuildStoredTargets(out var saved, out var rejected));
            Assert.Empty(saved);
            // ("proxy enabled with zero targets" is the item manager's refusal about its toggle,
            // not the rows'; it stays where it was: MC-9 posture, pinned in the matrix.)
        }

        [Fact]
        public void DescribeKeepsTheProbeRecordShape()
        {
            var row = new ProxyTargetEditorRow { Scheme = "http", Host = "*.neverssl.com", AcceptUntrusted = true };
            Assert.Equal("http://*.neverssl.com accept-untrusted", row.Describe());

            var plain = new ProxyTargetEditorRow { Scheme = "https", Host = "10.0.0.5", PortText = "8443" };
            Assert.Equal("https://10.0.0.5:8443", plain.Describe());
        }

        [Fact]
        public void EditingTheFlagNeverChangesValidation()
        {
            var row = new ProxyTargetEditorRow { Scheme = "https", Host = "foo.*.bar.com" };
            var before = row.ErrorCode;
            row.AcceptUntrusted = true;
            row.AcceptUntrusted = false;
            Assert.Equal(before, row.ErrorCode);
        }

        /// <summary>The save's audit line is the storage-side view of the grid: it names every
        /// stored record in the admin's own entry shape (scheme, port, and the flag token), which
        /// is what the #81 live probe compares the grid against. A record it cannot parse is named
        /// as unparseable rather than left out, so the line never overstates the allowlist.</summary>
        [Fact]
        public void StoredTargetsAreDescribedInShapeForTheAuditLine()
        {
            var described = ProxyAllowlistEditor.DescribeStoredTargets(new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "http", Pattern = "httpforever.com", Port = 0 },
                new ProxyTargetConfiguration { Scheme = "https", Pattern = "*.cam.example", Port = 8443 },
                new ProxyTargetConfiguration { Scheme = "https", Host = "10.0.0.5", Port = 18125, AcceptUntrusted = true },
                new ProxyTargetConfiguration { Scheme = "ftp", Host = "example.com", Port = 21 },
            });

            Assert.Equal(
                "http://httpforever.com; https://*.cam.example:8443; https://10.0.0.5:18125 accept-untrusted; ftp://example.com:21 (unparseable)",
                described);

            Assert.Equal(string.Empty, ProxyAllowlistEditor.DescribeStoredTargets(null));
        }

        // ---- #84: the one-click promotion write path ----------------------------------------

        [Fact]
        public void PromotionAddsAnExactRowInTheGridsOwnShape()
        {
            // The Diagnostics tab's Add-to-allowlist button lands the blocked origin as an exact
            // scheme+host+port row - no wildcard, no accept-untrusted flag, no stored-record
            // shortcut around the grid.
            var editor = new ProxyAllowlistEditor();
            var changed = 0;
            editor.Rows.CollectionChanged += (s, e) => changed++;

            var outcome = editor.TryAddExactOrigin("http://172.16.128.210:18131", out var row);

            Assert.Equal(ProxyPromotionOutcome.Added, outcome);
            Assert.NotNull(row);
            Assert.Equal("http", row.Scheme);
            Assert.Equal("172.16.128.210", row.Host);
            Assert.Equal("18131", row.PortText);
            Assert.False(row.AcceptUntrusted);
            Assert.Equal("http://172.16.128.210:18131", row.CanonicalEntry);
            Assert.Equal(1, editor.Count);
            Assert.True(changed > 0, "the row-collection chain the page dirties the pane through never fired.");
        }

        [Theory]
        [InlineData("https://Corp.Example", "https://corp.example", "")]      // scheme default port canonicalises away
        [InlineData("http://cdn.example.com:8080", "http://cdn.example.com:8080", "8080")]
        [InlineData("https://[::1]:8443", "https://[::1]:8443", "8443")]
        public void PromotionRowsCarryTheCanonicalOriginAndItsParts(string origin, string canonical, string portText)
        {
            var editor = new ProxyAllowlistEditor();
            Assert.Equal(ProxyPromotionOutcome.Added, editor.TryAddExactOrigin(origin, out var row));
            Assert.Equal(canonical, row.CanonicalEntry);
            Assert.Equal(portText, row.PortText);
        }

        [Fact]
        public void PromotionDedupesAgainstAnEquivalentRowNotItsTyping()
        {
            // The grid already carries the entry (loaded from storage, differently typed): the
            // promotion must not double it - the canonical entry is the identity, exactly like
            // the save's duplicate collapse.
            var editor = new ProxyAllowlistEditor();
            editor.Load(new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "HTTPS", Host = "Example.COM", Port = 443 },
            });

            var outcome = editor.TryAddExactOrigin("https://example.com", out var row);

            Assert.Equal(ProxyPromotionOutcome.AlreadyPresent, outcome);
            Assert.Null(row);
            Assert.Equal(1, editor.Count);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not an origin")]
        [InlineData("ftp://files.example")]
        [InlineData("http://*.cdn.example")]     // the wildcard grammar is the admin's edit, not the button's
        [InlineData("http://*")]
        [InlineData("http://example.com/path")]
        public void PromotionRefusesAnythingOutsideTheExactEntryGrammarWithoutTouchingTheGrid(string origin)
        {
            var editor = new ProxyAllowlistEditor();
            editor.AddBlankRow();

            var outcome = editor.TryAddExactOrigin(origin, out var row);

            Assert.Equal(ProxyPromotionOutcome.Refused, outcome);
            Assert.Null(row);
            Assert.Equal(1, editor.Count);
        }

        [Fact]
        public void APromotedRowSavesAsTheStructuredRecordTheEditorItselfWouldWrite()
        {
            // #84 AC: "promotion produces a config write equivalent to the allowlist editor's
            // save". The promoted row is an ordinary grid row, so the ordinary save build lands
            // it as a structured pattern record - identical to what typing the row in by hand
            // would store.
            var editor = new ProxyAllowlistEditor();
            Assert.Equal(ProxyPromotionOutcome.Added, editor.TryAddExactOrigin("http://172.16.128.210:18131", out _));

            Assert.True(editor.TryBuildStoredTargets(out var targets, out var rejected));
            Assert.Empty(rejected);
            var record = Assert.Single(targets);
            Assert.Equal("http", record.Scheme);
            Assert.Equal("172.16.128.210", record.EffectivePattern);
            Assert.Equal(18131, record.Port);
            Assert.False(record.AcceptUntrusted);
        }

        [Fact]
        public void ValidCanonicalEntriesCoverUnsavedEditsAndSkipBrokenRows()
        {
            // The Diagnostics annotation feeds this through ProxyPolicy.IsRequestAllowed, so it
            // must list every row that PROMISES an entry - saved rows, hand-added rows, and
            // promoted rows alike - and never a row the save would refuse.
            var editor = new ProxyAllowlistEditor();
            editor.Load(new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "http", Pattern = "*.cam.example", Port = 0 },
            });
            editor.AddBlankRow();                                            // blank: promises nothing
            editor.AddBlankRow().Host = "bad.*.host";                        // invalid: refused at save
            editor.TryAddExactOrigin("https://promoted.example", out _);     // unsaved promotion

            var entries = editor.ValidCanonicalEntries();

            Assert.Contains("http://*.cam.example", entries);
            Assert.Contains("https://promoted.example", entries);
            Assert.Equal(2, entries.Count);
        }
    }
}
