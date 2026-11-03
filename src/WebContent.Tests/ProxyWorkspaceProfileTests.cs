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
            Assert.False(ProxyWorkspaceProfile.TryCreate(false, "http://10.0.0.5:8080", null, null, out var profile));
            Assert.Null(profile);
        }

        [Fact]
        public void EnabledProxyCarriesCanonicalTargetsAsTheListenerAllowlist()
        {
            Assert.True(ProxyWorkspaceProfile.TryCreate(true, "http://10.0.0.5:8080,https://portal.test:8443", null, null, out var profile));
            Assert.Equal(new[] { "http://10.0.0.5:8080", "https://portal.test:8443" }, profile.CanonicalTargets);
        }

        [Fact]
        public void DuplicateAndOverlappingEntriesCollapseOntoOneOrigin()
        {
            Assert.True(ProxyWorkspaceProfile.TryCreate(true, "http://10.0.0.5:8080,http://10.0.0.5:8080/, HTTP://10.0.0.5:8080", null, null, out var profile));
            Assert.Equal(new[] { "http://10.0.0.5:8080" }, profile.CanonicalTargets);
        }

        [Fact]
        public void EnabledProxyWithNoUsableTargetsStillProxiesAndAllowsNothing()
        {
            // Never direct: an enabled proxy with a blank or junk target list still routes the
            // browser through the listener, which then refuses every request. (A bare host is a
            // legitimate #52 shorthand, so the junk entry must be truly un-parseable.)
            Assert.True(ProxyWorkspaceProfile.TryCreate(true, ", ,not host at all,", null, null, out var profile));
            Assert.Empty(profile.CanonicalTargets);
        }

        [Fact]
        public void WorkspaceItemIdRidesTheProfileForDiagnosticsAttribution()
        {
            // #69: the listener's session context is built from the profile, so the workspace id
            // the seeding path published must survive into it; a standalone item keeps empty.
            Assert.True(ProxyWorkspaceProfile.TryCreate(true, "http://10.0.0.5:8080", "c2c1b7f2-1111-2222-3333-444455556666", "fp-2026", out var profile));
            Assert.Equal("c2c1b7f2-1111-2222-3333-444455556666", profile.WorkspaceItemId);
            Assert.Equal("fp-2026", profile.WorkspaceFingerprint);

            Assert.True(ProxyWorkspaceProfile.TryCreate(true, "http://10.0.0.5:8080", null, null, out var standalone));
            Assert.Equal(string.Empty, standalone.WorkspaceItemId);
            Assert.Equal(string.Empty, standalone.WorkspaceFingerprint);
        }

        [Fact]
        public void WildcardPatternEntriesSurviveTheSeededPropertyIntact()
        {
            // #73: the comma-separated seeding property carries pattern entries the same way it
            // carries exact origins - the listener's matcher decides their scope, the profile
            // just transports them canonically.
            Assert.True(ProxyWorkspaceProfile.TryCreate(
                true, "https://*.portal.test,http://*.cams.local:8080,https://*", null, null, out var profile));
            Assert.Equal(
                new[] { "https://*.portal.test", "http://*.cams.local:8080", "https://*" },
                profile.CanonicalTargets);
        }

        [Fact]
        public void VmsFloorHostsTravelWithTheProfileForTheListenersMatcher()
        {
            // #73: the client gate and the Event Server gate must floor the same hosts.
            Assert.True(ProxyWorkspaceProfile.TryCreate(
                true, "https://*", null, null, out var profile, new[] { " VMS.Joshooaj.com ", "vms.joshooaj.com", " " }));
            Assert.Equal(new[] { "vms.joshooaj.com" }, profile.VmsFloorHosts);

            Assert.True(ProxyWorkspaceProfile.TryCreate(true, "https://*", null, null, out var bare));
            Assert.Empty(bare.VmsFloorHosts);
        }
    }
}
