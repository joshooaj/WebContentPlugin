using System.Collections.Generic;
using Xunit;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the definition fingerprint embedded in provisioned view-item properties. It
    /// changes whenever the admin edits any part of the workspace definition, which lets the
    /// Smart Client re-provision a persisted view item instead of keeping stale settings.
    /// </summary>
    public class WorkspaceDefinitionFingerprintTests
    {
        [Fact]
        public void Same_Definition_Produces_Same_Fingerprint()
        {
            var properties = new Dictionary<string, string>
            {
                ["HomeAddress"] = "monitor.local",
                ["ZoomLevel"] = "150"
            };

            var first = WorkspaceDefinitionFingerprint.Compute("Ops", properties);
            var second = WorkspaceDefinitionFingerprint.Compute("Ops", properties);

            Assert.Equal(first, second);
            Assert.NotEmpty(first);
        }

        [Fact]
        public void Property_Order_Does_Not_Affect_Fingerprint()
        {
            var one = new Dictionary<string, string>
            {
                ["HomeAddress"] = "monitor.local",
                ["ZoomLevel"] = "150"
            };
            var other = new Dictionary<string, string>
            {
                ["ZoomLevel"] = "150",
                ["HomeAddress"] = "monitor.local"
            };

            Assert.Equal(
                WorkspaceDefinitionFingerprint.Compute("Ops", one),
                WorkspaceDefinitionFingerprint.Compute("Ops", other));
        }

        [Fact]
        public void Edited_HomeAddress_Changes_Fingerprint()
        {
            var before = new Dictionary<string, string> { ["HomeAddress"] = "old.local" };
            var after = new Dictionary<string, string> { ["HomeAddress"] = "new.local" };

            Assert.NotEqual(
                WorkspaceDefinitionFingerprint.Compute("Ops", before),
                WorkspaceDefinitionFingerprint.Compute("Ops", after));
        }

        [Fact]
        public void Renamed_Workspace_Changes_Fingerprint()
        {
            var properties = new Dictionary<string, string> { ["HomeAddress"] = "monitor.local" };

            Assert.NotEqual(
                WorkspaceDefinitionFingerprint.Compute("Old Name", properties),
                WorkspaceDefinitionFingerprint.Compute("New Name", properties));
        }

        [Fact]
        public void Null_Inputs_Produce_A_Stable_Fingerprint()
        {
            var first = WorkspaceDefinitionFingerprint.Compute(null, null);
            var second = WorkspaceDefinitionFingerprint.Compute(null, null);

            Assert.Equal(first, second);
            Assert.NotEmpty(first);
        }
    }
}
