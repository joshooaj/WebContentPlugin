using System;
using System.Collections.Generic;
using Xunit;
using WebContent;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #52: the workspace configuration carries a proxy toggle plus an allowlist of proxy
    /// targets (scheme, host, port). Definitions saved before the proxy existed — including the
    /// v1 blobs already stored by older plugin versions — must mean "proxy off" with no targets,
    /// and a corrupt or partial proxy section must never take down the config pipeline.
    /// </summary>
    public class ProxyConfigurationTests
    {
        private const string LegacyV1Blob =
            "{\"version\":1,\"layout\":\"1x1\",\"items\":[{\"type\":\"WebContent\",\"position\":0," +
            "\"settings\":{\"homeAddress\":\"https://legacy.example/\",\"hideToolbar\":false," +
            "\"hideAddress\":false,\"zoomLevel\":100,\"rememberLastAddress\":false," +
            "\"clearBrowsingDataOnExit\":false,\"allowBackgroundBrowser\":true," +
            "\"allowAudioNotifications\":true,\"muteAudio\":false,\"isolateBrowserInstances\":false," +
            "\"enableDevTools\":false}}]}";

        [Fact]
        public void DefinitionSavedBeforeProxyExistedMeansProxyOff()
        {
            var settings = WorkspaceConfiguration.Deserialize(LegacyV1Blob).GetFirstWebContentSettings();

            Assert.NotNull(settings);
            Assert.False(settings.ProxyEnabled);
            Assert.Null(settings.ProxyTargets);
        }

        [Fact]
        public void DefaultWorkspaceConfigurationLeavesProxyOff()
        {
            var settings = WorkspaceConfiguration.CreateDefault("https://example.com/").GetFirstWebContentSettings();

            Assert.False(settings.ProxyEnabled);
            Assert.Null(settings.ProxyTargets);
        }

        [Fact]
        public void ProxyToggleAndTargetsSurviveTheSerializedRoundtrip()
        {
            var config = WorkspaceConfiguration.CreateDefault("https://camera-ui.example/");
            var settings = config.GetFirstWebContentSettings();
            settings.ProxyEnabled = true;
            settings.ProxyTargets = new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "https", Host = "10.0.0.5", Port = 8443 },
                new ProxyTargetConfiguration { Scheme = "http", Host = "cam2.local", Port = 80 }
            };

            var parsed = WorkspaceConfiguration.Deserialize(config.Serialize());

            var proxied = parsed.GetFirstWebContentSettings();
            Assert.True(proxied.ProxyEnabled);
            Assert.Equal(2, proxied.ProxyTargets.Count);
            Assert.Equal("https", proxied.ProxyTargets[0].Scheme);
            Assert.Equal("10.0.0.5", proxied.ProxyTargets[0].Host);
            Assert.Equal(8443, proxied.ProxyTargets[0].Port);
            Assert.Equal("http", proxied.ProxyTargets[1].Scheme);
            Assert.Equal("cam2.local", proxied.ProxyTargets[1].Host);
            Assert.Equal(80, proxied.ProxyTargets[1].Port);
        }

        // #73: the stored entry shape gains a structured pattern member. Readers accept both
        // shapes forever (no migration write-back); a save writes the new one.

        private const string LegacyShapeBlob =
            "{\"version\":1,\"layout\":\"1x1\",\"items\":[{\"type\":\"WebContent\",\"position\":0," +
            "\"settings\":{\"homeAddress\":\"https://x/\",\"proxyEnabled\":true,\"proxyTargets\":[" +
            "{\"scheme\":\"https\",\"host\":\"portal.test\",\"port\":443,\"acceptUntrusted\":true}]}}]}";

        private const string PatternShapeBlob =
            "{\"version\":1,\"layout\":\"1x1\",\"items\":[{\"type\":\"WebContent\",\"position\":0," +
            "\"settings\":{\"homeAddress\":\"https://x/\",\"proxyEnabled\":true,\"proxyTargets\":[" +
            "{\"scheme\":\"https\",\"pattern\":\"*.portal.test\",\"port\":443,\"acceptUntrusted\":true}," +
            "{\"scheme\":\"https\",\"pattern\":\"*\",\"port\":443}]}}]}";

        [Fact]
        public void LegacyHostShapeStillReadsAndCanonicalizesThroughThePatternPath()
        {
            var settings = WorkspaceConfiguration.Deserialize(LegacyShapeBlob).GetFirstWebContentSettings();

            Assert.Equal(1, settings.ProxyTargets.Count);
            var entry = settings.ProxyTargets[0];
            Assert.Null(entry.Pattern);
            Assert.Equal("portal.test", entry.Host);
            Assert.Equal("portal.test", entry.EffectivePattern);
            Assert.True(entry.AcceptUntrusted);
        }

        [Fact]
        public void NewPatternShapeReadsIncludingWildcardEntries()
        {
            var settings = WorkspaceConfiguration.Deserialize(PatternShapeBlob).GetFirstWebContentSettings();

            Assert.Equal(2, settings.ProxyTargets.Count);
            Assert.Equal("*.portal.test", settings.ProxyTargets[0].EffectivePattern);
            Assert.True(settings.ProxyTargets[0].AcceptUntrusted);
            Assert.Equal("*", settings.ProxyTargets[1].EffectivePattern);
            Assert.False(settings.ProxyTargets[1].AcceptUntrusted);
        }

        [Fact]
        public void SavingAWorkspaceWritesTheNewStructuredPatternShape()
        {
            var config = WorkspaceConfiguration.CreateDefault("https://x/");
            var settings = config.GetFirstWebContentSettings();
            settings.ProxyEnabled = true;
            settings.ProxyTargets = new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "https", Pattern = "*.portal.test", Port = 443, AcceptUntrusted = true },
                new ProxyTargetConfiguration { Scheme = "https", Pattern = "exact.host", Port = 0 },
            };

            var json = config.Serialize();
            Assert.Contains("\"pattern\":\"*.portal.test\"", json);
            Assert.Contains("\"pattern\":\"exact.host\"", json);
            Assert.DoesNotContain("\"host\"", json);

            var reparsed = WorkspaceConfiguration.Deserialize(json).GetFirstWebContentSettings();
            Assert.Equal("*.portal.test", reparsed.ProxyTargets[0].EffectivePattern);
            Assert.True(reparsed.ProxyTargets[0].AcceptUntrusted);
            Assert.Equal("exact.host", reparsed.ProxyTargets[1].EffectivePattern);
            Assert.Equal(0, reparsed.ProxyTargets[1].Port);
        }

        [Fact]
        public void ReadingALegacyWorkspaceNeverRewritesTheStoredShapeByItself()
        {
            // No migration write-back: deserializing a legacy blob and re-serializing it (the
            // read path does this on inspection) keeps the legacy host member.
            var config = WorkspaceConfiguration.Deserialize(LegacyShapeBlob);
            var json = config.Serialize();

            Assert.Contains("\"host\":\"portal.test\"", json);
            Assert.DoesNotContain("\"pattern\"", json);
            Assert.Equal("portal.test", WorkspaceConfiguration.Deserialize(json).GetFirstWebContentSettings().ProxyTargets[0].EffectivePattern);
        }
    }
}
