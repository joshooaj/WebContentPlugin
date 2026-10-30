using System;
using System.Collections.Generic;
using Xunit;
using WebContent;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the pure view-item property builder used by <see cref="WorkspacePluginFactory"/>
    /// (ticket #7). It turns an admin-defined workspace into the effective property dictionary that
    /// the WebContent view item reads, reading the JSON blob first and falling back to the legacy
    /// Name + Address properties. Home address normalization, zoom clamping, and the
    /// hide-toolbar-forces-hide-address rule are all applied here.
    /// </summary>
    public class WorkspacePropertiesTests
    {
        private const string FallbackHomeAddress = "https://fallback.example/";

        private static WorkspaceConfiguration CreateConfig(
            string homeAddress,
            bool hideToolbar = false,
            bool hideAddress = false,
            double zoomLevel = 100,
            bool rememberLastAddress = false,
            bool clearBrowsingDataOnExit = false,
            bool allowBackgroundBrowser = true,
            bool allowAudioNotifications = true,
            bool muteAudio = false,
            bool isolateBrowserInstances = false,
            bool enableDevTools = false)
        {
            var config = WorkspaceConfiguration.CreateDefault(homeAddress);
            var settings = config.Items[0].Settings;
            settings.HideToolbar = hideToolbar;
            settings.HideAddress = hideAddress;
            settings.ZoomLevel = zoomLevel;
            settings.RememberLastAddress = rememberLastAddress;
            settings.ClearBrowsingDataOnExit = clearBrowsingDataOnExit;
            settings.AllowBackgroundBrowser = allowBackgroundBrowser;
            settings.AllowAudioNotifications = allowAudioNotifications;
            settings.MuteAudio = muteAudio;
            settings.IsolateBrowserInstances = isolateBrowserInstances;
            settings.EnableDevTools = enableDevTools;
            return config;
        }

        [Fact]
        public void Json_Blob_Builds_Full_Property_Set()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string>
            {
                [WorkspaceConfiguration.WorkspaceConfigKey] =
                    CreateConfig("example.com", hideToolbar: true, hideAddress: true, zoomLevel: 150).Serialize()
            };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal(id.ToString(), properties["WorkspaceId"]);
            Assert.Equal("Ops", properties["WorkspaceName"]);
            Assert.Equal(id.ToString(), properties["InstanceId"]);
            Assert.Equal("https://example.com/", properties["HomeAddress"]);
            Assert.Equal("True", properties["HideToolbar"]);
            Assert.Equal("False", properties["HideAddress"]);
            Assert.Equal("150", properties["ZoomLevel"]);
            Assert.Equal("False", properties["RememberLastAddress"]);
            Assert.Equal("False", properties["ClearBrowsingDataOnExit"]);
            Assert.Equal("True", properties["AllowBackgroundBrowser"]);
            Assert.Equal("True", properties["AllowAudioNotifications"]);
            Assert.Equal("False", properties["MuteAudio"]);
            Assert.Equal("False", properties["IsolateBrowserInstances"]);
            Assert.Equal("False", properties["EnableDevTools"]);
        }

        [Fact]
        public void Json_Blob_HideAddress_Allowed_When_Toolbar_Visible()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string>
            {
                [WorkspaceConfiguration.WorkspaceConfigKey] =
                    CreateConfig("example.com", hideToolbar: false, hideAddress: true).Serialize()
            };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("False", properties["HideToolbar"]);
            Assert.Equal("True", properties["HideAddress"]);
        }

        [Fact]
        public void Json_Blob_Zoom_Level_Is_Clamped()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string>
            {
                [WorkspaceConfiguration.WorkspaceConfigKey] =
                    CreateConfig("example.com", zoomLevel: 5000).Serialize()
            };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("500", properties["ZoomLevel"]);
        }

        [Fact]
        public void Legacy_Path_Uses_Address_Property()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string> { ["Address"] = "intranet.local" };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("https://intranet.local/", properties["HomeAddress"]);
        }

        [Fact]
        public void Legacy_Path_Prefers_HomeAddress_Over_Address()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string>
            {
                ["Address"] = "intranet.local",
                ["HomeAddress"] = "monitor.local"
            };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("https://monitor.local/", properties["HomeAddress"]);
        }

        [Fact]
        public void Legacy_Path_InstanceId_Defaults_To_Workspace_Id()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string> { ["Address"] = "intranet.local" };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal(id.ToString(), properties["InstanceId"]);
        }

        [Fact]
        public void Legacy_Path_InstanceId_Uses_Configured_Value()
        {
            var id = Guid.NewGuid();
            var configuredId = Guid.NewGuid();
            var source = new Dictionary<string, string>
            {
                ["Address"] = "intranet.local",
                ["InstanceId"] = configuredId.ToString()
            };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal(configuredId.ToString(), properties["InstanceId"]);
        }

        [Fact]
        public void Legacy_Path_HideToolbar_Forces_HideAddress_False()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string>
            {
                ["Address"] = "intranet.local",
                ["HideToolbar"] = "True",
                ["HideAddress"] = "True"
            };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("True", properties["HideToolbar"]);
            Assert.Equal("False", properties["HideAddress"]);
        }

        [Fact]
        public void Legacy_Path_Empty_Address_Uses_Fallback()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string>();

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("https://fallback.example/", properties["HomeAddress"]);
        }

        [Fact]
        public void Json_Blob_Empty_Address_Uses_Fallback()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string>
            {
                [WorkspaceConfiguration.WorkspaceConfigKey] = CreateConfig(string.Empty).Serialize()
            };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("https://fallback.example/", properties["HomeAddress"]);
        }

        [Fact]
        public void Build_Adds_Stable_Definition_Fingerprint()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string> { ["Address"] = "intranet.local" };

            var first = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);
            var second = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.NotEmpty(first["DefinitionFingerprint"]);
            Assert.Equal(first["DefinitionFingerprint"], second["DefinitionFingerprint"]);
        }

        [Fact]
        public void Build_Changes_Fingerprint_When_Admin_Edits_Definition()
        {
            var id = Guid.NewGuid();

            var before = WorkspaceProperties.Build(
                id, "Ops", new Dictionary<string, string> { ["Address"] = "old.local" }, FallbackHomeAddress);
            var after = WorkspaceProperties.Build(
                id, "Ops", new Dictionary<string, string> { ["Address"] = "new.local" }, FallbackHomeAddress);

            Assert.NotEqual(before["DefinitionFingerprint"], after["DefinitionFingerprint"]);
        }

        [Fact]
        public void Json_Blob_Proxy_On_Emits_Canonical_Proxy_Properties()
        {
            var id = Guid.NewGuid();
            var config = CreateConfig("camera.example");
            var settings = config.Items[0].Settings;
            settings.ProxyEnabled = true;
            settings.ProxyTargets = new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "https", Host = "10.0.0.5", Port = 8443 },
                new ProxyTargetConfiguration { Scheme = "http", Host = "Cam2.local", Port = 80 },
            };
            var source = new Dictionary<string, string> { [WorkspaceConfiguration.WorkspaceConfigKey] = config.Serialize() };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("True", properties["ProxyEnabled"]);
            Assert.Equal("https://10.0.0.5:8443,http://cam2.local", properties["ProxyTargets"]);
        }

        [Fact]
        public void Json_Blob_Proxy_Off_Emits_Disabled_State()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string>
            {
                [WorkspaceConfiguration.WorkspaceConfigKey] = CreateConfig("example.com").Serialize()
            };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("False", properties["ProxyEnabled"]);
            Assert.Equal(string.Empty, properties["ProxyTargets"]);
        }

        [Fact]
        public void Legacy_Path_Proxy_Is_Off()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string> { ["Address"] = "intranet.local" };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("False", properties["ProxyEnabled"]);
            Assert.Equal(string.Empty, properties["ProxyTargets"]);
        }

        [Fact]
        public void HandCraftedProxyTargetGibberish_IsSkipped_AndValidEntriesSurvive()
        {
            var id = Guid.NewGuid();
            var config = CreateConfig("camera.example");
            var settings = config.Items[0].Settings;
            settings.ProxyEnabled = true;
            settings.ProxyTargets = new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "ftp", Host = "files.example", Port = 21 },
                new ProxyTargetConfiguration { Scheme = "https", Host = " ", Port = 8443 },
                new ProxyTargetConfiguration { Scheme = "https", Host = "cam2.local", Port = 70000 },
                new ProxyTargetConfiguration { Scheme = "https", Host = "10.0.0.5", Port = 8443 },
            };
            var source = new Dictionary<string, string> { [WorkspaceConfiguration.WorkspaceConfigKey] = config.Serialize() };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("https://10.0.0.5:8443", properties["ProxyTargets"]);
        }

        [Fact]
        public void Proxy_Off_Preserves_Stored_Targets_For_The_Next_Toggle_On()
        {
            var id = Guid.NewGuid();
            var config = CreateConfig("camera.example");
            var settings = config.Items[0].Settings;
            settings.ProxyEnabled = false;
            settings.ProxyTargets = new List<ProxyTargetConfiguration>
            {
                new ProxyTargetConfiguration { Scheme = "https", Host = "10.0.0.5", Port = 8443 },
            };
            var source = new Dictionary<string, string> { [WorkspaceConfiguration.WorkspaceConfigKey] = config.Serialize() };

            var properties = WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Equal("False", properties["ProxyEnabled"]);
            Assert.Equal("https://10.0.0.5:8443", properties["ProxyTargets"]);
        }

        [Fact]
        public void Source_Properties_Are_Not_Mutated()
        {
            var id = Guid.NewGuid();
            var source = new Dictionary<string, string> { ["Address"] = "intranet.local" };

            WorkspaceProperties.Build(id, "Ops", source, FallbackHomeAddress);

            Assert.Single(source);
            Assert.DoesNotContain("HomeAddress", source.Keys);
            Assert.DoesNotContain("InstanceId", source.Keys);
        }
    }
}
