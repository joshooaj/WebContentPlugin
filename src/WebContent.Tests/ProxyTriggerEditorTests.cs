using System;
using System.Collections.Generic;
using System.Linq;

using WebContent;
using WebContent.Admin;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #78: the Management Client's "Rule triggers" row model and save build. The grammar
    /// is the shared ProxyTriggerPolicy (#65), so these tests pin the UI-side posture: an invalid
    /// row (bad URL glob, junk method filter, bad status filter) refuses the WHOLE save by name
    /// instead of being silently dropped; blanks are skipped; configuration order survives into
    /// the stored records (the matcher raises at most the FIRST matching row's event); stored
    /// rows the grammar refuses — or that name a verb the dropdown cannot render — are not shown
    /// and get counted for the log line.
    /// </summary>
    public class ProxyTriggerEditorTests
    {
        [Fact]
        public void BlankRowIsValidAndSkipped()
        {
            var row = new ProxyTriggerEditorRow();
            Assert.True(row.IsEmpty); // no url, no status, any-method "*"
            Assert.Equal(ProxyTriggerRowError.None, row.ErrorCode);

            Assert.True(ProxyTriggerEditor.TryBuildStoredTriggers(new List<ProxyTriggerEditorRow> { row }, out var triggers, out var rejected));
            Assert.Empty(triggers);
            Assert.Empty(rejected);
        }

        [Theory]
        [InlineData("foo.bar.com", "*", "", (int)ProxyTriggerRowError.UrlInvalid)]            // must be absolute (or the lone *)
        [InlineData("http://foo.*.bar.com/", "*", "", (int)ProxyTriggerRowError.UrlInvalid)]  // mid-label wildcard
        [InlineData("http://user@host/", "*", "", (int)ProxyTriggerRowError.UrlInvalid)]      // credentials
        [InlineData("ftp://host/", "*", "", (int)ProxyTriggerRowError.UrlInvalid)]            // non-http scheme
        [InlineData("https://host:99999/", "*", "", (int)ProxyTriggerRowError.UrlInvalid)]    // port out of range
        [InlineData("http://host/path#frag", "*", "", (int)ProxyTriggerRowError.UrlInvalid)]  // fragment
        [InlineData("", "*", "404", (int)ProxyTriggerRowError.UrlRequired)]                   // status alone is not a trigger
        [InlineData("https://host/", "GE T", "", (int)ProxyTriggerRowError.MethodInvalid)]    // verb token with a space
        [InlineData("https://host/", "*", "4x", (int)ProxyTriggerRowError.StatusInvalid)]     // half a class
        [InlineData("https://host/", "*", "600", (int)ProxyTriggerRowError.StatusInvalid)]    // code out of 100-599
        [InlineData("https://host/", "*", "abc", (int)ProxyTriggerRowError.StatusInvalid)]    // junk status
        public void InvalidRowsCarryThePartLevelErrorCode(string url, string method, string status, int expectedCode)
        {
            var expected = (ProxyTriggerRowError)expectedCode;
            var row = new ProxyTriggerEditorRow { Url = url, Method = method, Status = status };
            Assert.Equal(expected, row.ErrorCode);
            Assert.True(row.HasError);
        }

        [Theory]
        [InlineData("https://*.neverssl.com/*", "GET", "2xx")]
        [InlineData("http://example.com:8080/snap*", "*", "")]
        [InlineData("*", "", "404")]
        [InlineData("https://host/", "PATCH", "4xx")]
        public void ValidRowsCarryNoErrorCode(string url, string method, string status)
        {
            var row = new ProxyTriggerEditorRow { Url = url, Method = method, Status = status };
            Assert.Equal(ProxyTriggerRowError.None, row.ErrorCode);
            Assert.False(row.IsEmpty);
        }

        [Fact]
        public void AnyInvalidRowRefusesTheWholeSaveAndIsNamedInOrder()
        {
            var rows = new List<ProxyTriggerEditorRow>
            {
                new ProxyTriggerEditorRow { Url = "https://good.example/*", Method = "GET", Status = "2xx" },
                new ProxyTriggerEditorRow { Url = "http://foo.*.bar.com/", Method = "*", Status = "" },
                new ProxyTriggerEditorRow { Url = "https://host/", Method = "*", Status = "600" },
            };

            Assert.False(ProxyTriggerEditor.TryBuildStoredTriggers(rows, out var triggers, out var rejected));
            Assert.Empty(triggers); // nothing partial is ever stored - the header-save posture
            Assert.Equal(2, rejected.Count);
            Assert.Contains("url=http://foo.*.bar.com/ method=any status=any", rejected[0], StringComparison.Ordinal);
            Assert.Contains("url=https://host/ method=any status=600", rejected[1], StringComparison.Ordinal);
        }

        [Fact]
        public void ValidRowsStoreInOrderWithUntouchedFiltersBecomingNullMembers()
        {
            var rows = new List<ProxyTriggerEditorRow>
            {
                new ProxyTriggerEditorRow { Url = "https://first.example/*", Method = "GET", Status = "4xx" },
                new ProxyTriggerEditorRow { Url = "https://second.example/", Method = "*", Status = "" }, // untouched combo/status
                new ProxyTriggerEditorRow { Url = "http://*/", Method = "DELETE", Status = "500" },
                new ProxyTriggerEditorRow(), // blank - skipped
            };

            Assert.True(ProxyTriggerEditor.TryBuildStoredTriggers(rows, out var triggers, out var rejected));
            Assert.Empty(rejected);
            Assert.Equal(3, triggers.Count);

            // Order preserved: the matcher evaluates top-down and raises at most the FIRST match.
            Assert.Equal("https://first.example/*", triggers[0].Url);
            Assert.Equal("GET", triggers[0].Method);
            Assert.Equal("4xx", triggers[0].Status);

            // Untouched any-filters store as null members (the compact wire shape).
            Assert.Null(triggers[1].Method);
            Assert.Null(triggers[1].Status);
            Assert.Equal("DELETE", triggers[2].Method);
            Assert.Equal("500", triggers[2].Status);
        }

        [Fact]
        public void StoredRowsOutsideTheGrammarOrUnrenderableAreNotShownAndAreCounted()
        {
            var editor = new ProxyTriggerEditor();
            var dropped = editor.Load(new List<ProxyTriggerConfiguration>
            {
                new ProxyTriggerConfiguration { Url = "https://ok.example/*", Method = "GET", Status = "2xx" },
                new ProxyTriggerConfiguration { Url = "hand-edited-garbage", Method = "*", Status = null },  // grammar refuses
                new ProxyTriggerConfiguration { Url = "https://ok.example/", Method = "TRACE", Status = null }, // relayed-set dropdown cannot render
                new ProxyTriggerConfiguration { Url = "https://ok.example/", Method = null, Status = "99" },   // status outside 100-599
                null,
            });

            Assert.Equal(4, dropped);
            var row = Assert.Single(editor.Rows);
            Assert.Equal("https://ok.example/*", row.Url);
            Assert.Equal("GET", row.Method);
            Assert.Equal("2xx", row.Status);
            Assert.Equal(ProxyTriggerRowError.None, row.ErrorCode);
        }

        [Fact]
        public void LoadedValidRowsRoundTripBackToTheSameRecords()
        {
            var editor = new ProxyTriggerEditor();
            Assert.Equal(0, editor.Load(new List<ProxyTriggerConfiguration>
            {
                new ProxyTriggerConfiguration { Url = "https://*.onvif.org/*", Method = "GET", Status = "4xx" },
                new ProxyTriggerConfiguration { Url = "https://10.0.0.5:9225/snapshot" }, // no members = any/any
            }));

            // A stored row with no method member renders as the dropdown's any-method "*".
            Assert.Equal("*", editor.Rows[1].Method);
            Assert.Equal(ProxyTriggerRowError.None, editor.Rows[1].ErrorCode);

            Assert.True(editor.TryBuildStoredTriggers(out var triggers, out var rejected));
            Assert.Empty(rejected);
            Assert.Equal(2, triggers.Count);
            Assert.Equal("https://*.onvif.org/*", triggers[0].Url);
            Assert.Equal("GET", triggers[0].Method);
            Assert.Equal("4xx", triggers[0].Status);
            Assert.Equal("https://10.0.0.5:9225/snapshot", triggers[1].Url);
            Assert.Null(triggers[1].Method);
            Assert.Null(triggers[1].Status);
        }

        [Fact]
        public void AbsentStoredSectionIsTheZeroTriggersReading()
        {
            var editor = new ProxyTriggerEditor();
            Assert.Equal(0, editor.Load(null));
            Assert.Equal(0, editor.Count);
            Assert.True(editor.TryBuildStoredTriggers(out var triggers, out var rejected));
            Assert.Empty(triggers);
        }

        [Fact]
        public void DescribeStoredTriggersRendersTheAuditLineShape()
        {
            var text = ProxyTriggerEditor.DescribeStoredTriggers(new List<ProxyTriggerConfiguration>
            {
                new ProxyTriggerConfiguration { Url = "https://*.neverssl.com/*", Method = "GET", Status = "2xx" },
                new ProxyTriggerConfiguration { Url = "http://example/x" },
            });

            Assert.Equal("url=https://*.neverssl.com/* method=GET status=2xx; url=http://example/x method=any status=any", text);
        }

        [Fact]
        public void RowsTheGridAcceptsAreRowsTheEventServerBuildsAndViceVersa()
        {
            // The agreement that makes a highlighted row and an ES audit line the SAME statement:
            // for every shape, grid validation and the matcher's own row build agree.
            var shapes = new (string Url, string Method, string Status)[]
            {
                ("https://*.neverssl.com/*", "GET", "2xx"),
                ("http://*", "", ""),
                ("*", "*", "*"),
                ("https://host:8443/a?b", "PATCH", "418"),
                ("foo.bar.com", "GET", "200"),
                ("http://*.a..b/", "*", ""),
                ("https://host/", "GET", "04x"),
                ("https://host/", "G T", ""),
            };

            foreach (var (url, method, status) in shapes)
            {
                var row = new ProxyTriggerEditorRow { Url = url, Method = method, Status = status };
                var gridAccepts = row.ErrorCode == ProxyTriggerRowError.None && !row.IsEmpty;
                var serverBuilds = ProxyTriggerPolicy.TryBuildRow(
                    new ProxyTriggerConfiguration { Url = url, Method = method, Status = status }, out _, out _);
                Assert.Equal(serverBuilds, gridAccepts);
            }
        }

        [Fact]
        public void EventColumnCarriesTheOneFixedEventType()
        {
            var row = new ProxyTriggerEditorRow();
            Assert.Equal("WebContent proxy request", row.Event);
            Assert.Equal(ProxyTriggerPolicy.EventType, row.Event);
        }

        [Fact]
        public void MethodDropdownOffersTheAnyTokenPlusExactlyTheRelayedVerbs()
        {
            // The dropdown set is pinned: any-method "*" first, then exactly the verbs the proxy
            // fetch path relays. A stored row naming anything else is dropped (and logged) rather
            // than rendered as a row that could never match.
            Assert.Equal(
                new[] { "*", "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "PATCH" },
                ProxyTriggerEditor.ProxyTriggerMethods);
        }

        [Fact]
        public void BlankRowWithOnlyAStatusOrVerbIsNotAnEntryButIsNotValidEither()
        {
            var verbOnly = new ProxyTriggerEditorRow { Url = "", Method = "GET", Status = "" };
            Assert.False(verbOnly.IsEmpty);
            Assert.Equal(ProxyTriggerRowError.UrlRequired, verbOnly.ErrorCode);

            var statusOnly = new ProxyTriggerEditorRow { Url = "", Method = "*", Status = "404" };
            Assert.False(statusOnly.IsEmpty);
            Assert.Equal(ProxyTriggerRowError.UrlRequired, statusOnly.ErrorCode);
        }
    }
}
