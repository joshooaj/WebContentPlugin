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
    }
}
