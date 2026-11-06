using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #72: the per-browser deployment-CA trust posture the Smart Client banner renders.
    /// The holder decides nothing about trust itself - the pins here are that it fires only on
    /// real changes (per-page-load mint traffic never re-triggers banner work), that the banner
    /// condition IsUntrustedConfirmed needs BOTH a reported CA and a negative verdict, and that
    /// switching the proxy off clears the posture so a non-proxied view can never show a CA banner.
    /// </summary>
    public class ProxyCaTrustStateTests
    {
        [Fact]
        public void FreshStateHasNoBannerCondition()
        {
            var state = new ProxyCaTrustState();

            Assert.False(state.HasAuthority);
            Assert.False(state.IsTrusted);
            Assert.False(state.IsUntrustedConfirmed);
        }

        [Fact]
        public void FirstUntrustedReportRaisesChangeAndConfirmsTheBannerCondition()
        {
            var state = new ProxyCaTrustState();
            var changes = 0;
            state.Changed += () => changes++;

            var changed = state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false);

            Assert.True(changed);
            Assert.Equal(1, changes);
            Assert.True(state.HasAuthority);
            Assert.True(state.IsUntrustedConfirmed);
            Assert.Equal("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", state.AuthorityThumbprint);
            Assert.Equal("ZGVy", state.AuthorityDerBase64);
        }

        [Fact]
        public void SamePostureAgainFiresNothing()
        {
            var state = new ProxyCaTrustState();
            state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false);
            var changes = 0;
            state.Changed += () => changes++;

            var changed = state.Publish("aaaabbbbccccddddeeeeffff0000111122223333", "ZGVy", false);

            // Case-insensitive thumbprint equality (the store prints hex uppercase): every page
            // load re-publishes the same posture and must stay silent.
            Assert.False(changed);
            Assert.Equal(0, changes);
        }

        [Fact]
        public void TrustFlipRaisesChangeAndClearsTheBannerCondition()
        {
            var state = new ProxyCaTrustState();
            state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false);
            var changes = 0;
            state.Changed += () => changes++;

            var changed = state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", true);

            Assert.True(changed);
            Assert.Equal(1, changes);
            Assert.True(state.IsTrusted);
            Assert.False(state.IsUntrustedConfirmed);
        }

        [Fact]
        public void DifferentAuthorityThumbprintRaisesChangeEvenWithTheSameVerdict()
        {
            var state = new ProxyCaTrustState();
            state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false);
            var changes = 0;
            state.Changed += () => changes++;

            var changed = state.Publish("FFFFEEEDDDCCCBBBAAA000011112222333344445", "YWJj", false);

            // A redeployed CA is a different CA: the per-session banner dismissal was keyed to the
            // old thumbprint, so the new authority re-surfaces the banner.
            Assert.True(changed);
            Assert.Equal(1, changes);
        }

        [Fact]
        public void ClearAfterAReportRaisesChangeAndKillsTheBannerCondition()
        {
            var state = new ProxyCaTrustState();
            state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false);
            var changes = 0;
            state.Changed += () => changes++;

            state.Clear();

            Assert.Equal(1, changes);
            Assert.False(state.HasAuthority);
            Assert.False(state.IsUntrustedConfirmed);
            Assert.Null(state.AuthorityThumbprint);
            Assert.Null(state.AuthorityDerBase64);
        }

        [Fact]
        public void ClearWhenNothingWasRecordedFiresNothing()
        {
            var state = new ProxyCaTrustState();
            var changes = 0;
            state.Changed += () => changes++;

            state.Clear();

            Assert.Equal(0, changes);
        }

        [Fact]
        public void EmptyThumbprintIsNeverPublished()
        {
            var state = new ProxyCaTrustState();

            var changed = state.Publish(null, "ZGVy", false);

            Assert.False(changed);
            Assert.False(state.HasAuthority);
        }

        [Fact]
        public void ThrowingSubscriberNeverReachesThePublisher()
        {
            var state = new ProxyCaTrustState();
            state.Changed += () => throw new InvalidOperationException("banner callback blew up");

            // The mint thread calls Publish; a throwing UI subscriber must not fault the tunnel.
            state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false);
        }

        [Fact]
        public void LastMintOriginRefreshesQuietlyAndNeverEntersChangeDetection()
        {
            // #80: the flip-to-untrusted report names the mint that produced the verdict, so
            // Publish carries an origin - but the origin is bookkeeping, NOT posture. Every page
            // load mints a different origin against the same known posture and must stay silent.
            var state = new ProxyCaTrustState();
            var changes = 0;
            state.Changed += () => changes++;

            Assert.True(state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false, "crm.example:443"));
            Assert.Equal(1, changes);
            Assert.Equal("crm.example:443", state.LastMintOrigin);

            Assert.False(state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false, "api.example:443"));
            Assert.Equal(1, changes);
            Assert.Equal("api.example:443", state.LastMintOrigin);

            // Origin is optional on Publish (the #72 callers predate it); a blank origin keeps
            // the last known one rather than wiping the detail the report needs.
            Assert.False(state.Publish("AAAABBBBCCCCDDDDEEEEFFFF0000111122223333", "ZGVy", false));
            Assert.Equal("api.example:443", state.LastMintOrigin);
        }
    }

    /// <summary>
    /// Ticket #72: the mint flow feeds the posture. Pins: every real mint publishes (the leaf
    /// cache short-circuits otherwise), the root-store probe is consulted per mint rather than
    /// trusted from the wire, and InvalidateLeafCache forces a re-mint that re-checks a flipped
    /// posture - which is what lets a mid-session CA fix work without restarting Smart Client.
    /// </summary>
    public class ProxyLoopbackTlsTrustPostureTests : IDisposable
    {
        private static readonly Action<string> Sink = _ => { };

        private readonly Authority _authority = Authority.Create(Sink);
        private readonly List<string> _probes = new List<string>();
        private bool _trusted = true;
        private readonly ProxyCaTrustState _state = new ProxyCaTrustState();
        private readonly ProxyLoopbackTls _tls;

        public ProxyLoopbackTlsTrustPostureTests()
        {
            _tls = new ProxyLoopbackTls(
                (host, port, spki) => Task.FromResult(new ProxyLeafResponsePayload
                {
                    Success = true,
                    LeafDerBase64 = Convert.ToBase64String(_authority.MintLeafForPublicKey(host, spki)),
                    AuthorityDerBase64 = Convert.ToBase64String(_authority.Certificate.RawData),
                    AuthorityThumbprint = _authority.Certificate.Thumbprint,
                }),
                Sink,
                _state,
                thumbprint => { lock (_probes) _probes.Add(thumbprint); return _trusted; });
        }

        [Fact]
        public async Task EveryMintPublishesThePostureFromTheLocalRootStoreProbe()
        {
            var leaf = await _tls.GetOrCreateLeafCertificateAsync("cam1.test", 8443);

            Assert.NotNull(leaf);
            Assert.True(_state.HasAuthority);
            Assert.Equal(_authority.Certificate.Thumbprint, _state.AuthorityThumbprint);
            Assert.Equal(Convert.ToBase64String(_authority.Certificate.RawData), _state.AuthorityDerBase64);
            Assert.True(_state.IsTrusted);
            Assert.Equal(new[] { _authority.Certificate.Thumbprint }, _probes.ToArray());
        }

        [Fact]
        public async Task CachedLeavesDoNotRepublishButAnUntrustedDeploymentConfirmsTheBanner()
        {
            _trusted = false;
            await _tls.GetOrCreateLeafCertificateAsync("cam1.test", 8443);
            await _tls.GetOrCreateLeafCertificateAsync("cam1.test", 8443);

            // The cache serves the second origin request without a mint, so the probe ran once.
            Assert.Single(_probes);
            Assert.True(_state.IsUntrustedConfirmed);
        }

        [Fact]
        public async Task InvalidateLeafCacheMakesTheNextMintReCheckAFippedPosture()
        {
            _trusted = false;
            var untrusted = await _tls.GetOrCreateLeafCertificateAsync("cam1.test", 8443);
            var untrustedThumbprint = untrusted.Thumbprint;
            Assert.True(_state.IsUntrustedConfirmed);

            _tls.InvalidateLeafCache();
            _trusted = true;
            var reminted = await _tls.GetOrCreateLeafCertificateAsync("cam1.test", 8443);

            // The fix imported the CA; the invalidated cache means the next tunnel gets a fresh
            // leaf AND the posture flips without any restart. (InvalidateLeafCache disposes the
            // old leaf's handle, so the identity comparison uses the captured thumbprint.)
            Assert.NotEqual(untrustedThumbprint, reminted.Thumbprint);
            Assert.True(_state.IsTrusted);
            Assert.False(_state.IsUntrustedConfirmed);
            Assert.Equal(2, _probes.Count);
        }

        [Fact]
        public async Task MintPublishesCarryTheMintOriginTheUntrustedReportNeeds()
        {
            // #80: the flip-to-untrusted report names the https origin behind the browser's
            // certificate error, sourced from the mint's own cache key - so the posture holder
            // must know it even on the FIRST mint (the flip fires there, not later).
            await _tls.GetOrCreateLeafCertificateAsync("cam1.test", 8443);

            Assert.Equal("cam1.test:8443", _state.LastMintOrigin);
        }

        public void Dispose()
        {
            _tls?.Dispose();
            _authority?.Dispose();
        }
    }
}
