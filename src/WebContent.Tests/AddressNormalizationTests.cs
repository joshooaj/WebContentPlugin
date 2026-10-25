using WebContent;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #14: operator-typed addresses (hostname, host:port, full URL) must normalize to the
    /// same canonical navigation target in the setup flow and the runtime flow, and garbage must
    /// be rejected instead of being coerced into a bogus URI.
    /// </summary>
    public class AddressNormalizationTests
    {
        [Theory]
        [InlineData("example.com", "https://example.com/")]
        [InlineData("  example.com  ", "https://example.com/")]
        [InlineData("sub.example.co.uk", "https://sub.example.co.uk/")]
        [InlineData("server:8443", "https://server:8443/")]
        [InlineData("server:8443/", "https://server:8443/")]
        [InlineData("localhost:3000/app", "https://localhost:3000/app")]
        [InlineData("192.168.1.10:8080", "https://192.168.1.10:8080/")]
        public void OperatorSuppliedHostSpecsNormalizeToHttpsNavigationTargets(string input, string expected)
        {
            Assert.True(WebContentConfiguration.TryCreateNavigationUri(input, out var uri));
            Assert.Equal(expected, uri.ToString(), ignoreCase: true);
        }

        [Theory]
        [InlineData("https://example.com/path?x=1", "https://example.com/path?x=1")]
        [InlineData("http://example.com", "http://example.com/")]
        [InlineData("HTTP://Example.COM", "http://Example.COM/")]
        [InlineData("  https://example.com/a#b  ", "https://example.com/a#b")]
        public void FullHttpUrlsAreAcceptedWithoutRewritingTheirScheme(string input, string expected)
        {
            Assert.True(WebContentConfiguration.TryCreateNavigationUri(input, out var uri));
            Assert.Equal(expected, uri.ToString(), ignoreCase: true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a valid url")]
        [InlineData("javascript:alert(1)")]
        [InlineData("javascript:alert(document.location)")]
        [InlineData("mailto:a@b.com")]
        [InlineData("ftp://files.example.com")]
        [InlineData("data:text/html,test")]
        [InlineData(@"C:\path\file")]
        [InlineData("server:99999")]
        [InlineData("https://")]
        [InlineData("-invalid host-")]
        public void GarbageAndNonHttpSchemesAreRejected(string input)
        {
            Assert.False(WebContentConfiguration.TryCreateNavigationUri(input, out _));
        }

        [Fact]
        public void SetupHomeAdminLastAddressAndRuntimeNavigationProduceTheSameCanonicalAddress()
        {
            var setup = WebContentConfiguration.NormalizeHomeAddress("server:8443", "unused.example.com");
            var last = WebContentConfiguration.NormalizeOptionalAddress("server:8443");
            var runtime = WebContentConfiguration.CreateNavigationUri("server:8443").ToString();

            Assert.Equal("https://server:8443/", setup);
            Assert.Equal(setup, last);
            Assert.Equal(setup, runtime);
        }

        [Fact]
        public void InvalidInputFallsBackToTheFallbackAddressNormalizedByTheSameRules()
        {
            var uri = WebContentConfiguration.CreateNavigationUri("not a valid url", "server:8443");
            Assert.Equal("https://server:8443/", uri.AbsoluteUri);
        }

        [Fact]
        public void TheStoredCanonicalFormIsTheAbsoluteUriUsedForNavigation()
        {
            var stored = WebContentConfiguration.NormalizeHomeAddress("https://host/search?q=a b", "unused.example.com");
            var navigated = WebContentConfiguration.CreateNavigationUri("https://host/search?q=a b").AbsoluteUri;

            Assert.Equal("https://host/search?q=a%20b", stored);
            Assert.Equal(navigated, stored);
        }
    }
}
