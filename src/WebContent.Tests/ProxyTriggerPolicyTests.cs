using System;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #65: grammar tests for the proxy rule-trigger matcher. Two halves: the URL-glob
    /// normalizer (what an admin is allowed to write) and the row's verdict for one served fetch
    /// (what that writing means). The host-wildcard half deliberately mirrors the #73 allowlist
    /// shapes - the same host wildcard must mean the same thing an admin typed it in twice.
    /// </summary>
    public class ProxyTriggerPolicyTests
    {
        // ---- the fixed event type is one constant, not a per-row zoo ----

        [Fact]
        public void ThereIsExactlyOneEventTypeAndItIsFixedText()
        {
            Assert.Equal("WebContent proxy request", ProxyTriggerPolicy.EventType);
        }

        // ---- url glob normalization: accepted shapes ----

        [Theory]
        [InlineData("https://camera.local/snapshot")]           // exact host + literal path
        [InlineData("https://*.onvif.org/*")]                   // leftmost-label wildcard + path star
        [InlineData("https://*")]                               // bare host hatch, any path
        [InlineData("http://192.168.2.76:9225/onvif-3R/snapshot")] // exact host with port
        [InlineData("https://*.vendor.com:8443/api/login")]     // wildcard host with port
        [InlineData("*")]                                      // any scheme, any host, any path
        [InlineData("https://host/path/seg?x")]                 // '?' path glob
        public void TryNormalizeUrlPattern_AcceptsTheDocumentedGrammar(string pattern)
        {
            ProxyTriggerPolicy.TriggerRow row;
            string error;

            Assert.True(ProxyTriggerPolicy.TryNormalizeUrlPattern(pattern, out row, out error), error);
            Assert.NotNull(row);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("camera.local/path")]              // scheme missing
        [InlineData("ftp://camera.local/path")]        // scheme outside http/https/*
        [InlineData("https://user@host/path")]         // credentials
        [InlineData("https://host/path#frag")]         // fragment
        [InlineData("https://host:0/path")]            // port below range
        [InlineData("https://host:70000/path")]        // port above range
        [InlineData("https://*.*.example.com/x")]      // two wildcards is outside the host grammar
        [InlineData("https://exa*mple.com/x")]         // embedded wildcard label is outside the grammar
        [InlineData("https://.example.com/x")]         // empty leading label
        public void TryNormalizeUrlPattern_RefusesEverythingElse(string pattern)
        {
            ProxyTriggerPolicy.TriggerRow row;
            string error;

            Assert.False(ProxyTriggerPolicy.TryNormalizeUrlPattern(pattern, out row, out error));
            Assert.Null(row);
            Assert.False(string.IsNullOrEmpty(error), "a refused pattern must name the reason");
        }

        // ---- row verdicts: url, host, port, path ----

        [Theory]
        [InlineData("https://*.onvif.org/*", "https://camera.onvif.org/snapshot", true)]
        [InlineData("https://*.onvif.org/*", "https://deep.sub.onvif.org/snapshot", false)] // leftmost label only, same as the allowlist
        [InlineData("https://*.onvif.org/*", "https://onvif.org/snapshot", false)]          // apex never matches the label wildcard
        [InlineData("https://*/*", "https://anything.local/a/b", true)]
        [InlineData("https://camera.local/snapshot", "https://camera.local/snapshot?user=x", true)] // query is never part of the match
        [InlineData("https://camera.local/snapshot", "https://camera.local/snapshot2", false)]      // literal path is anchored
        [InlineData("https://camera.local/", "https://camera.local/other", false)]                 // lone "/" means exactly the root
        [InlineData("https://camera.local", "https://camera.local/any/deep/path", true)]           // absent path means any path
        [InlineData("https://*.vendor.com:8443/*", "https://cam.vendor.com:8443/login", true)]
        [InlineData("https://*.vendor.com/*", "https://cam.vendor.com:8443/login", false)]         // absent port = the scheme default only
        [InlineData("https://host.local/", "http://host.local/", false)]                           // scheme must match
        [InlineData("https://host.local/x", "http://host.local/x", false)]                         // https row never matches http traffic
        [InlineData("https://host.local/*", "https://host.local/x?query=1#frag", true)]            // query/fragment never participate
        public void RowMatches_UrlGrammar(string pattern, string url, bool expected)
        {
            ProxyTriggerPolicy.TriggerRow row;
            string error;
            Assert.True(ProxyTriggerPolicy.TryNormalizeUrlPattern(pattern, out row, out error), error);

            Uri target;
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out target));
            Assert.Equal(expected, row.MatchesUrl(target));
        }

        [Fact]
        public void RowMatches_SchemeWildcardIsTheLoneStarPattern()
        {
            ProxyTriggerPolicy.TriggerRow row;
            string error;

            // "*" alone is the any-scheme any-host any-path hatch.
            Assert.True(ProxyTriggerPolicy.TryNormalizeUrlPattern("*", out row, out error), error);
            Assert.True(row.MatchesUrl(new Uri("http://anywhere.example/x")));
            Assert.True(row.MatchesUrl(new Uri("https://anywhere.example/x")));
        }

        // ---- row verdicts: method filter ----

        [Theory]
        [InlineData(null, "GET", true)]
        [InlineData("", "POST", true)]
        [InlineData("GET", "get", true)]
        [InlineData("get", "GET", true)]
        [InlineData("GET", "POST", false)]
        public void RowMatches_MethodFilter(string rowMethod, string fetchMethod, bool expected)
        {
            var row = BuildRow("https://host/path", rowMethod, null);
            Assert.Equal(expected, row.Matches(new Uri("https://host/path"), fetchMethod, 200));
        }

        // ---- row verdicts: response-status filter ----

        [Theory]
        [InlineData(null, 200, true)]
        [InlineData(null, 404, true)]
        [InlineData("4xx", 404, true)]
        [InlineData("4XX", 404, true)]
        [InlineData("4xx", 200, false)]
        [InlineData("2xx", 204, true)]
        [InlineData("204", 204, true)]
        [InlineData("204", 200, false)]
        [InlineData("5xx", 503, true)]
        public void RowMatches_StatusFilter(string rowStatus, int status, bool expected)
        {
            var row = BuildRow("https://host/path", null, rowStatus);
            Assert.Equal(expected, row.Matches(new Uri("https://host/path"), "GET", status));
        }

        [Fact]
        public void Row_NeverMatchesWithoutAResponseStatus()
        {
            // The matcher runs after the response; a transport failure (status 0) produces no event.
            var row = BuildRow("https://host/path", null, null);
            Assert.False(row.Matches(new Uri("https://host/path"), "GET", 0));
            Assert.False(row.Matches(new Uri("https://host/path"), "GET", -1));
        }

        [Theory]
        [InlineData("6xx")]
        [InlineData("x22")]
        [InlineData("4x")]
        [InlineData("abc")]
        [InlineData("99")]
        [InlineData("600")]
        public void StatusFilter_RefusesEverythingOutsideClassAndExact(string status)
        {
            int exact;
            int cls;
            string error;

            Assert.False(ProxyTriggerPolicy.TryNormalizeStatusFilter(status, out exact, out cls, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        // ---- path glob: the anchored '*'/'?' engine ----

        [Theory]
        [InlineData("/a/*", "/a/b/c", true)]        // '*' spans '/' on purpose
        [InlineData("/a/*", "/a/", true)]
        [InlineData("/a/*", "/A/b", false)]         // path glob stays case-sensitive
        [InlineData("/*/login", "/v1/login", true)]
        [InlineData("/*/login", "/login", false)]   // '?' consumes exactly one character
        [InlineData("/*", "/deep/deep/deep", true)]
        [InlineData("/snapshot", "/snapshot", true)]
        [InlineData("/snapshot", "/snapshots", false)]
        [InlineData("*.jpg", "a/b/c.JPG", false)]
        [InlineData("*.jpg", "a/b/c.jpg", true)]
        public void GlobMatches_Coverage(string pattern, string value, bool expected)
        {
            Assert.Equal(expected, ProxyTriggerPolicy.GlobMatches(pattern, value));
        }

        [Fact]
        public void GlobMatches_Property_StarOnlyPatternMatchesEveryGeneratedPath()
        {
            // Seeded-Random property pin (same style as the ProxyPolicy grammar tests): every
            // generated path, however adversarial, matches the catch-all.
            var random = new Random(6501);
            for (var i = 0; i < 2000; i++)
            {
                Assert.True(ProxyTriggerPolicy.GlobMatches("/*", RandomPath(random)));
            }
        }

        [Fact]
        public void GlobMatches_Property_LiteralPatternMatchesItselfAndNearMissesDiffer()
        {
            var random = new Random(6502);
            for (var i = 0; i < 2000; i++)
            {
                // The pattern side uses the wildcard-free alphabet: a generated literal must
                // stay literal, or the '?' character in it would stop being one.
                var path = RandomPath(random, patternSafe: true);

                // No wildcards in the literal, so it matches itself...
                Assert.True(ProxyTriggerPolicy.GlobMatches(path, path));

                // ...and an adversarial single-character flip never matches a literal.
                if (path.Length > 1)
                {
                    var flip = path.Substring(0, path.Length / 2)
                               + (path[path.Length / 2] == 'z' ? 'q' : 'z')
                               + path.Substring(path.Length / 2 + 1);
                    Assert.False(ProxyTriggerPolicy.GlobMatches(path, flip));
                }
            }
        }

        [Fact]
        public void GlobMatches_Property_PrefixStarMatchesExactlyTheSuffixFreeSubset()
        {
            var random = new Random(6503);
            for (var i = 0; i < 2000; i++)
            {
                // Literal prefix (wildcard-free alphabet) plus a trailing star; the candidate
                // may contain anything, wildcards included.
                var prefix = RandomPath(random, patternSafe: true);
                var candidate = RandomPath(random);
                Assert.Equal(
                    ProxyTriggerPolicy.GlobMatches(prefix + "*", candidate),
                    candidate.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        // ---- describe / audit ----

        [Fact]
        public void Describe_RendersTheRowForTheAuditLine()
        {
            var row = BuildRow("https://*.onvif.org/*", "GET", "4xx");
            var described = ProxyTriggerPolicy.Describe(row);

            Assert.Contains("url=https://*.onvif.org/*", described);
            Assert.Contains("method=GET", described);
            Assert.Contains("status=4xx", described);
        }

        // ---- helpers ----

        private static ProxyTriggerPolicy.TriggerRow BuildRow(string url, string method, string status)
        {
            var trigger = new ProxyTriggerConfiguration { Url = url, Method = method, Status = status };
            ProxyTriggerPolicy.TriggerRow row;
            string error;
            Assert.True(ProxyTriggerPolicy.TryBuildRow(trigger, out row, out error), error);
            return row;
        }

        private static string RandomPath(Random random, bool patternSafe = false)
        {
            // The full alphabet carries the glob wildcard '?' on purpose - values are adversarial.
            // patternSafe drops it for text that plays the PATTERN role, where a literal match is
            // exactly what the property pins. ('*' stays out of both alphabets here.)
            const string alphabet = "abcxyz01-./?%";
            const string patternAlphabet = "abcxyz01-./%";

            var segments = 1 + random.Next(4);
            var path = string.Empty;
            for (var i = 0; i < segments; i++)
            {
                var length = 1 + random.Next(6);
                var segment = string.Empty;
                for (var c = 0; c < length; c++)
                {
                    var letters = patternSafe ? patternAlphabet : alphabet;
                    segment += letters[random.Next(letters.Length)];
                }

                path += "/" + segment;
            }

            return path;
        }
    }
}
