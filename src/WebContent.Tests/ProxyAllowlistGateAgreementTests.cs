using System;
using System.Collections.Generic;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #73 AC2, extended by #88: the gates that must never disagree - the Event Server
    /// fetch gate (<see cref="ProxyServerAllowlist"/>, which is also what the leaf-mint gate
    /// <see cref="ProxyLeafIssuer"/> consults, i.e. the allowlist IS the minting policy), the
    /// client forward policy (<see cref="ProxyForwardPolicy"/>), and the view-layer https refusal
    /// (<see cref="ProxyViewRefusal"/>, #88) - consume ONE canonical matcher over ONE canonical
    /// entry shape. This matrix feeds the identical (entries, request-origin) pairs through all
    /// of them and pins identical verdicts, legacy host records and new pattern records alike.
    /// The view gate adds its own scheme rule on top: it answers https only (http rides the
    /// proxy's absolute-form 403), never wider than the forward gate would.
    /// </summary>
    public class ProxyAllowlistGateAgreementTests
    {
        private static readonly Action<string> Sink = _ => { };

        // One entry set covering both stored shapes (legacy "host", new "pattern") and both
        // entry kinds (exact origin, leftmost-label wildcard).
        private static List<Dictionary<string, string>> WorkspaceEntries() =>
            new List<Dictionary<string, string>>
            {
                Workspace(true,
                    "{\"scheme\":\"https\",\"host\":\"cam1.test\",\"port\":8443}",
                    "{\"scheme\":\"http\",\"host\":\"10.0.0.5\",\"port\":8080}",
                    "{\"scheme\":\"https\",\"pattern\":\"*.portal.test\",\"port\":443}",
                    "{\"scheme\":\"http\",\"pattern\":\"*.cams.local\",\"port\":8080}"),
            };

        private static Dictionary<string, string> Workspace(bool enabled, params string[] targetJsonObjects)
        {
            var targetsJson = "[" + string.Join(",", targetJsonObjects) + "]";
            return new Dictionary<string, string>
            {
                ["WorkspaceConfig"] =
                    "{\"version\":1,\"layout\":\"1x1\",\"items\":[{\"type\":\"WebContent\",\"position\":0,\"settings\":" +
                    "{\"homeAddress\":\"http://x/\",\"proxyEnabled\":" + (enabled ? "true" : "false") +
                    ",\"proxyTargets\":" + targetsJson + "}}]}",
            };
        }

        // The canonical entries the client listener was seeded with - the same set, canonical form.
        private static readonly string[] SeededEntries =
        {
            "https://cam1.test:8443",
            "http://10.0.0.5:8080",
            "https://*.portal.test",
            "http://*.cams.local:8080",
        };

        private static readonly string[] VmsFloorHosts = { "vms.joshooaj.com" };

        private static readonly Uri[] Probes =
        {
            new Uri("https://cam1.test:8443/live/index.html"),   // legacy exact entry
            new Uri("https://sub.cam1.test:8443/"),              // exact entry never covers subdomains
            new Uri("https://cam1.test:9443/"),                  // port stays exact
            new Uri("http://10.0.0.5:8080/monitor/live"),        // explicit exact entry MAY name an IP
            new Uri("https://10.0.0.5:8080/"),                   // https-scheme at that IP: no entry, refused
            new Uri("https://a.portal.test/dashboard"),          // wildcard covers a subdomain
            new Uri("https://a.b.portal.test/dashboard"),        // ...and a deep subdomain
            new Uri("https://portal.test/"),                     // ...but never the apex
            new Uri("https://evilportal.test/"),                 // ...and never across a label boundary
            new Uri("http://cam9.cams.local:8080/live"),         // wildcard with an explicit port
            new Uri("http://cam9.cams.local:9090/live"),         // port exact across wildcards too
        };

        private static bool ExpectedAllowed(Uri probe)
        {
            // The truth table the matcher itself documents; the three gates must each reproduce it.
            switch (probe.Host.ToLowerInvariant())
            {
                case "cam1.test":
                    return probe.Port == 8443 && probe.Scheme == "https";
                case "10.0.0.5":
                    return probe.Scheme == "http" && probe.Port == 8080;
                case "a.portal.test":
                case "a.b.portal.test":
                    return probe.Scheme == "https" && probe.IsDefaultPort;
                case "portal.test":
                case "evilportal.test":
                    return false;
                case "cam9.cams.local":
                    return probe.Scheme == "http" && probe.Port == 8080;
                default:
                    return false;
            }
        }

        [Fact]
        public void AllThreeGatesReachTheSameVerdictOnEveryProbe()
        {
            var serverGate = new ProxyServerAllowlist(WorkspaceEntries, Sink, () => VmsFloorHosts);
            var mintGate = new ProxyLeafIssuer(
                serverGate,
                () => throw new InvalidOperationException("authority seam: a refused mint never reaches the CA"),
                Sink);

            foreach (var probe in Probes)
            {
                var expected = ExpectedAllowed(probe);

                var serverVerdict = serverGate.IsAllowed(probe, out _);
                Assert.Equal(expected, serverVerdict);

                var head = $"GET {probe.AbsoluteUri} HTTP/1.1\r\nHost: host\r\n\r\n";
                var clientVerdict = ProxyForwardPolicy.Decide(SeededEntries, head, null, VmsFloorHosts, out _, out _);
                Assert.Equal(expected ? ProxyForwardDisposition.Forward : ProxyForwardDisposition.OriginNotAllowed, clientVerdict);

                // #88: the view-layer https gate joins the matrix. It answers https only (plain
                // http stays on the absolute-form forward path), and on its scheme its verdict
                // matches every other gate: allowed origins reach the network, refused origins
                // get the view's own 403 page.
                var viewRefused = ProxyViewRefusal.TryRefuseHttpsRequest(SeededEntries, VmsFloorHosts, probe, "GET", out _, out var viewHtml);
                if (probe.Scheme == Uri.UriSchemeHttps)
                {
                    Assert.Equal(!expected, viewRefused);
                    if (viewRefused)
                    {
                        Assert.Contains("not in the workspace proxy allowlist", viewHtml);
                    }
                    else
                    {
                        Assert.Null(viewHtml);
                    }
                }
                else
                {
                    Assert.False(viewRefused);
                }

                // The mint gate is https-only; on its scheme it must follow the identical policy:
                // allowed mints proceed past the gate and fail only at the authority seam,
                // refused mints stop at NotAllowed before the authority is ever consulted.
                if (probe.Scheme == Uri.UriSchemeHttps)
                {
                    var mint = mintGate.Issue(new ProxyLeafRequestPayload
                    {
                        CorrelationId = Guid.NewGuid().ToString("N"),
                        Host = probe.Host,
                        Port = probe.Port,
                        SpkiBase64 = "AQID",
                    });
                    Assert.False(mint.Success);
                    Assert.Equal(expected ? ProxyFetchErrorCodes.NoAuthority : ProxyFetchErrorCodes.NotAllowed, mint.ErrorCode);
                }
            }
        }

        [Fact]
        public void FullHostHatchAgreesAcrossTheGatesUnderTheSameFloor()
        {
            var entries = new[] { "https://*" };
            var serverGate = new ProxyServerAllowlist(
                () => new List<Dictionary<string, string>>
                {
                    Workspace(true, "{\"scheme\":\"https\",\"pattern\":\"*\",\"port\":443}"),
                },
                Sink,
                () => VmsFloorHosts);

            foreach (var host in new[] { "anything.example.test", "deep.sub.example.test", "vms.joshooaj.com", "10.20.30.40", "localhost", "[::1]" })
            {
                var expected = host != "vms.joshooaj.com" && host != "10.20.30.40" && host != "localhost" && host != "[::1]";
                var probe = new Uri($"https://{host}/");

                Assert.Equal(expected, serverGate.IsAllowed(probe, out _));

                var head = $"GET {probe.AbsoluteUri} HTTP/1.1\r\nHost: host\r\n\r\n";
                Assert.Equal(
                    expected ? ProxyForwardDisposition.Forward : ProxyForwardDisposition.OriginNotAllowed,
                    ProxyForwardPolicy.Decide(entries, head, null, VmsFloorHosts, out _, out _));

                // #88: the view gate honours the same floor - the full-host hatch admits hosts
                // at the view exactly as at the other gates, floored hosts stay refused.
                Assert.Equal(!expected, ProxyViewRefusal.TryRefuseHttpsRequest(entries, VmsFloorHosts, probe, "GET", out _, out _));
            }
        }
    }
}
