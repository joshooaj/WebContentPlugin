using System;
using System.Linq;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #69: the Smart Client side session bookkeeping (client half of the session
    /// lifecycle). Pure registry: it owns no messaging, it just answers who is live and which
    /// heartbeat payloads are owed this tick.
    /// </summary>
    public class ProxySessionRegistryTests
    {
        private static ProxyViewSessionContext Context(string workspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001")
        {
            return new ProxyViewSessionContext
            {
                SessionId = Guid.NewGuid().ToString("N"),
                WorkspaceItemId = workspaceItemId,
                WorkspaceFingerprint = "fp-1",
                ClientMachine = "BEAST",
            };
        }

        [Fact]
        public void HeartbeatPayloadsCoverEveryLiveSessionOnceAndSkipNothing()
        {
            var registry = new ProxySessionRegistry();
            var first = Context();
            var second = Context("bbbbbbbb-0000-0000-0000-000000000002");
            registry.Open(first);
            registry.Open(second);

            var payloads = registry.HeartbeatPayloads(DateTime.UtcNow);
            Assert.Equal(2, payloads.Count);
            Assert.All(payloads, payload => Assert.Equal(ProxySessionEventKinds.Heartbeat, payload.Kind));
            Assert.Contains(payloads, payload => payload.SessionId == first.SessionId && payload.WorkspaceItemId == first.WorkspaceItemId);
            Assert.Contains(payloads, payload => payload.SessionId == second.SessionId);

            registry.Close(first.SessionId);
            Assert.Single(registry.HeartbeatPayloads(DateTime.UtcNow));
        }

        [Fact]
        public void OpenIgnoresContextlessListenersAndCloseIsIdempotent()
        {
            var registry = new ProxySessionRegistry();
            registry.Open(null);
            registry.Open(new ProxyViewSessionContext());
            Assert.Equal(0, registry.Count);

            var session = Context();
            registry.Open(session);
            Assert.Equal(1, registry.Count);
            registry.Close(session.SessionId);
            registry.Close(session.SessionId);
            Assert.Equal(0, registry.Count);

            registry.Open(session);
            registry.CloseAll();
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void WorkspaceIdentityGateRejectsMissingAndUnparsableIds()
        {
            Assert.True(new ProxyViewSessionContext { WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001" }.HasWorkspaceIdentity);
            Assert.False(new ProxyViewSessionContext { WorkspaceItemId = "" }.HasWorkspaceIdentity);
            Assert.False(new ProxyViewSessionContext { WorkspaceItemId = null }.HasWorkspaceIdentity);
            Assert.False(new ProxyViewSessionContext { WorkspaceItemId = "00000000-0000-0000-0000-000000000000" }.HasWorkspaceIdentity);
            Assert.False(new ProxyViewSessionContext { WorkspaceItemId = "not-a-guid" }.HasWorkspaceIdentity);
        }

        [Fact]
        public void SnapshotContextsMirrorTheLiveTable()
        {
            var registry = new ProxySessionRegistry();
            var session = Context();
            registry.Open(session);
            Assert.Contains(registry.SnapshotContexts(), context => context.SessionId == session.SessionId);
            Assert.Equal(session, registry.SnapshotContexts().Single());
        }
    }
}
