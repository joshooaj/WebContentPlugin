using System;
using System.Collections.Generic;

using WebContent.Client;

using Xunit;

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

        // ---- ticket #71: locked (security-relevant) keys are enforced, cosmetic keys are not ----

        [Fact]
        public void ComputeSeed_Overwrites_A_Hand_Edited_Home_Address_Override()
        {
            // A per-instance HomeAddress override (left from an older build or hand-edited in the
            // store) never survives into runtime: the definition wins on every seed.
            var definition = new Dictionary<string, string> { ["HomeAddress"] = "admin.local" };

            var seed = WorkspaceInstanceProperties.ComputeSeed(key => "user.local", definition);

            Assert.Single(seed);
            Assert.Equal("HomeAddress", seed[0].Key);
            Assert.Equal("admin.local", seed[0].Value);
        }

        [Fact]
        public void ComputeSeed_Enforces_The_Definition_Proxy_Posture_And_Allowlist()
        {
            var current = new Dictionary<string, string>
            {
                ["ProxyEnabled"] = "false",
                ["ProxyTargets"] = "http://user.example",
            };
            var definition = new Dictionary<string, string>
            {
                ["ProxyEnabled"] = "true",
                ["ProxyTargets"] = "http://admin.example",
            };

            var seed = WorkspaceInstanceProperties.ComputeSeed(key => current.TryGetValue(key, out var value) ? value : null, definition);

            Assert.Equal(2, seed.Count);
            Assert.Contains(seed, pair => pair.Key == "ProxyEnabled" && pair.Value == "true");
            Assert.Contains(seed, pair => pair.Key == "ProxyTargets" && pair.Value == "http://admin.example");
        }

        [Fact]
        public void ComputeSeed_Keeps_A_Per_Instance_Cosmetic_Override_Inside_An_Unchanged_Definition()
        {
            // Unlocked controls save per instance: a stored cosmetic value differing from the
            // definition is NOT reverted while the definition itself is unchanged.
            var current = new Dictionary<string, string> { ["ZoomLevel"] = "200" };
            var definition = new Dictionary<string, string>
            {
                ["ZoomLevel"] = "150",
                ["HideToolbar"] = "true",
            };

            var seed = WorkspaceInstanceProperties.ComputeSeed(key => current.TryGetValue(key, out var value) ? value : null, definition);

            // ZoomLevel stays the user's; HideToolbar had no stored value so the definition seeds it.
            Assert.Single(seed);
            Assert.Equal("HideToolbar", seed[0].Key);
        }

        [Fact]
        public void ComputeSeed_Reinitialises_Cosmetic_Keys_When_The_Definition_Changed()
        {
            var definition = new Dictionary<string, string> { ["ZoomLevel"] = "150" };

            var seed = WorkspaceInstanceProperties.ComputeSeed(key => "200", definition, definitionChanged: true);

            Assert.Single(seed);
            Assert.Equal("150", seed[0].Value);
        }

        [Fact]
        public void ComputeSeed_Always_Enforces_Identity_And_Fingerprint_Keys()
        {
            var definition = new Dictionary<string, string>
            {
                ["WorkspaceId"] = "ws-new",
                ["DefinitionFingerprint"] = "fp-new",
            };

            var seed = WorkspaceInstanceProperties.ComputeSeed(key => "stale", definition);

            Assert.Equal(2, seed.Count);
            Assert.Contains(seed, pair => pair.Key == "WorkspaceId" && pair.Value == "ws-new");
            Assert.Contains(seed, pair => pair.Key == "DefinitionFingerprint" && pair.Value == "fp-new");
        }
    }
}
