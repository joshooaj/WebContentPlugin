using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #71: the decision table for which persisted view-item settings an admin-defined
    /// (managed) workspace governs. Locked (security-relevant) keys can never be overridden per
    /// instance; cosmetic/navigation keys stay per-instance editable; standalone (manually
    /// placed) view items are never managed and nothing is locked for them.
    /// </summary>
    public class WorkspaceSettingLocksTests
    {
        [Fact]
        public void Standalone_Item_With_No_Workspace_Identity_Is_Not_Managed()
        {
            Assert.False(WorkspaceSettingLocks.IsManagedWorkspace(string.Empty, string.Empty));
            Assert.False(WorkspaceSettingLocks.IsManagedWorkspace(null, null));
            Assert.False(WorkspaceSettingLocks.IsManagedWorkspace("   ", "  "));
        }

        [Fact]
        public void Persisted_Workspace_Identity_Marks_The_Item_Managed()
        {
            Assert.True(WorkspaceSettingLocks.IsManagedWorkspace("6c8d4038-4f24-4577-a04c-2d5493805f43", string.Empty));
            Assert.True(WorkspaceSettingLocks.IsManagedWorkspace(string.Empty, "af4c1e2d"));
            Assert.True(WorkspaceSettingLocks.IsManagedWorkspace("6c8d4038-4f24-4577-a04c-2d5493805f43", "af4c1e2d"));
        }

        [Theory]
        [InlineData("HomeAddress")]
        [InlineData("Address")]
        [InlineData("ProxyEnabled")]
        [InlineData("ProxyTargets")]
        [InlineData("homeaddress")]
        [InlineData("PROXYTARGETS")]
        public void Security_Relevant_Keys_Are_Locked(string key)
        {
            Assert.True(WorkspaceSettingLocks.IsLockedKey(key));
        }

        [Theory]
        [InlineData("HideToolbar")]
        [InlineData("HideAddress")]
        [InlineData("ZoomLevel")]
        [InlineData("RememberLastAddress")]
        [InlineData("ClearBrowsingDataOnExit")]
        [InlineData("MuteAudio")]
        [InlineData("AllowAudioNotifications")]
        [InlineData("AllowBackgroundBrowser")]
        [InlineData("IsolateBrowserInstances")]
        [InlineData("EnableDevTools")]
        [InlineData("LastAddress")]
        public void Cosmetic_And_Navigation_Keys_Are_Not_Locked(string key)
        {
            Assert.False(WorkspaceSettingLocks.IsLockedKey(key));
        }

        [Fact]
        public void Null_Key_Is_Neither_Locked_Nor_Identity()
        {
            Assert.False(WorkspaceSettingLocks.IsLockedKey(null));
            Assert.False(WorkspaceSettingLocks.IsIdentityKey(null));
        }

        [Theory]
        [InlineData("WorkspaceId")]
        [InlineData("WorkspaceName")]
        [InlineData("DefinitionFingerprint")]
        [InlineData("InstanceId")]
        public void Identity_And_Sync_Keys_Are_Identity_Keys(string key)
        {
            Assert.True(WorkspaceSettingLocks.IsIdentityKey(key));
            // Identity keys are per-item identity, not admin-governed settings and not cosmetic
            // per-instance choices; they are never in the locked or unlocked setting sets.
            Assert.False(WorkspaceSettingLocks.IsLockedKey(key));
        }

        [Fact]
        public void Equal_Values_Never_Need_A_Write()
        {
            Assert.False(WorkspaceSettingLocks.ShouldWriteSeedValue("HomeAddress", "same", "same", false));
            Assert.False(WorkspaceSettingLocks.ShouldWriteSeedValue("ZoomLevel", "150", "150", true));
            Assert.False(WorkspaceSettingLocks.ShouldWriteSeedValue("DefinitionFingerprint", "fp", "fp", false));
        }

        [Fact]
        public void Locked_Keys_Are_Always_Enforced_Against_Per_Instance_Overrides()
        {
            // A stored per-instance override (older build or hand-edited) of a locked key is
            // overwritten no matter what else is true.
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("HomeAddress", "user.local", "admin.local", false));
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("ProxyEnabled", "false", "true", false));
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("ProxyTargets", "http://user.example", "http://admin.example", false));
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("Address", "user.local", "admin.local", true));
        }

        [Fact]
        public void Identity_Keys_Are_Always_Enforced()
        {
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("DefinitionFingerprint", "old-fp", "new-fp", false));
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("WorkspaceId", "ws-a", "ws-b", false));
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("InstanceId", "id-a", "id-b", true));
        }

        [Fact]
        public void Cosmetic_Keys_Get_The_Definition_As_Their_Initial_Value()
        {
            // No persisted value at all (brand-new or re-provisioned view item) -> seed supplies it.
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("ZoomLevel", null, "150", false));
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("HideToolbar", string.Empty, "true", false));
        }

        [Fact]
        public void Cosmetic_Per_Instance_Override_Survives_A_Seed_Inside_An_Unchanged_Definition()
        {
            Assert.False(WorkspaceSettingLocks.ShouldWriteSeedValue("ZoomLevel", "200", "150", false));
            Assert.False(WorkspaceSettingLocks.ShouldWriteSeedValue("ClearBrowsingDataOnExit", "true", "false", false));
            Assert.False(WorkspaceSettingLocks.ShouldWriteSeedValue("EnableDevTools", "false", "true", false));
        }

        [Fact]
        public void A_Changed_Admin_Definition_Reinitialises_Cosmetic_Keys()
        {
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("ZoomLevel", "200", "150", true));
            Assert.True(WorkspaceSettingLocks.ShouldWriteSeedValue("HideAddress", "true", "false", true));
        }
    }
}
