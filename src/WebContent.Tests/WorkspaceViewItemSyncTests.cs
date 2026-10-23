using System;
using System.Collections.Generic;
using Xunit;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the reconciliation decision used by <c>WebContentWorkSpacePlugin</c> when it
    /// finds a persisted view item at position 0 of its layout item. Every WebContent tab shares
    /// one view item plugin id, so presence alone cannot prove the persisted view item belongs to
    /// this workspace definition; a stale item makes the tab name and the rendered content drift
    /// apart. The decision is pure so it can be tested without the VideoOS SDK.
    /// </summary>
    public class WorkspaceViewItemSyncTests
    {
        private static readonly Guid WorkspaceId = Guid.NewGuid();

        private static Dictionary<string, string> Persisted(Guid workspaceId, string fingerprint)
        {
            return new Dictionary<string, string>
            {
                [WorkspaceViewItemSync.WorkspaceIdKey] = workspaceId.ToString(),
                [WorkspaceViewItemSync.FingerprintKey] = fingerprint
            };
        }

        [Fact]
        public void Missing_Persisted_Properties_Need_Reprovision()
        {
            Assert.False(WorkspaceViewItemSync.IsCurrent(null, WorkspaceId, "abc"));
        }

        [Fact]
        public void Persisted_Properties_Without_Identity_Need_Reprovision()
        {
            Assert.False(WorkspaceViewItemSync.IsCurrent(new Dictionary<string, string>(), WorkspaceId, "abc"));
        }

        [Fact]
        public void Matching_WorkspaceId_And_Fingerprint_Is_Current()
        {
            var persisted = Persisted(WorkspaceId, "abc");

            Assert.True(WorkspaceViewItemSync.IsCurrent(persisted, WorkspaceId, "abc"));
        }

        [Fact]
        public void View_Item_Belonging_To_Another_Workspace_Needs_Reprovision()
        {
            var persisted = Persisted(Guid.NewGuid(), "abc");

            Assert.False(WorkspaceViewItemSync.IsCurrent(persisted, WorkspaceId, "abc"));
        }

        [Fact]
        public void Stale_Definition_Fingerprint_Needs_Reprovision()
        {
            var persisted = Persisted(WorkspaceId, "old-fingerprint");

            Assert.False(WorkspaceViewItemSync.IsCurrent(persisted, WorkspaceId, "new-fingerprint"));
        }

        [Fact]
        public void Missing_Definition_Fingerprint_Needs_Reprovision()
        {
            var persisted = new Dictionary<string, string>
            {
                [WorkspaceViewItemSync.WorkspaceIdKey] = WorkspaceId.ToString()
            };

            Assert.False(WorkspaceViewItemSync.IsCurrent(persisted, WorkspaceId, "abc"));
        }

        [Fact]
        public void Unparsable_WorkspaceId_Needs_Reprovision()
        {
            var persisted = new Dictionary<string, string>
            {
                [WorkspaceViewItemSync.WorkspaceIdKey] = "not-a-guid",
                [WorkspaceViewItemSync.FingerprintKey] = "abc"
            };

            Assert.False(WorkspaceViewItemSync.IsCurrent(persisted, WorkspaceId, "abc"));
        }

        [Fact]
        public void Keys_Are_Matched_Case_Insensitively()
        {
            var persisted = new Dictionary<string, string>
            {
                ["workspaceid"] = WorkspaceId.ToString(),
                ["definitionfingerprint"] = "abc"
            };

            Assert.True(WorkspaceViewItemSync.IsCurrent(persisted, WorkspaceId, "abc"));
        }
    }
}
