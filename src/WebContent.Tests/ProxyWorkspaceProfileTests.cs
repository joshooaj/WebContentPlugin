using System;
using System.Linq;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #54: the per-view proxy state fed into browser-profile creation. The admin stores
    /// ProxyEnabled plus a canonical comma-separated ProxyTargets property (the #52 seeding path);
    /// this class turns that into the listener's allowlist. Enabled with no usable targets still
    /// proxies — everything then refuses, honoring "never direct" — and a disabled proxy creates
    /// no profile at all.
    /// </summary>
    public class ProxyWorkspaceProfileTests
    {
        [Fact]
        public void DisabledProxyCreatesNoProfile()
        {
            Assert.False(ProxyWorkspaceProfile.TryCreate(false, "http://10.0.0.5:8080", out var profile));
            Assert.Null(profile);
        }

        [Fact]
        public void EnabledProxyCarriesCanonicalTargetsAsTheListenerAllowlist()
        {
            Assert.True(ProxyWorkspaceProfile.TryCreate(true, "http://10.0.0.5:8080,https://portal.test:8443", out var profile));
            Assert.Equal(new[] { "http://10.0.0.5:8080", "https://portal.test:8443" }, profile.CanonicalTargets);
        }

        [Fact]
        public void DuplicateAndOverlappingEntriesCollapseOntoOneOrigin()
        {
            Assert.True(ProxyWorkspaceProfile.TryCreate(true, "http://10.0.0.5:8080,http://10.0.0.5:8080/, HTTP://10.0.0.5:8080", out var profile));
            Assert.Equal(new[] { "http://10.0.0.5:8080" }, profile.CanonicalTargets);
        }

        [Fact]
        public void EnabledProxyWithNoUsableTargetsStillProxiesAndAllowsNothing()
        {
            // Never direct: an enabled proxy with a blank or junk target list still routes the
            // browser through the listener, which then refuses every request. (A bare host is a
            // legitimate #52 shorthand, so the junk entry must be truly un-parseable.)
            Assert.True(ProxyWorkspaceProfile.TryCreate(true, ", ,not host at all,", out var profile));
            Assert.Empty(profile.CanonicalTargets);
        }
    }
}
