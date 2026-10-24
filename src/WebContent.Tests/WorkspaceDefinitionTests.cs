using System;
using System.Collections.Generic;
using Xunit;
using WebContent;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the pure workspace model and the multi-workspace selection used by
    /// <see cref="WorkspacePluginFactory"/> (ticket #8). A definition is provisionable only
    /// when it has a display name and a configured home address (JSON blob first, then the
    /// legacy HomeAddress/Address properties). <see cref="WorkspaceDefinition.SelectVisible"/>
    /// returns the provisionable definitions the current user may VIEW, in admin order.
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
        public void IsProvisionable_True_When_Name_And_Json_Address_Present()
        {
            var definition = Create("Ops", jsonHomeAddress: "example.com");

            Assert.True(definition.IsProvisionable);
        }

        [Fact]
        public void IsProvisionable_True_When_Name_And_Legacy_Address_Present()
        {
            var definition = Create("Ops", legacyAddress: "intranet.local");

            Assert.True(definition.IsProvisionable);
        }

        [Fact]
        public void IsProvisionable_False_When_Name_Missing()
        {
            var definition = Create(null, legacyAddress: "intranet.local");

            Assert.False(definition.IsProvisionable);
        }

        [Fact]
        public void IsProvisionable_False_When_Address_Missing()
        {
            var definition = Create("Ops");

            Assert.False(definition.IsProvisionable);
        }

        [Fact]
        public void IsProvisionable_False_When_Only_Whitespace_Name()
        {
            var definition = Create("  ", legacyAddress: "intranet.local");

            Assert.False(definition.IsProvisionable);
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
        public void SelectVisible_Skips_Non_Provisionable_Even_When_Permitted()
        {
            var noAddress = Create("NoAddress");
            var ok = Create("Ok", legacyAddress: "ok.local");

            var visible = WorkspaceDefinition.SelectVisible(
                new[] { noAddress, ok },
                new[] { noAddress.Id, ok.Id });

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
    }
}
