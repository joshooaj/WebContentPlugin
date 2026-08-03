using System;
using System.Collections.Generic;
using Xunit;
using WebContent;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the pure workspace model and the "first valid config" selection used by
    /// <see cref="WorkspacePluginFactory"/> (ticket #7). A definition is provisionable only
    /// when it has a display name and a configured home address (JSON blob first, then the
    /// legacy HomeAddress/Address properties).
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
        public void SelectFirstProvisionable_Returns_First_Qualifying_Definition()
        {
            var skipped = Create("Skipped");
            var first = Create("First", legacyAddress: "first.local");
            var second = Create("Second", legacyAddress: "second.local");

            var selected = WorkspaceDefinition.SelectFirstProvisionable(new[] { skipped, first, second });

            Assert.Same(first, selected);
        }

        [Fact]
        public void SelectFirstProvisionable_Returns_Null_When_List_Empty()
        {
            var selected = WorkspaceDefinition.SelectFirstProvisionable(new WorkspaceDefinition[0]);

            Assert.Null(selected);
        }

        [Fact]
        public void SelectFirstProvisionable_Returns_Null_When_None_Qualify()
        {
            var selected = WorkspaceDefinition.SelectFirstProvisionable(new[]
            {
                Create("One"),
                Create("Two")
            });

            Assert.Null(selected);
        }

        [Fact]
        public void SelectFirstProvisionable_Skips_Null_Entries()
        {
            var valid = Create("Valid", legacyAddress: "valid.local");

            var selected = WorkspaceDefinition.SelectFirstProvisionable(new WorkspaceDefinition[] { null, valid, null });

            Assert.Same(valid, selected);
        }

        [Fact]
        public void SelectFirstProvisionable_Handles_Null_List()
        {
            var selected = WorkspaceDefinition.SelectFirstProvisionable(null);

            Assert.Null(selected);
        }
    }
}
