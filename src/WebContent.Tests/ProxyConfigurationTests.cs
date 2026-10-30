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
    }
}
