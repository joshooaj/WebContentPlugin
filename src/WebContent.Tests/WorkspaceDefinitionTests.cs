using System;
using System.Collections.Generic;
using Xunit;
using WebContent;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the pure workspace model and the multi-workspace selection used by
    /// <see cref="WorkspacePluginFactory"/> (tickets #8 and #9). A workspace is displayed as a
    /// tab when it has a name and the user may VIEW it, in admin order; since ticket #9 a named
    /// workspace with a broken or missing config stays visible so its tab can carry a
    /// WorkSpaceNotification instead of silently disappearing, and
    /// <see cref="WorkspaceDefinition.ConfigurationError"/> names the defect (JSON blob first,
    /// then the legacy HomeAddress/Address properties).
    /// </summary>
    public class WorkspaceDefinitionTests
    {
        private static WorkspaceDefinition Create(
            string name,
            string jsonHomeAddress = null,
            string legacyAddress = null,
            string legacyHomeAddress = null)
        {
            var properties = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(jsonHomeAddress))
            {
                var config = WorkspaceConfiguration.CreateDefault(jsonHomeAddress);
                properties[WorkspaceConfiguration.WorkspaceConfigKey] = config.Serialize();
            }

            if (!string.IsNullOrEmpty(legacyAddress))
            {
                properties["Address"] = legacyAddress;
            }

            if (!string.IsNullOrEmpty(legacyHomeAddress))
            {
                properties["HomeAddress"] = legacyHomeAddress;
            }

            return new WorkspaceDefinition(Guid.NewGuid(), name, properties);
        }

        private static WorkspaceDefinition CreateWithRawConfig(string name, string rawConfigJson, string legacyAddress = null)
        {
            var properties = new Dictionary<string, string>();
            if (rawConfigJson != null)
            {
                properties[WorkspaceConfiguration.WorkspaceConfigKey] = rawConfigJson;
            }

            if (!string.IsNullOrEmpty(legacyAddress))
            {
                properties["Address"] = legacyAddress;
            }

            return new WorkspaceDefinition(Guid.NewGuid(), name, properties);
        }

        [Fact]
        public void HomeAddress_Reads_Json_Blob_When_Present()
        {
            var definition = Create("Ops", jsonHomeAddress: "example.com");

            Assert.Equal("example.com", definition.HomeAddress);
        }

        [Fact]
        public void HomeAddress_Falls_Back_To_Legacy_Address()
        {
            var definition = Create("Ops", legacyAddress: "intranet.local");

            Assert.Equal("intranet.local", definition.HomeAddress);
        }

        [Fact]
        public void HomeAddress_Falls_Back_To_Legacy_HomeAddress_Property()
        {
            var definition = Create("Ops", legacyHomeAddress: "monitor.local");

            Assert.Equal("monitor.local", definition.HomeAddress);
        }

        [Fact]
        public void HomeAddress_Prefers_Json_Over_Legacy_Properties()
        {
            var definition = Create("Ops", jsonHomeAddress: "example.com", legacyAddress: "intranet.local");

            Assert.Equal("example.com", definition.HomeAddress);
        }

        [Fact]
        public void HomeAddress_Is_Empty_When_Nothing_Configured()
        {
            var definition = Create("Ops");

            Assert.Equal(string.Empty, definition.HomeAddress);
        }

        [Fact]
        public void IsDisplayable_True_When_Name_Present_Even_Without_Address()
        {
            Assert.True(Create("Ops", jsonHomeAddress: "example.com").IsDisplayable);
            Assert.True(Create("Ops").IsDisplayable);
        }

        [Fact]
        public void IsDisplayable_False_When_Name_Missing()
        {
            Assert.False(Create(null, legacyAddress: "intranet.local").IsDisplayable);
        }

        [Fact]
        public void IsDisplayable_False_When_Only_Whitespace_Name()
        {
            Assert.False(Create("  ", legacyAddress: "intranet.local").IsDisplayable);
        }

        [Fact]
        public void SelectVisible_Returns_Provisionable_And_Permitted_In_Admin_Order()
        {
            var first = Create("First", legacyAddress: "first.local");
            var second = Create("Second", legacyAddress: "second.local");
            var third = Create("Third", legacyAddress: "third.local");

            var visible = WorkspaceDefinition.SelectVisible(
                new[] { first, second, third },
                new[] { first.Id, third.Id });

            Assert.Equal(new[] { first, third }, visible);
        }

        [Fact]
        public void SelectVisible_Skips_Workspaces_Without_View_Permission()
        {
            var first = Create("First", legacyAddress: "first.local");
            var second = Create("Second", legacyAddress: "second.local");

            var visible = WorkspaceDefinition.SelectVisible(
                new[] { first, second },
                new[] { first.Id });

            Assert.Single(visible);
            Assert.Same(first, visible[0]);
        }

        [Fact]
        public void SelectVisible_Includes_Config_Broken_But_Permitted_Workspace()
        {
            var noAddress = Create("NoAddress");
            var ok = Create("Ok", legacyAddress: "ok.local");

            var visible = WorkspaceDefinition.SelectVisible(
                new[] { noAddress, ok },
                new[] { noAddress.Id, ok.Id });

            Assert.Equal(new[] { noAddress, ok }, visible);
            Assert.Equal(WorkspaceConfigError.MissingBrowserConfig, noAddress.ConfigurationError);
        }

        [Fact]
        public void SelectVisible_Still_Skips_Unnamed_Workspaces_Even_When_Permitted()
        {
            var nameless = Create(null, legacyAddress: "hidden.local");
            var ok = Create("Ok", legacyAddress: "ok.local");

            var visible = WorkspaceDefinition.SelectVisible(
                new[] { nameless, ok },
                new[] { nameless.Id, ok.Id });

            Assert.Single(visible);
            Assert.Same(ok, visible[0]);
        }

        [Fact]
        public void SelectVisible_Returns_Empty_When_No_Workspace_Is_Permitted()
        {
            var first = Create("First", legacyAddress: "first.local");

            var visible = WorkspaceDefinition.SelectVisible(new[] { first }, Array.Empty<Guid>());

            Assert.Empty(visible);
        }

        [Fact]
        public void SelectVisible_Returns_Empty_When_List_Empty()
        {
            var visible = WorkspaceDefinition.SelectVisible(new WorkspaceDefinition[0], new[] { Guid.NewGuid() });

            Assert.Empty(visible);
        }

        [Fact]
        public void SelectVisible_Handles_Null_List()
        {
            var visible = WorkspaceDefinition.SelectVisible(null, new[] { Guid.NewGuid() });

            Assert.Empty(visible);
        }

        [Fact]
        public void SelectVisible_Handles_Null_Permitted_Ids()
        {
            var valid = Create("Valid", legacyAddress: "valid.local");

            var visible = WorkspaceDefinition.SelectVisible(new[] { valid }, null);

            Assert.Empty(visible);
        }

        [Fact]
        public void SelectVisible_Skips_Null_Entries()
        {
            var valid = Create("Valid", legacyAddress: "valid.local");

            var visible = WorkspaceDefinition.SelectVisible(new WorkspaceDefinition[] { null, valid }, new[] { valid.Id });

            Assert.Single(visible);
            Assert.Same(valid, visible[0]);
        }

        [Fact]
        public void ConfigurationError_None_For_Json_Blob_With_Home_Address()
        {
            var definition = Create("Ops", jsonHomeAddress: "example.com");

            Assert.Equal(WorkspaceConfigError.None, definition.ConfigurationError);
        }

        [Fact]
        public void ConfigurationError_None_For_Legacy_Only_Definition()
        {
            var definition = Create("Ops", legacyAddress: "intranet.local");

            Assert.Equal(WorkspaceConfigError.None, definition.ConfigurationError);
        }

        [Fact]
        public void ConfigurationError_MalformedJson_When_Blob_Fails_To_Parse()
        {
            var definition = CreateWithRawConfig("Ops", "{ this is not json");

            Assert.Equal(WorkspaceConfigError.MalformedJson, definition.ConfigurationError);
        }

        [Fact]
        public void ConfigurationError_MalformedJson_Even_When_Legacy_Properties_Would_Rescue_It()
        {
            var definition = CreateWithRawConfig("Ops", "{ corrupt", legacyAddress: "intranet.local");

            Assert.Equal(WorkspaceConfigError.MalformedJson, definition.ConfigurationError);
            Assert.Equal("intranet.local", definition.HomeAddress);
        }

        [Fact]
        public void ConfigurationError_MissingBrowserConfig_When_Blob_Carries_No_Items()
        {
            var definition = CreateWithRawConfig("Ops", "[]");

            Assert.Equal(WorkspaceConfigError.MissingBrowserConfig, definition.ConfigurationError);
        }

        [Fact]
        public void ConfigurationError_MissingBrowserConfig_When_WebContent_Item_Has_No_Home_Address()
        {
            var definition = CreateWithRawConfig("Ops", "{\"version\":1,\"items\":[{\"type\":\"WebContent\",\"position\":0}]}");

            Assert.Equal(WorkspaceConfigError.MissingBrowserConfig, definition.ConfigurationError);
        }

        [Fact]
        public void ConfigurationError_MissingBrowserConfig_When_Nothing_Is_Configured()
        {
            var definition = Create("Ops");

            Assert.Equal(WorkspaceConfigError.MissingBrowserConfig, definition.ConfigurationError);
        }

        [Fact]
        public void ConfigurationError_MissingBrowserConfig_When_Only_Whitespace_Name_And_Legacy_Address()
        {
            var definition = Create("  ", legacyAddress: "intranet.local");

            Assert.Equal(WorkspaceConfigError.None, definition.ConfigurationError);
            Assert.False(definition.IsDisplayable);
        }
    }
}
