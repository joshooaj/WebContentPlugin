using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #80 live-shape guards: the aggregate crosses three surfaces no unit harness can
    /// host end to end - the Smart Client listener that turns a #72 posture FLIP into exactly one
    /// CertUntrusted block report named after the mint's origin, the Event Server ingress that
    /// attributes only client-decided untrusted reports to a machine, and the Management Client
    /// Diagnostics banner that aggregates per machine and carries the #75 export inline. The
    /// aggregation maths itself is pinned in ProxyDiagnosticsAggregatorTests and the row render
    /// in ProxyDiagnosticsViewTests; the live rows sit in docs/verification-matrix.md.
    /// </summary>
    public class ProxyCaUntrustedReportWiringGuardTests
    {
        private static string Read(params string[] parts)
        {
            var segments = new[] { RepoLocator.FindRepoRoot(), "src", "WebContent" }.Concat(parts).ToArray();
            return File.ReadAllText(Path.Combine(segments));
        }

        private static string Collapse(string source)
        {
            return Regex.Replace(source, @"\s+", " ");
        }

        // ---- client: the flip-to-untrusted report ------------------------------------------------

        [Fact]
        public void TheListenerSubscribesToThePostureAndUnsubscribesOnStop()
        {
            var collapsed = Collapse(Read("Client", "LoopbackProxyListener.cs"));

            // One subscription per listener lifetime, torn down with it: a re-wired listener does
            // not double-report, and a stopped browser never reports from beyond its own grave.
            Assert.Contains("if (_caTrust != null) { _caTrust.Changed += OnProxyCaTrustChanged; }", collapsed);
            Assert.Contains("if (_caTrust != null) { _caTrust.Changed -= OnProxyCaTrustChanged; }", collapsed);
        }

        [Fact]
        public void OnlyTheConfirmedUntrustedPostureReportsAndItNamesTheMintOrigin()
        {
            var collapsed = Collapse(Read("Client", "LoopbackProxyListener.cs"));

            // Fixed, cleared, or never-confirmed postures send nothing; the report can only exist
            // when a mint named an origin, and it files under the https origin the browser
            // actually dialled (the mint cacheKey is host:port).
            Assert.Contains("if (_caTrust == null || !_caTrust.IsUntrustedConfirmed)", collapsed);
            Assert.Contains("var mintOrigin = _caTrust.LastMintOrigin;", collapsed);
            Assert.Contains("Uri.TryCreate(\"https://\" + mintOrigin, UriKind.Absolute, out var origin)", collapsed);
        }

        [Fact]
        public void TheReportGoesThroughTheBlockReporterNeverTheBlockedResourceTally()
        {
            var collapsed = Collapse(Read("Client", "LoopbackProxyListener.cs"));

            var handler = Regex.Match(
                collapsed,
                @"private void OnProxyCaTrustChanged\(\) \{ (?<body>.*?) InvalidateLeafCache",
                RegexOptions.Singleline);
            Assert.True(handler.Success, "OnProxyCaTrustChanged not found before InvalidateLeafCache.");

            // The ES-side machine attribution consumes block reports; the #83 tally counts URL
            // refusals for the user indicator, and a CA trust problem is not one - double
            // counting would mislabel the condition (the #72 banner already surfaces it locally).
            Assert.Contains("_blocks.Report(origin, ProxyBlockReasons.CertUntrusted);", handler.Groups["body"].Value);
            Assert.DoesNotContain("_tally", handler.Groups["body"].Value, System.StringComparison.Ordinal);
        }

        // ---- Event Server: attribution only for client-decided untrusted reports ------------------

        [Fact]
        public void TheIngressAttributesMachineReportsOnlyOnTheNewMachineTransition()
        {
            var collapsed = Collapse(Read("ProxyFetchBackgroundPlugin.cs"));

            // Client-decided CertUntrusted WITH a machine name, and RecordUntrustedAuthorityReport
            // returns true only for the FIRST report from that machine - so the operator-facing
            // log line fires once per machine, not once per view per page load.
            Assert.Contains(
                "if (string.Equals(report.Reason, ProxyBlockReasons.CertUntrusted, StringComparison.Ordinal)",
                collapsed);
            Assert.Contains("&& !string.IsNullOrEmpty(report.ClientMachine)", collapsed);
            Assert.Contains(
                "&& diagnostics.RecordUntrustedAuthorityReport(report.WorkspaceItemId, report.ClientMachine, DateTime.UtcNow))",
                collapsed);
            Assert.Contains(
                "Proxy CA trust report recorded: workspace='{report.WorkspaceItemId}' machine='{report.ClientMachine}'",
                Read("ProxyFetchBackgroundPlugin.cs"));
        }

        [Fact]
        public void ExactlyOneCallSiteFeedsTheMachineTable()
        {
            var source = Read("ProxyFetchBackgroundPlugin.cs");

            // The ES's own TLS failures go through RecordBlockForSession (target-chain at the
            // SERVER), and must never attribute a workstation to the machine table. Counting the
            // single sanctioned call site keeps a future second call site from creeping in
            // unreviewed - it would need a deliberate edit here too.
            var calls = Regex.Matches(source, "RecordUntrustedAuthorityReport\\(").Count;
            Assert.Equal(1, calls);
            Assert.True(Regex.Matches(source, "RecordBlockForSession\\(").Count >= 1);
        }

        // ---- Management Client: the aggregate banner and its inline export ------------------------

        [Fact]
        public void TheBannerSurfaceExistsOnTheDiagnosticsTabAndStartsCollapsed()
        {
            var xaml = Read("Admin", "WorkspaceConfigPage.xaml");

            Assert.Contains("<Border Grid.Row=\"2\" x:Name=\"caUntrustedBanner\" Style=\"{StaticResource WarningBannerStyle}\" Visibility=\"Collapsed\"",
                Collapse(xaml));
            Assert.Contains("x:Name=\"txtCaUntrustedSummary\"", xaml);
            Assert.Contains("x:Name=\"txtCaUntrustedMachines\"", xaml);
            Assert.Contains("x:Name=\"btnCaUntrustedExport\"", xaml);
            Assert.Contains("Click=\"OnCaUntrustedExport\"", xaml);

            // The probe-addressable ids: the banner text and the inline export button.
            Assert.Contains("AutomationProperties.AutomationId=\"caUntrustedBannerText\"", xaml);
            Assert.Contains("AutomationProperties.AutomationId=\"proxyCaExportFromDiagnostics\"", xaml);
        }

        [Fact]
        public void TheBannerShowsOnlyForNamedMachinesAndTheExportSharesTheOverviewExchange()
        {
            var collapsed = Collapse(Read("Admin", "WorkspaceConfigPage.xaml.cs"));

            // Zero machines (or a failed pull) never show the banner, and the last export result
            // clears when it leaves - no stale sentence on a later visit.
            Assert.Contains(
                "if (caUntrustedBanner == null || view == null || view.UntrustedMachineCount <= 0)",
                collapsed);
            Assert.Contains("Strings.CaUntrustedBannerFormat", collapsed);
            Assert.Contains("Strings.CaUntrustedMachineFormat", collapsed);

            // The inline export is the SAME pull and the SAME pure failure sentences as the #75
            // overview button, single-flighted like it.
            Assert.Contains("var response = await client.RequestProxyCertificateAsync();", collapsed);
            Assert.Contains("ProxyCaExportFailureSentence.For(view.FailureCode, response)", collapsed);
            Assert.Contains("if (_caExportRunning)", collapsed);
        }

        [Fact]
        public void TheBannerWordingStaysInTheResourcesTheInstallerLocalizes()
        {
            var resx = Read("Strings.resx");

            // The acceptance sentence names the machines and points at the export; the machine
            // list and per-machine report counts render from their own formats.
            Assert.Contains("<value>{0} client machine(s) report that the proxy certificate is NOT trusted", resx);
            Assert.Contains("<value>Machines: {0}</value>", resx);
            Assert.Contains("<value>{0} ({1} report(s))</value>", resx);
        }
    }
}
