using System;
using System.IO;
using System.Text.RegularExpressions;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Pins the #86 Event Server header-policy wiring by regex over the REAL source files: the
    /// composition root constructs and primes the store, ServeFetchAsync applies the request-leg
    /// rules to query.Headers BEFORE the fetch and the response-leg rules to result.Headers
    /// BEFORE framing (only for a real upstream status - a transport failure stays a pure error
    /// frame), and all three unconditional block sets derive from the one canonical
    /// ProxyHeaderPolicy.ForbiddenHeaderNames instead of restating the list. The runtime half of
    /// the acceptance (a forbidden stored row never reaches the wire) lives in the store tests;
    /// these guards keep the shape those tests describe actually wired into the plugin.
    /// </summary>
    public class EventServerHeaderPolicyWiringGuardTests
    {
        private static readonly string Source = ReadSource("ProxyFetchBackgroundPlugin.cs");
        private static readonly string CodecSource = ReadSource("ProxyForwardCodec.cs");
        private static readonly string TransportSource = ReadSource("ProxyFetchTransport.cs");

        private static string ReadSource(string fileName)
        {
            var directory = AppContext.BaseDirectory;
            while (directory != null && !Directory.Exists(Path.Combine(directory, "src")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            var path = Path.Combine(directory ?? throw new InvalidOperationException("no src root found"), "src", "WebContent", fileName);
            Assert.True(File.Exists(path), $"Expected to find the plugin source at {path}.");
            return File.ReadAllText(path);
        }

        [Fact]
        public void CompositionRootConstructsAndPrimesTheHeaderPolicyStore()
        {
            Assert.Matches(
                new Regex(@"_headerPolicies\s*=\s*new ProxyHeaderPolicyStore\(\s*ResolveWorkspaceHeaderSnapshots", RegexOptions.Singleline),
                Source);
            Assert.Contains("_headerPolicies.Prime();", Source);
        }

        [Fact]
        public void RequestRulesRewriteTheQueryHeadersBeforeTheFetcherRuns()
        {
            // Order is the point: the apply must sit between the query build and FetchAsync.
            var apply = Source.IndexOf("ProxyHeaderPolicy.ApplyRules(query.Headers", StringComparison.Ordinal);
            var fetch = Source.IndexOf("new ProxyHttpFetcher().FetchAsync(query", StringComparison.Ordinal);
            Assert.True(apply > 0, "request-leg apply missing");
            Assert.True(fetch > 0, "fetch call missing");
            Assert.True(apply < fetch, "request rules must be applied BEFORE the fetch");
        }

        [Fact]
        public void ResponseRulesRewriteTheResultHeadersBeforeFramingAndOnlyForARealStatus()
        {
            Assert.Matches(
                new Regex(@"policy\.ResponseRules\.Count > 0 && result != null && result\.StatusCode > 0\s*\)\s*\{\s*ProxyHeaderPolicy\.ApplyRules\(result\.Headers, policy\.ResponseRules\)", RegexOptions.Singleline),
                Source);

            var apply = Source.IndexOf("ProxyHeaderPolicy.ApplyRules(result.Headers", StringComparison.Ordinal);
            var frame = Source.IndexOf("ProxyFetchFramer.Frame(payload.CorrelationId", StringComparison.Ordinal);
            Assert.True(apply > 0 && frame > 0 && apply < frame, "response rules must be applied BEFORE the framer");
        }

        [Fact]
        public void WorkspaceAttributionForHeaderRulesMatchesTheCapAttribution()
        {
            // Same fallback chain as ResolveWorkspaceResponseCapBytes: payload stamp, then the
            // diagnostics session map, and a fault answers the EMPTY policy - never a refusal.
            Assert.Matches(
                new Regex(@"private WorkspaceHeaderPolicy ResolveWorkspaceHeaderPolicy\(ProxyFetchRequestPayload payload\)[\s\S]*?WorkspaceHeaderPolicy\.Empty;", RegexOptions.Singleline),
                Source);
            Assert.Contains("store.Resolve(workspaceItemId)", Source);
            Assert.Contains("_diagnostics.TryGetSessionWorkspace(payload.SessionId, out mapped)", Source);
        }

        [Fact]
        public void EveryUnconditionalBlockSetDerivesFromTheOneCanonicalForbiddenSet()
        {
            // The #86 explicitness requirement: hop-by-hop stripping exists in exactly one list,
            // and the three consumers derive from it instead of restating names.
            Assert.Matches(new Regex(@"new HashSet<string>\(ProxyHeaderPolicy\.ForbiddenHeaderNames, StringComparer\.OrdinalIgnoreCase\)", RegexOptions.Singleline), CodecSource);
            Assert.Contains("BuildRelayBlockedHeaders", CodecSource);
            Assert.Matches(new Regex(@"blocked\.Remove\(""Host""\)", RegexOptions.Singleline), CodecSource);

            Assert.Contains("BuildTransportBlockedRequestHeaders", TransportSource);
            Assert.Matches(new Regex(@"blocked\.Add\(""Expect""\)", RegexOptions.Singleline), TransportSource);

            // No consumer may restate the old literal sets beside the derived ones.
            Assert.DoesNotContain("\"Proxy-Authenticate\"", CodecSource);
            Assert.DoesNotContain("\"Proxy-Authenticate\"", TransportSource);
        }
    }
}
