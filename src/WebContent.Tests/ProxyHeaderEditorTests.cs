using System;
using System.Collections.Generic;

using WebContent;
using WebContent.Admin;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #86: the Management Client's "Advanced: headers" row model and save build. The
    /// grammar is the shared ProxyHeaderPolicy, so these tests pin the UI-side posture: an invalid
    /// row (forbidden name, junk token, missing value, CR/LF) refuses the WHOLE save by name
    /// instead of being dropped; blanks are skipped; configuration order survives into the stored
    /// records; stored rows the grammar refuses are not rendered and get counted for the log line.
    /// </summary>
    public class ProxyHeaderEditorTests
    {
        [Fact]
        public void BlankRowIsValidAndSkipped()
        {
            var row = new ProxyHeaderEditorRow();
            Assert.True(row.IsEmpty);
            Assert.Equal(ProxyHeaderRuleError.None, row.ErrorCode);

            Assert.True(ProxyHeaderEditor.TryBuildStoredRules(new List<ProxyHeaderEditorRow> { row }, out var rules, out var rejected));
            Assert.Empty(rules);
            Assert.Empty(rejected);
        }

        [Theory]
        [InlineData("Host", "evil.example", false, (int)ProxyHeaderRuleError.Forbidden)]
        [InlineData("Content-Length", "0", false, (int)ProxyHeaderRuleError.Forbidden)]
        [InlineData("connection", "close", false, (int)ProxyHeaderRuleError.Forbidden)]
        [InlineData("Proxy-Cookie", "x", false, (int)ProxyHeaderRuleError.Forbidden)]
        [InlineData("Transfer-Encoding", null, true, (int)ProxyHeaderRuleError.Forbidden)] // a STRIP tick does not unlock forbidden names
        [InlineData("Bad Name", "x", false, (int)ProxyHeaderRuleError.NameInvalid)]
        [InlineData("X-NoValue", null, false, (int)ProxyHeaderRuleError.ValueEmpty)]
        [InlineData("X-CRLF", "a\r\nX-Evil: yes", false, (int)ProxyHeaderRuleError.ValueLineBreak)]
        [InlineData("", "orphan value", false, (int)ProxyHeaderRuleError.NameEmpty)]
        public void InvalidRowsCarryTheSharedErrorCode(string name, string value, bool remove, int expectedCode)
        {
            var expected = (ProxyHeaderRuleError)expectedCode;
            var row = new ProxyHeaderEditorRow { Name = name, Value = value, Remove = remove };
            Assert.Equal(expected, row.ErrorCode);
            Assert.True(row.HasError);
        }

        [Fact]
        public void AnyInvalidRowRefusesTheWholeSaveAndIsNamedInOrder()
        {
            var rows = new List<ProxyHeaderEditorRow>
            {
                new ProxyHeaderEditorRow { Name = "X-Good", Value = "1" },
                new ProxyHeaderEditorRow { Name = "Host", Value = "evil.example" },
                new ProxyHeaderEditorRow { Name = "X-CRLF", Value = "a\r\nb" },
            };

            Assert.False(ProxyHeaderEditor.TryBuildStoredRules(rows, out var rules, out var rejected));
            Assert.Empty(rules); // nothing partial is ever stored - the cap-save posture
            Assert.Equal(2, rejected.Count);
            Assert.Contains("Host=evil.example", rejected[0], StringComparison.Ordinal);
            Assert.Contains("X-CRLF=", rejected[1], StringComparison.Ordinal);
        }

        [Fact]
        public void ValidRowsStoreInOrderWithStripRowsCarryingNoValue()
        {
            var rows = new List<ProxyHeaderEditorRow>
            {
                new ProxyHeaderEditorRow { Name = "X-First", Value = "1" },
                new ProxyHeaderEditorRow { Name = "X-First", Value = "2" },
                new ProxyHeaderEditorRow { Name = "X-Internal", Remove = true },
                new ProxyHeaderEditorRow(),
            };

            Assert.True(ProxyHeaderEditor.TryBuildStoredRules(rows, out var rules, out var rejected));
            Assert.Empty(rejected);
            Assert.Equal(3, rules.Count);
            Assert.Equal("2", rules[1].Value); // order preserved: last rule wins at read time
            Assert.True(rules[2].Remove);
            Assert.Null(rules[2].Value);
        }

        [Fact]
        public void StripRowWithoutValueIsValid()
        {
            var row = new ProxyHeaderEditorRow { Name = "Server", Remove = true };
            Assert.Equal(ProxyHeaderRuleError.None, row.ErrorCode);
            Assert.False(row.IsEmpty); // a lone strip tick IS a rule
        }

        [Fact]
        public void StoredRowsOutsideTheGrammarAreNotRenderedAndAreCounted()
        {
            var editor = new ProxyHeaderEditor();
            var dropped = editor.Load(new List<ProxyHeaderRuleConfiguration>
            {
                new ProxyHeaderRuleConfiguration { Name = "X-Ok", Value = "v" },
                new ProxyHeaderRuleConfiguration { Name = "Host", Value = "hand-edited" },
                new ProxyHeaderRuleConfiguration { Name = "Bad Name", Value = "v" },
                null,
            });

            Assert.Equal(3, dropped);
            var row = Assert.Single(editor.Rows);
            Assert.Equal("X-Ok", row.Name);
            Assert.Equal(ProxyHeaderRuleError.None, row.ErrorCode);
        }

        [Fact]
        public void LoadedValidRowsRoundTripBackToTheSameRecords()
        {
            var editor = new ProxyHeaderEditor();
            Assert.Equal(0, editor.Load(new List<ProxyHeaderRuleConfiguration>
            {
                new ProxyHeaderRuleConfiguration { Name = "X-Edge", Value = "vms" },
                new ProxyHeaderRuleConfiguration { Name = "Set-Cookie", Remove = true },
            }));

            Assert.True(editor.TryBuildStoredRules(out var rules, out var rejected));
            Assert.Empty(rejected);
            Assert.Equal(2, rules.Count);
            Assert.Equal("X-Edge", rules[0].Name);
            Assert.Equal("vms", rules[0].Value);
            Assert.True(rules[1].Remove);
        }

        [Fact]
        public void DescribeStoredRulesRendersTheAuditLineShape()
        {
            var text = ProxyHeaderEditor.DescribeStoredRules(new List<ProxyHeaderRuleConfiguration>
            {
                new ProxyHeaderRuleConfiguration { Name = "X-Edge", Value = "vms" },
                new ProxyHeaderRuleConfiguration { Name = "Server", Remove = true },
            });

            Assert.Equal("X-Edge=vms; -Server", text);
        }
    }
}
