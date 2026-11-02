using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Pins the Event Server rule-trigger wiring (#65) in ProxyFetchBackgroundPlugin.cs: the
    /// plugin raises its match events as the system-defined Server.NewEventCommand message with a
    /// full event header (the Event Server matches administrator Analytics Event definitions
    /// against the header's Message field, and a header without ID/Timestamp is dropped), the
    /// receiver contract is the matching NewEventIndication with detach at Close, and the payload
    /// travels as one JSON string like every other proxy payload. These are text guards over the
    /// real source file, not stubs, because the platform event types cannot be exercised in a
    /// unit test and the live gate only proves one labeled run, not the permanent shape.
    /// </summary>
    public class EventServerTriggerWiringGuardTests
    {
        private static readonly string PluginSourcePath = FindPluginSourcePath();
        private static readonly string PluginSource = File.ReadAllText(PluginSourcePath);

        private static string FindPluginSourcePath()
        {
            var directory = AppContext.BaseDirectory;
            while (directory != null && !Directory.Exists(Path.Combine(directory, "src")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            var path = Path.Combine(directory ?? throw new InvalidOperationException("no src root found"), "src", "WebContent", "ProxyFetchBackgroundPlugin.cs");
            Assert.True(File.Exists(path), $"Expected to find the Event Server plugin source at {path}.");
            return path;
        }

        [Fact]
        public void FetchMatchRaisesTheFixedEventTypeWithACompleteEventHeader()
        {
            // The admin's Analytics Event definition matches on the header's Message; ID and
            // Timestamp must be filled or the event is dropped by the platform.
            Assert.Matches(
                new Regex(@"EventHeader\s*=\s*new VideoOS\.Platform\.Data\.EventHeader\s*\{[^}]*ID\s*=\s*Guid\.NewGuid\(\)[^}]*Timestamp\s*=[^}]*Message\s*=\s*ProxyTriggerPolicy\.EventType", RegexOptions.Singleline),
                PluginSource);

            // One fixed event type for every row; per-row context rides in the CustomTag.
            Assert.Contains("CustomTag = $\"{workspaceName} |", PluginSource);
        }

        [Fact]
        public void MatchEventsRideSystemNewEventCommandAsOneJsonString()
        {
            // The raise route available to a component-hosted plugin is the system-defined
            // Server.NewEventCommand message; the payload stays one JSON string (the platform
            // binds custom types via Type.GetType and silently delivers null across hosts).
            Assert.Matches(
                new Regex(@"new Message\(MessageId\.Server\.NewEventCommand,\s*ProxyTriggerEventCodec\.ToWire\(analyticsEvent\)\)", RegexOptions.Singleline),
                PluginSource);

            // The matcher evaluates the served response per workspace, after the outcome exists.
            Assert.Contains("matcher.TryMatch(workspaceItemId, query.Uri, query.Method, responseStatus, out match)", PluginSource);
            Assert.Contains("RaiseTriggerEventsSafely(payload, query, result.StatusCode);", PluginSource);
        }

        [Fact]
        public void PluginObservesNewEventIndicationAndDetachesItOnClose()
        {
            Assert.Contains("new MessageIdFilter(MessageId.Server.NewEventIndication)", PluginSource);
            Assert.Contains("EnvironmentManager.Instance.RegisterReceiver(", PluginSource);
            Assert.Contains("_newEventIndicationRegistration = null;", PluginSource);
        }

        [Fact]
        public void WorkspaceAttributionAndFallbackSurviveTheFetchPath()
        {
            // rows come from the per-workspace configuration snapshot ...
            Assert.Contains("_triggerMatcher.Prime();", PluginSource);
            // ... the Smart Client listener stamps the workspace item id on every fetch ...
            var listenerSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(PluginSourcePath), "Client", "LoopbackProxyListener.cs"));
            Assert.Contains("query.WorkspaceItemId = _session.WorkspaceItemId;", listenerSource);
            // ... and the fallback attribution reads the diagnostics session map (fail closed).
            Assert.Contains("_diagnostics.TryGetSessionWorkspace(payload.SessionId, out mapped)", PluginSource);
            var aggregatorSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(PluginSourcePath), "ProxyDiagnosticsAggregator.cs"));
            Assert.Contains("internal bool TryGetSessionWorkspace(string sessionId, out string workspaceItemId)", aggregatorSource);
        }

        [Fact]
        public void TriggerStoreRefreshAndSelfTestStayGuardedAgainstSilentDrop()
        {
            // A silently broken row load would mean zero triggers forever with no trace: Init
            // primes the store and logs what loaded.
            Assert.Contains("Workspace rows refreshed: ", ReadMatcherSource());
            // The live-gate self-test fires only under an explicit operator switch.
            Assert.Contains("WEBCONTENT_TRIGGER_SELFTEST", PluginSource);
            Assert.Contains("trigger-selftest.flag", PluginSource);
        }

        private static string ReadMatcherSource()
        {
            var path = Path.Combine(Path.GetDirectoryName(PluginSourcePath), "ProxyTriggerMatcher.cs");
            Assert.True(File.Exists(path), $"Expected to find the trigger matcher at {path}.");
            return File.ReadAllText(path);
        }
    }
}
