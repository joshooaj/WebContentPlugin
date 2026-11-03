using System;
using System.Collections.Generic;

using WebContent;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #86: the shared header-policy grammar and the guardrail set. Acceptance lives here:
    /// the forbidden set covers the RFC hop-by-hop headers plus Host/Content-Length/Transfer-
    /// Encoding/TE/Upgrade and the whole Proxy-* prefix, no configured rule can name one (at save
    /// or at snapshot), CR/LF in a value is refused (response-header injection), and the runtime
    /// apply model is what the fetch path runs: removals and add/overrides in configuration order
    /// (last rule for a name wins), forbidden rules never applied, name matching case-insensitive.
    /// </summary>
    public class ProxyHeaderPolicyTests
    {
        [Theory]
        [InlineData("Connection")]
        [InlineData("Keep-Alive")]
        [InlineData("Proxy-Authenticate")]
        [InlineData("Proxy-Authorization")]
        [InlineData("Proxy-Connection")]
        [InlineData("Proxy-Anything-Custom")]
        [InlineData("TE")]
        [InlineData("Trailer")]
        [InlineData("Transfer-Encoding")]
        [InlineData("Upgrade")]
        [InlineData("Host")]
        [InlineData("Content-Length")]
        [InlineData("connection")] // case-insensitive, like every HTTP name comparison
        [InlineData("PROXY-COOKIE")]
        public void ForbiddenHeadersCanBeNeverConfigured(string name)
        {
            Assert.True(ProxyHeaderPolicy.IsForbiddenHeader(name));
            Assert.Equal(ProxyHeaderRuleError.Forbidden, ProxyHeaderPolicy.ValidateRule(name, "anything", false));
            Assert.Equal(ProxyHeaderRuleError.Forbidden, ProxyHeaderPolicy.ValidateRule(name, null, true));
        }

        [Theory]
        [InlineData("X-Custom")]
        [InlineData("X-Forwarded-For")]
        [InlineData("Via")]
        [InlineData("User-Agent")]
        [InlineData("Cookie")]
        [InlineData("Accept")]
        [InlineData("M-9")] // plugin custom verbs keep their headers (#55 posture)
        public void OrdinaryHeadersAreConfigurable(string name)
        {
            Assert.False(ProxyHeaderPolicy.IsForbiddenHeader(name));
            Assert.Equal(ProxyHeaderRuleError.None, ProxyHeaderPolicy.ValidateRule(name, "v", false));
            Assert.Equal(ProxyHeaderRuleError.None, ProxyHeaderPolicy.ValidateRule(name, null, true));
        }

        [Theory]
        [InlineData("Bad Name")]      // space is not a token char
        [InlineData("Bad\u0001Name")] // control character
        [InlineData("Bad(Name)")]     // separators are not tchars
        [InlineData("Bad:Name")]
        [InlineData("@Bad")]
        public void InvalidFieldNamesAreRefused(string name)
        {
            Assert.Equal(ProxyHeaderRuleError.NameInvalid, ProxyHeaderPolicy.ValidateRule(name, "v", false));
        }

        [Fact]
        public void BlankNamesAndValuelessAddRowsAreRefused()
        {
            Assert.Equal(ProxyHeaderRuleError.NameEmpty, ProxyHeaderPolicy.ValidateRule(null, "v", false));
            Assert.Equal(ProxyHeaderRuleError.NameEmpty, ProxyHeaderPolicy.ValidateRule("  ", "v", false));
            Assert.Equal(ProxyHeaderRuleError.ValueEmpty, ProxyHeaderPolicy.ValidateRule("X-Ok", null, false));
            Assert.Equal(ProxyHeaderRuleError.ValueEmpty, ProxyHeaderPolicy.ValidateRule("X-Ok", "   ", false));
        }

        [Theory]
        [InlineData("line1\r\nX-Evil: yes")]
        [InlineData("line1\nX-Evil: yes")]
        [InlineData("line1\rX-Evil: yes")]
        public void LineBreaksInValuesAreRefusedOnBothLegs(string value)
        {
            // CRLF in a configured value is a response-header injection vector; the name grammar
            // already blocks CR/LF in names, this closes the value side.
            Assert.Equal(ProxyHeaderRuleError.ValueLineBreak, ProxyHeaderPolicy.ValidateRule("X-Ok", value, false));
        }

        [Fact]
        public void DescribeRuleErrorNamesTheHeaderForAuditLines()
        {
            Assert.Contains("Host", ProxyHeaderPolicy.DescribeRuleError(ProxyHeaderRuleError.Forbidden, "Host"), StringComparison.Ordinal);
            Assert.Contains("reserved", ProxyHeaderPolicy.DescribeRuleError(ProxyHeaderRuleError.Forbidden, "Host"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Bad Name", ProxyHeaderPolicy.DescribeRuleError(ProxyHeaderRuleError.NameInvalid, "Bad Name"), StringComparison.Ordinal);
        }

        private static List<ProxyFetchHeader> Headers(params ProxyFetchHeader[] headers)
        {
            return new List<ProxyFetchHeader>(headers);
        }

        private static ProxyFetchHeader Header(string name, string value)
        {
            return new ProxyFetchHeader { Name = name, Value = value };
        }

        [Fact]
        public void AddRuleAppendsOneEntryAndOverrideReplacesEverySameNameEntry()
        {
            var headers = Headers(
                Header("accept", "text/html"),
                Header("Accept", "*/*"),
                Header("Cookie", "a=1"));
            var rules = new List<ProxyHeaderPolicy.HeaderRule>
            {
                new ProxyHeaderPolicy.HeaderRule { Name = "Accept", Value = "application/json", Remove = false },
                new ProxyHeaderPolicy.HeaderRule { Name = "X-Trace", Value = "es", Remove = false },
            };

            ProxyHeaderPolicy.ApplyRules(headers, rules);

            var accept = headers.FindAll(h => string.Equals(h.Name, "Accept", StringComparison.OrdinalIgnoreCase));
            Assert.Single(accept);
            Assert.Equal("application/json", accept[0].Value);
            Assert.Equal("es", headers.Find(h => h.Name == "X-Trace").Value);
            Assert.Single(headers.FindAll(h => h.Name == "Cookie"));
        }

        [Fact]
        public void RemoveRuleStripsEveryEntryOfTheNameAndAbsenceIsANoOp()
        {
            var headers = Headers(
                Header("Set-Cookie", "a=1"),
                Header("set-cookie", "b=2"),
                Header("X-Keep", "yes"));
            var rules = new List<ProxyHeaderPolicy.HeaderRule>
            {
                new ProxyHeaderPolicy.HeaderRule { Name = "Set-Cookie", Remove = true },
                new ProxyHeaderPolicy.HeaderRule { Name = "X-Absent", Remove = true },
            };

            var applied = ProxyHeaderPolicy.ApplyRules(headers, rules);

            Assert.Empty(headers.FindAll(h => string.Equals(h.Name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)));
            Assert.Single(headers.FindAll(h => h.Name == "X-Keep"));
            Assert.Single(applied); // the absent-name removal is not recorded as an application
        }

        [Fact]
        public void LastRuleForANameWinsInConfigurationOrder()
        {
            var headers = new List<ProxyFetchHeader>();
            var rules = new List<ProxyHeaderPolicy.HeaderRule>
            {
                new ProxyHeaderPolicy.HeaderRule { Name = "X-Step", Value = "one", Remove = false },
                new ProxyHeaderPolicy.HeaderRule { Name = "X-Step", Value = "two", Remove = false },
            };

            ProxyHeaderPolicy.ApplyRules(headers, rules);

            Assert.Single(headers);
            Assert.Equal("two", headers[0].Value);
        }

        [Fact]
        public void ForbiddenRulesAreNeverAppliedEvenWhenAStoredBlobCarriesThem()
        {
            // The snapshot store refuses such rows; ApplyRules re-checks, so even a hand-fed rule
            // list cannot put Host: or Content-Length: on either leg.
            var headers = Headers(Header("Host", "good.example"));
            var rules = new List<ProxyHeaderPolicy.HeaderRule>
            {
                new ProxyHeaderPolicy.HeaderRule { Name = "Host", Value = "evil.example", Remove = false },
                new ProxyHeaderPolicy.HeaderRule { Name = "Content-Length", Value = "0", Remove = false },
                new ProxyHeaderPolicy.HeaderRule { Name = "Transfer-Encoding", Remove = true },
                new ProxyHeaderPolicy.HeaderRule { Name = "Proxy-Cookie", Remove = true },
            };

            var applied = ProxyHeaderPolicy.ApplyRules(headers, rules);

            Assert.Empty(applied);
            Assert.Single(headers);
            Assert.Equal("good.example", headers[0].Value);
        }

        [Fact]
        public void CanonicalForbiddenSetIsWhatEveryLegDerivesFrom()
        {
            // Behavior-preservation pin for the #86 rewire: the three block sets (relay,
            // outbound-request, transport) now derive from this set, so the exact hop-by-hop
            // names they have always stripped stay listed here.
            foreach (var name in new[]
            {
                "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
                "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
                "Host", "Content-Length",
            })
            {
                Assert.True(ProxyHeaderPolicy.ForbiddenHeaderNames.Contains(name), name);
            }
        }
    }
}
