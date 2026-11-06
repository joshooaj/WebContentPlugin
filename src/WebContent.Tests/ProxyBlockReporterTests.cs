using System;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #69: the per-listener block reporter. It must build a complete payload from its
    /// session context, stay silent when the view has no workspace to file under, and swallow
    /// every send fault - the diagnostics channel must never disturb the browser flow.
    /// </summary>
    public class ProxyBlockReporterTests
    {
        private static ProxyViewSessionContext Context()
        {
            return new ProxyViewSessionContext
            {
                SessionId = "s-1",
                WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001",
                WorkspaceFingerprint = "fp-1",
                ClientMachine = "BEAST",
            };
        }

        [Fact]
        public void ReportBuildsAPayloadCarryingSessionAttributionAndCanonicalOrigin()
        {
            ProxyBlockReportPayload sent = null;
            var reporter = new ProxyBlockReporter(Context(), payload => sent = payload, null);

            reporter.Report(new Uri("https://Vendor.Test:8443/some/deep/path?query=1"), ProxyBlockReasons.NotAllowed);

            Assert.NotNull(sent);
            Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", sent.WorkspaceItemId);
            Assert.Equal("fp-1", sent.WorkspaceFingerprint);
            Assert.Equal("BEAST", sent.ClientMachine);
            Assert.Equal("https://vendor.test:8443", sent.Origin);
            Assert.Equal(ProxyBlockReasons.NotAllowed, sent.Reason);
            Assert.Equal(1, sent.Count);
        }

        [Fact]
        public void StandaloneViewsWithoutAWorkspaceFileNothing()
        {
            var sends = 0;
            var reporter = new ProxyBlockReporter(new ProxyViewSessionContext { SessionId = "s-1" }, payload => sends++, null);
            reporter.Report(new Uri("http://one.test:80/"), ProxyBlockReasons.NotAllowed);
            Assert.Equal(0, sends);
        }

        [Fact]
        public void EmptyReasonOrOriginSendsNothing()
        {
            var sends = 0;
            var reporter = new ProxyBlockReporter(Context(), payload => sends++, null);
            reporter.Report(new Uri("http://one.test:80/"), null);
            reporter.Report(null, ProxyBlockReasons.NotAllowed);
            Assert.Equal(0, sends);
        }

        [Fact]
        public void AThrowingSendIsSwallowedAndLogged()
        {
            var log = string.Empty;
            var reporter = new ProxyBlockReporter(
                Context(),
                payload => throw new InvalidOperationException("channel gone"),
                message => log = message);

            reporter.Report(new Uri("http://one.test:80/"), ProxyBlockReasons.TunnelRefused);
            Assert.Contains("channel gone", log);
        }
    }
}
