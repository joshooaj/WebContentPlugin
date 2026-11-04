using System;
using System.Collections.Generic;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #66: the friendly proxy error page. Pins the wording layer as pure logic -
    /// every distinct refusal cause carries its own explicit cause category mapped 1:1 onto
    /// ProxyFetchErrorCodes (no collapsing distinct causes into one generic page), and the page
    /// renders headline + cause with the technical detail collapsed by default plus the
    /// actionable facts (exact origin, canonical allowlist entry, byte cap, verbatim message).
    /// </summary>
    public class ProxyErrorPageTests
    {
        [Fact]
        public void EveryWireErrorCodeMapsToItsOwnDistinctCategory()
        {
            // 1:1: every failure code names a category, and no two codes share one. Collapsing
            // (say) TlsValidation and Unreachable onto one generic cause is the exact failure
            // mode #66 forbids.
            var codes = new[]
            {
                ProxyFetchErrorCodes.BadRequest,
                ProxyFetchErrorCodes.Unreachable,
                ProxyFetchErrorCodes.Timeout,
                ProxyFetchErrorCodes.TooLarge,
                ProxyFetchErrorCodes.Error,
                ProxyFetchErrorCodes.NotAllowed,
                ProxyFetchErrorCodes.TlsValidation,
                ProxyFetchErrorCodes.NoAuthority,
                ProxyFetchErrorCodes.HandshakeRefused,
                ProxyFetchErrorCodes.ServerUnavailable,
            };

            var categories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var code in codes)
            {
                string category;
                Assert.True(ProxyErrorPage.TryCategoryForErrorCode(code, out category), $"no category mapped for wire code '{code}'");
                Assert.True(categories.Add(category), $"wire code '{code}' shares category '{category}' with another code");
            }

            Assert.Equal(codes.Length, categories.Count);
        }

        [Fact]
        public void UnknownAndNullCodesMapToNothingForTheCallerToDefault()
        {
            string category;
            Assert.False(ProxyErrorPage.TryCategoryForErrorCode("NoSuchCode", out category));
            Assert.Null(category);
            Assert.False(ProxyErrorPage.TryCategoryForErrorCode(null, out category));
        }

        [Fact]
        public void EveryCategoryCarriesFriendlyHeadlineCauseAndExplanation()
        {
            var categories = new[]
            {
                ProxyErrorPage.CategoryNotAllowed,
                ProxyErrorPage.CategoryResponseTooLarge,
                ProxyErrorPage.CategoryRequestTooLarge,
                ProxyErrorPage.CategoryTargetUnreachable,
                ProxyErrorPage.CategoryTargetTimeout,
                ProxyErrorPage.CategoryCertificateUntrusted,
                ProxyErrorPage.CategoryAuthorityUnavailable,
                ProxyErrorPage.CategoryTunnelFailed,
                ProxyErrorPage.CategoryComponentUnavailable,
                ProxyErrorPage.CategoryMalformedRequest,
                ProxyErrorPage.CategoryMethodNotAllowed,
                ProxyErrorPage.CategoryUnknownFailure,
                ProxyErrorPage.CategoryProfileOwned,
            };

            foreach (var category in categories)
            {
                Assert.False(string.IsNullOrWhiteSpace(ProxyErrorPage.CauseLabel(category)), category);
                Assert.False(string.IsNullOrWhiteSpace(ProxyErrorPage.Headline(category)), category);
                Assert.False(string.IsNullOrWhiteSpace(ProxyErrorPage.Explanation(category)), category);
            }
        }

        [Fact]
        public void CategoryNamesMatchTheMapTargets()
        {
            // The friendly six named in the ticket must be exactly the categories the
            // diagnostics-surface readers see for those wire codes (shared substrate, #69).
            string category;
            Assert.True(ProxyErrorPage.TryCategoryForErrorCode(ProxyFetchErrorCodes.NotAllowed, out category));
            Assert.Equal(ProxyErrorPage.CategoryNotAllowed, category);
            Assert.True(ProxyErrorPage.TryCategoryForErrorCode(ProxyFetchErrorCodes.TooLarge, out category));
            Assert.Equal(ProxyErrorPage.CategoryResponseTooLarge, category);
            Assert.True(ProxyErrorPage.TryCategoryForErrorCode(ProxyFetchErrorCodes.Unreachable, out category));
            Assert.Equal(ProxyErrorPage.CategoryTargetUnreachable, category);
            Assert.True(ProxyErrorPage.TryCategoryForErrorCode(ProxyFetchErrorCodes.Timeout, out category));
            Assert.Equal(ProxyErrorPage.CategoryTargetTimeout, category);
            Assert.True(ProxyErrorPage.TryCategoryForErrorCode(ProxyFetchErrorCodes.TlsValidation, out category));
            Assert.Equal(ProxyErrorPage.CategoryCertificateUntrusted, category);
            Assert.True(ProxyErrorPage.TryCategoryForErrorCode(ProxyFetchErrorCodes.HandshakeRefused, out category));
            Assert.Equal(ProxyErrorPage.CategoryTunnelFailed, category);
        }

        [Fact]
        public void PageShowsHeadlineAndCauseWithTechnicalDetailCollapsedByDefault()
        {
            var html = ProxyErrorPage.RenderHtml(
                403,
                "Forbidden",
                ProxyErrorPage.CategoryNotAllowed,
                new ProxyErrorPage.Details { TechnicalMessage = "The request origin 'http://evil.test' is not in the workspace proxy allowlist." });

            // Title shape kept byte-compatible with the live probes' "Web Content proxy:" match.
            Assert.Contains("<title>Web Content proxy: 403</title>", html);

            // Friendly layer visible up front: one cause category + headline + explanation.
            Assert.Contains("Not allowed by the administrator", html);
            Assert.Contains("This web address is not allowed for this view", html);
            Assert.Contains("only loads web addresses the administrator put on its allowlist", html);

            // Technical detail behind an expandable section, COLLAPSED by default (no open attr).
            Assert.Contains("<details><summary>", html);
            Assert.DoesNotContain("<details open", html);
            Assert.Contains("id=\"wdc-diagnostics\"", html);
            Assert.Contains("<button id=\"wdc-copy\" type=\"button\">Copy diagnostics</button>", html);

            // Fail-closed posture stated, decision NOT influenced by the page.
            Assert.Contains("Nothing was loaded outside the proxy", html);
        }

        [Fact]
        public void DiagnosticsBlockCarriesActionableFacts()
        {
            var request = new ProxyForwardedRequest
            {
                Method = "GET",
                RawTarget = "http://10.0.0.5:8080/monitor/live",
                Uri = new Uri("http://10.0.0.5:8080/monitor/live"),
            };

            var html = ProxyErrorPage.RenderHtml(
                403,
                "Forbidden",
                ProxyErrorPage.CategoryNotAllowed,
                new ProxyErrorPage.Details
                {
                    Method = request.Method,
                    RequestUri = request.Uri,
                    RawTarget = request.RawTarget,
                    DecidedBy = ProxyErrorPage.DecidedByLocalListener,
                    TechnicalMessage = "The request origin 'http://10.0.0.5:8080' is not in the workspace proxy allowlist.",
                    SuggestedAllowlistEntry = ProxyPolicy.CanonicalOrigin(request.Uri),
                });

            // Exact origin, the canonical allowlist entry that would fix it, method+url, side, timestamp.
            Assert.Contains("Allowlist entry that would admit this origin: http://10.0.0.5:8080", html);
            Assert.Contains("Target origin:     http://10.0.0.5:8080", html);
            Assert.Contains("GET http://10.0.0.5:8080/monitor/live", html);
            Assert.Contains("this computer (the workspace browser proxy)", html);
            Assert.Contains("When (UTC):", html);
            Assert.Contains("The request origin &#39;http://10.0.0.5:8080&#39; is not in the workspace proxy allowlist.", html);
        }

        [Fact]
        public void SizeCapRefusalsNameTheCapAndTheVerbatimFetcherText()
        {
            var html = ProxyErrorPage.RenderHtml(
                502,
                "Bad Gateway",
                ProxyErrorPage.CategoryResponseTooLarge,
                new ProxyErrorPage.Details
                {
                    DecidedBy = ProxyErrorPage.DecidedByVmsServer,
                    ByteCap = 5242880,
                    TechnicalMessage = "Response body from 10.0.0.5:8080 exceeded the 5242880 byte response cap.",
                });

            Assert.Contains("Response too large (proxy size cap)", html);
            Assert.Contains("exceeded the VMS proxy response size cap", html);
            Assert.Contains("Proxy byte cap:    5242880", html);
            Assert.Contains("exceeded the 5242880 byte response cap.", html);
        }

        [Fact]
        public void ServerSideAllowlistDriftKeepsTheDriftContextOnThePage()
        {
            // The 403 the VMS answers despite the local gate allowing it stays distinguishable
            // from the local 403 (#58 posture preserved through the #66 wording layer).
            var html = ProxyErrorPage.RenderHtml(
                403,
                "Forbidden",
                ProxyErrorPage.CategoryNotAllowed,
                new ProxyErrorPage.Details
                {
                    DecidedBy = ProxyErrorPage.DecidedByVmsServer,
                    Note = "The VMS server also refuses this target, although the local workspace decision allowed it (the two machines' proxy allowlists differ).",
                    TechnicalMessage = "The origin 'http://10.0.0.9:80' is not in the proxy allowlist of any proxied workspace.",
                });

            Assert.Contains("the VMS Event Server (the server-side proxy component)", html);
            Assert.Contains("the two machines&#39; proxy allowlists differ", html);
            Assert.Contains("not in the proxy allowlist of any proxied workspace.", html);
        }

        [Fact]
        public void AllInterpolatedTextIsHtmlEncoded()
        {
            var html = ProxyErrorPage.RenderHtml(
                502,
                "Bad Gateway",
                "custom-<script>alert(1)</script>-category",
                new ProxyErrorPage.Details { TechnicalMessage = "<script>alert('xss')</script>" });

            Assert.DoesNotContain("<script>alert", html);
            Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", html);

            // An unknown category renders the generic wording, still a well-formed page.
            Assert.Contains("Proxy failure", html);
            Assert.Contains("<title>Web Content proxy: 502</title>", html);
        }

        [Fact]
        public void SideAttributionLinesNameTheFailingSide()
        {
            // #58 side naming survives the wording layer: three attribution lines, one per side.
            var component = ProxyErrorPage.RenderHtml(503, "Service Unavailable", ProxyErrorPage.CategoryComponentUnavailable, new ProxyErrorPage.Details { DecidedBy = ProxyErrorPage.DecidedByLocalAboutVms });
            Assert.Contains("this computer (the VMS proxy component never answered)", component);
            Assert.Contains("The VMS server proxy component is not available", component);
            Assert.Contains("never loaded direct from this computer", component);

            var unreachable = ProxyErrorPage.RenderHtml(502, "Bad Gateway", ProxyErrorPage.CategoryTargetUnreachable, new ProxyErrorPage.Details { DecidedBy = ProxyErrorPage.DecidedByVmsServer });
            Assert.Contains("The VMS server could not reach the target", unreachable);
            Assert.Contains("not at this computer", unreachable);

            var timeout = ProxyErrorPage.RenderHtml(504, "Gateway Timeout", ProxyErrorPage.CategoryTargetTimeout, new ProxyErrorPage.Details { DecidedBy = ProxyErrorPage.DecidedByVmsServer });
            Assert.Contains("The VMS server could not get an answer from the target in time", timeout);
        }
    }
}
