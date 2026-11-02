using System;
using System.Collections.Generic;
using Xunit;
using WebContent;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #52: an admin allowlist entry is an origin (scheme, host, port). ProxyPolicy
    /// canonicalizes every entry - whatever the admin types - to a comparable origin string, and
    /// refuses entries that are not a valid http(s) origin. Canonical form is what the Smart
    /// Client view item properties carry and what request matching later compares against.
    /// </summary>
    public class ProxyPolicyTests
    {
        [Theory]
        [InlineData("https://10.0.0.5:8443", "https://10.0.0.5:8443")]
        [InlineData("HTTPS://10.0.0.5:8443/live/index.html", "https://10.0.0.5:8443")]
        [InlineData("https://cam2.local:443/", "https://cam2.local")]
        [InlineData("http://cam2.local:80/", "http://cam2.local")]
        [InlineData("http://cam2.local", "http://cam2.local")]
        [InlineData("Cam2.Local:8443", "https://cam2.local:8443")]
        [InlineData("https://[::1]:8443/status.cgi", "https://[::1]:8443")]
        public void AdminEntryIsCanonicalizedToItsOrigin(string entry, string expected)
        {
            Assert.True(ProxyPolicy.TryNormalizeTarget(entry, out var normalized));
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("ftp://files.example/")]
        [InlineData("javascript:alert(1)")]
        [InlineData("https://host:99999")]
        [InlineData("https://:8443")]
        public void InvalidProxyTargetEntryIsRejected(string entry)
        {
            Assert.False(ProxyPolicy.TryNormalizeTarget(entry, out _));
        }

        [Fact]
        public void RequestIsAllowedOnlyOnTheExactAllowlistedOrigin()
        {
            var targets = new[] { "https://cam2.local:8443" };

            Assert.True(ProxyPolicy.IsRequestAllowed(targets, new Uri("https://cam2.local:8443/somewhere/page.html")));
            Assert.False(ProxyPolicy.IsRequestAllowed(targets, new Uri("https://cam2.local:8444/")));
            Assert.False(ProxyPolicy.IsRequestAllowed(targets, new Uri("http://cam2.local:8443/")));
            Assert.False(ProxyPolicy.IsRequestAllowed(targets, new Uri("https://cam2.local/")));
        }

        [Fact]
        public void DuplicateAndOverlappingEntriesCollapseOntoOneCanonicalOrigin()
        {
            var canonicalized = new List<string>();
            foreach (var entry in new[] { "https://Cam2.local:8443", "https://cam2.local:8443/live/", "Cam2.Local:8443" })
            {
                Assert.True(ProxyPolicy.TryNormalizeTarget(entry, out var normalized));
                canonicalized.Add(normalized);
            }

            Assert.All(canonicalized, value => Assert.Equal("https://cam2.local:8443", value));
            Assert.True(ProxyPolicy.IsRequestAllowed(canonicalized, new Uri("https://cam2.local:8443/any/thing")));
        }

        [Fact]
        public void EmptyAllowlistRefusesEveryRequest()
        {
            Assert.False(ProxyPolicy.IsRequestAllowed(new string[0], new Uri("https://cam2.local:8443/")));
            Assert.False(ProxyPolicy.IsRequestAllowed(null, new Uri("https://cam2.local:8443/")));
        }

        // #56: an admin line is one origin plus an optional accept-untrusted-certificate flag
        // token. Everything about the entry - origin and flag - travels with the entry.

        [Theory]
        [InlineData("https://10.0.0.5:8443", "https://10.0.0.5:8443", false)]
        [InlineData("https://10.0.0.5:8443 accept-untrusted", "https://10.0.0.5:8443", true)]
        [InlineData("https://cam2.local/ ACCEPT-UNTRUSTED", "https://cam2.local", true)]
        [InlineData("accept-untrusted Cam2.Local:8443", "https://cam2.local:8443", true)]
        [InlineData("  cam2.local:8443   accept-untrusted  ", "https://cam2.local:8443", true)]
        public void TargetLineCarriesOneOriginAndAnOptionalUntrustedCertificateFlag(
            string line, string expectedOrigin, bool expectedFlag)
        {
            Assert.True(ProxyPolicy.TryParseTargetEntry(line, out var origin, out var acceptUntrusted));
            Assert.Equal(expectedOrigin, origin);
            Assert.Equal(expectedFlag, acceptUntrusted);
        }

        [Theory]
        [InlineData("https://cam2.local:8443 allow-all")]
        [InlineData("https://cam2.local:8443 accept_untrusted")]
        [InlineData("https://a.test https://b.test")]
        [InlineData("accept-untrusted")]
        [InlineData("ftp://cam2 accept-untrusted")]
        [InlineData("")]
        [InlineData(null)]
        public void MalformedTargetLineIsRejected(string line)
        {
            Assert.False(ProxyPolicy.TryParseTargetEntry(line, out _, out _));
        }

        [Fact]
        public void StoredEntryRendersBackAsTheAdminLineThatCreatedIt()
        {
            Assert.Equal("https://cam2.local:8443", ProxyPolicy.DescribeTargetEntry("https://cam2.local:8443", false));
            Assert.Equal("https://cam2.local:8443 accept-untrusted", ProxyPolicy.DescribeTargetEntry("https://cam2.local:8443", true));
        }

        // #73: wildcard grammar. Exactly the leftmost-label wildcard ("*.tail") and the lone
        // full-host hatch ("*"); anything else with a '*' is a malformed entry.

        [Theory]
        [InlineData("https://*.foo.bar.com", "https://*.foo.bar.com")]
        [InlineData("HTTPS://*.FOO.BAR.COM/", "https://*.foo.bar.com")]
        [InlineData("https://*.foo.bar.com:8443", "https://*.foo.bar.com:8443")]
        [InlineData("http://*.cams.local:80", "http://*.cams.local")]
        [InlineData("https://*", "https://*")]
        [InlineData("https://*:9443", "https://*:9443")]
        [InlineData("*.foo.bar.com", "https://*.foo.bar.com")]
        [InlineData("*.foo.bar.com:8443", "https://*.foo.bar.com:8443")]
        [InlineData("*", "https://*")]
        public void WildcardEntryCanonicalizesToItsPatternOrigin(string entry, string expected)
        {
            Assert.True(ProxyPolicy.TryNormalizeTarget(entry, out var normalized));
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData("https://foo.*.bar.com")]
        [InlineData("https://*foo.bar.com")]
        [InlineData("https://foo.bar*.com")]
        [InlineData("https://**.foo.bar.com")]
        [InlineData("https://*.*.com")]
        [InlineData("https://*.")]
        [InlineData("https://.*.com")]
        [InlineData("https://*.foo..bar.com")]
        [InlineData("https://*.foo-.bar.com")]
        [InlineData("https://*.foo.bar.com:99999")]
        [InlineData("ftp://*.foo.bar.com")]
        [InlineData("https://*.foo_bar.com")]
        public void WildcardEntryOutsideTheGrammarIsRejected(string entry)
        {
            Assert.False(ProxyPolicy.TryNormalizeTarget(entry, out _));
        }

        [Theory]
        [InlineData("https://*.foo.bar.com accept-untrusted", "https://*.foo.bar.com", true)]
        [InlineData("accept-untrusted https://*", "https://*", true)]
        [InlineData("*.cams.local:8443 accept-untrusted", "https://*.cams.local:8443", true)]
        public void WildcardTargetLineCarriesTheOptionalFlagLikeAnExactEntry(
            string line, string expectedEntry, bool expectedFlag)
        {
            Assert.True(ProxyPolicy.TryParseTargetEntry(line, out var entry, out var acceptUntrusted));
            Assert.Equal(expectedEntry, entry);
            Assert.Equal(expectedFlag, acceptUntrusted);
        }

        // #73: matching. "*.tail" covers one-or-more-label subdomains and NEVER the apex;
        // "*" covers every host except the SSRF floor; ports and schemes stay exact.

        [Theory]
        [InlineData("https://*.foo.bar.com", "https://baz.foo.bar.com/", true)]
        [InlineData("https://*.foo.bar.com", "https://a.b.foo.bar.com/", true)]
        [InlineData("https://*.foo.bar.com", "https://foo.bar.com/", false)]
        [InlineData("https://*.foo.bar.com", "https://xfoo.bar.com/", false)]
        [InlineData("https://*.foo.bar.com", "https://foo.bar.com.evil.test/", false)]
        [InlineData("https://*.foo.bar.com", "http://baz.foo.bar.com/", false)]
        [InlineData("https://*.foo.bar.com", "https://baz.foo.bar.com:8443/", false)]
        [InlineData("https://*.foo.bar.com:8443", "https://baz.foo.bar.com:8443/", true)]
        [InlineData("https://*.foo.bar.com:8443", "https://baz.foo.bar.com/", false)]
        [InlineData("https://*", "https://anything.example.test/", true)]
        [InlineData("https://*", "http://anything.example.test/", false)]
        [InlineData("https://*.FOO.bar.com", "https://BAZ.foo.BAR.com/", true)]
        public void WildcardEntryMatchesSubdomainsNeverTheApexOrForeignPorts(
            string entry, string requestUri, bool expected)
        {
            Assert.Equal(expected, ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri(requestUri)));
        }

        [Theory]
        [InlineData("https://*", "https://10.20.30.40/")]
        [InlineData("https://*", "https://[::1]:8443/")]
        [InlineData("https://*", "https://localhost:8443/")]
        [InlineData("https://*", "https://3232235777/")]
        [InlineData("https://*.joshooaj.com", "https://vms.joshooaj.com/")]
        [InlineData("https://*.internal", "https://api-gw.internal/")]
        public void WildcardsNeverMatchTheSsrfFloor(string entry, string requestUri)
        {
            var vmsHosts = new[] { "vms.joshooaj.com", "api-gw.internal" };
            Assert.False(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri(requestUri), vmsHosts));
        }

        [Theory]
        [InlineData("https://vms.joshooaj.com", "https://vms.joshooaj.com/", true)]
        [InlineData("https://10.20.30.40:8443", "https://10.20.30.40:8443/live", true)]
        [InlineData("https://localhost:8443", "https://localhost:8443/", true)]
        public void ExactEntriesMayStillNameFlooredHostsExplicitly(string entry, string requestUri, bool expected)
        {
            var vmsHosts = new[] { "vms.joshooaj.com" };
            Assert.Equal(expected, ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri(requestUri), vmsHosts));
        }

        [Theory]
        [InlineData("https://*.vms.joshooaj.com", "https://sub.vms.joshooaj.com/", true)]
        [InlineData("http://10.20.30.40:8080", "http://10.20.30.40:8080/", true)]
        public void TheFloorCoversTheVmsHostNotSubdomainsOfItNorExactEntriesUnderIt(
            string entry, string requestUri, bool expected)
        {
            var vmsHosts = new[] { "vms.joshooaj.com" };
            Assert.Equal(expected, ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri(requestUri), vmsHosts));
        }

        // #73 AC1: property-based pins. Every wildcard-matching claim above is one sample from
        // these invariants, re-checked over a seeded pseudo-random host space so "decided and
        // pinned in tests" covers whole classes, not hand-picked origins.

        [Fact]
        public void WildcardApexSubdomainAndFloorInvariantsHoldAcrossSeededRandomHosts()
        {
            var random = new Random(20261102);
            var vmsHosts = new[] { "vms.joshooaj.com" };

            for (var round = 0; round < 300; round++)
            {
                var tail = RandomLabels(random, random.Next(2, 4));
                var entry = "https://*." + tail;

                // One-or-more random leftmost labels: always covered.
                var sub = RandomLabels(random, random.Next(1, 4)) + "." + tail;
                Assert.True(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + sub + "/"), vmsHosts),
                    $"*.tail must cover subdomain '{sub}'");

                // Case permutations of the same host: still covered.
                Assert.True(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + sub.ToUpperInvariant() + "/"), vmsHosts),
                    "matching must stay case-insensitive");

                // The apex itself: never covered.
                Assert.False(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + tail + "/"), vmsHosts),
                    $"*.tail must NOT cover the apex '{tail}'");

                // A label glued onto the tail without a dot boundary: never covered.
                var glued = "evil" + tail;
                Assert.False(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + glued + "/"), vmsHosts),
                    "suffix matching must respect the label boundary");

                // The floor, whatever the tail: literal IPs, localhost, and VMS hosts never match.
                foreach (var flooredHost in new[] { "localhost", RandomIpv4(random), "[::2]", "vms.joshooaj.com" })
                {
                    Assert.False(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + flooredHost + "/"), vmsHosts),
                        $"a wildcard must never match floored host '{flooredHost}'");
                }

                // Ports stay exact both ways.
                Assert.False(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + sub + ":9443/"), vmsHosts));
                Assert.True(
                    ProxyPolicy.IsRequestAllowed(new[] { "https://*." + tail + ":9443" }, new Uri("https://" + sub + ":9443/"), vmsHosts));
            }
        }

        [Fact]
        public void ExactEntryInvariantsHoldAcrossSeededRandomHosts()
        {
            var random = new Random(20261103);

            for (var round = 0; round < 200; round++)
            {
                var host = RandomLabels(random, random.Next(1, 4));
                var entry = "https://" + host;

                Assert.True(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + host + "/any/path")));
                Assert.True(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("HTTPS://" + host.ToUpperInvariant() + "/")));
                Assert.False(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + RandomLabels(random, 1) + "." + host + "/")));
                Assert.False(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("http://" + host + "/")));
                Assert.False(ProxyPolicy.IsRequestAllowed(new[] { entry }, new Uri("https://" + host + ":8443/")));
            }
        }

        private static string RandomLabels(Random random, int count)
        {
            var labels = new string[count];
            for (var i = 0; i < count; i++)
            {
                var length = random.Next(1, 8);
                var chars = new char[length];
                for (var c = 0; c < length; c++)
                {
                    chars[c] = (char)('a' + random.Next(26));
                }

                labels[i] = new string(chars);
            }

            return string.Join(".", labels);
        }

        private static string RandomIpv4(Random random)
        {
            return $"{random.Next(1, 256)}.{random.Next(0, 256)}.{random.Next(0, 256)}.{random.Next(1, 256)}";
        }
    }
}
