using System;
using System.Collections.Generic;
using Xunit;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the session registry that carries admin-defined workspace properties from the
    /// workspace plugin to the view item's own property store. The dictionary passed to
    /// InsertViewItemPlugin is not readable via ViewItemManager.GetProperty, so without this
    /// hand-off every property read falls back to the built-in defaults (the browser always
    /// opened the default home address instead of the configured one).
    /// </summary>
    public class WorkspaceInstancePropertiesTests
    {
        [Fact]
        public void Registered_Definition_Is_Found_By_Instance_Id()
        {
            var instanceId = Guid.NewGuid();
            WorkspaceInstanceProperties.Register(instanceId, new Dictionary<string, string> { ["HomeAddress"] = "monitor.local" });

            Assert.True(WorkspaceInstanceProperties.TryGet(instanceId, out var definition));
            Assert.Equal("monitor.local", definition["HomeAddress"]);
        }

        [Fact]
        public void Register_Snapshot_Insulates_Against_Later_Mutation()
        {
            var instanceId = Guid.NewGuid();
            var properties = new Dictionary<string, string> { ["HomeAddress"] = "monitor.local" };
            WorkspaceInstanceProperties.Register(instanceId, properties);

            properties["HomeAddress"] = "changed.local";

            Assert.True(WorkspaceInstanceProperties.TryGet(instanceId, out var definition));
            Assert.Equal("monitor.local", definition["HomeAddress"]);
        }

        [Fact]
        public void Re_Register_Replaces_The_Stored_Definition()
        {
            var instanceId = Guid.NewGuid();
            WorkspaceInstanceProperties.Register(instanceId, new Dictionary<string, string> { ["HomeAddress"] = "old.local" });
            WorkspaceInstanceProperties.Register(instanceId, new Dictionary<string, string> { ["HomeAddress"] = "new.local" });

            Assert.True(WorkspaceInstanceProperties.TryGet(instanceId, out var definition));
            Assert.Equal("new.local", definition["HomeAddress"]);
        }

        [Fact]
        public void Unknown_Instance_Id_Is_Not_Found()
        {
            Assert.False(WorkspaceInstanceProperties.TryGet(Guid.NewGuid(), out var definition));
            Assert.Null(definition);
        }

        [Fact]
        public void Empty_Instance_Id_Is_Rejected()
        {
            WorkspaceInstanceProperties.Register(Guid.Empty, new Dictionary<string, string> { ["HomeAddress"] = "monitor.local" });

            Assert.False(WorkspaceInstanceProperties.TryGet(Guid.Empty, out _));
        }

        [Fact]
        public void ComputeSeed_Writes_All_Properties_To_An_Empty_View_Item()
        {
            var definition = new Dictionary<string, string>
            {
                ["HomeAddress"] = "monitor.local",
                ["ZoomLevel"] = "150"
            };

            var seed = WorkspaceInstanceProperties.ComputeSeed(key => null, definition);

            Assert.Equal(2, seed.Count);
            Assert.Contains(seed, pair => pair.Key == "HomeAddress" && pair.Value == "monitor.local");
            Assert.Contains(seed, pair => pair.Key == "ZoomLevel" && pair.Value == "150");
        }

        [Fact]
        public void ComputeSeed_Writes_Only_Differing_Properties()
        {
            var current = new Dictionary<string, string> { ["HomeAddress"] = "monitor.local" };
            var definition = new Dictionary<string, string>
            {
                ["HomeAddress"] = "monitor.local",
                ["ZoomLevel"] = "150"
            };

            var seed = WorkspaceInstanceProperties.ComputeSeed(key => current.TryGetValue(key, out var value) ? value : null, definition);

            Assert.Single(seed);
            Assert.Equal("ZoomLevel", seed[0].Key);
        }

        [Fact]
        public void ComputeSeed_Overwrites_Stale_Workspace_Values()
        {
            var definition = new Dictionary<string, string> { ["HomeAddress"] = "new.local" };

            var seed = WorkspaceInstanceProperties.ComputeSeed(key => "old.local", definition);

            Assert.Single(seed);
            Assert.Equal("new.local", seed[0].Value);
        }

        [Fact]
        public void ComputeSeed_Handles_Null_Definition()
        {
            var seed = WorkspaceInstanceProperties.ComputeSeed(key => null, null);

            Assert.Empty(seed);
        }
    }
}
