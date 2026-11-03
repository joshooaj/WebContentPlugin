using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #57: the client-side tunnel correlator is the pure registry between the messaging
    /// receive thread and the browser connection pumps — open acks, ordered data queues, close
    /// notices, open deadlines, and idle reclaim. Messaging itself (ProxyFetchClient) is thin
    /// glue over this; everything that can go wrong about ordering and lifetime lives here.
    /// </summary>
    public class ProxyTunnelCorrelationTests
    {
        private static ProxyTunnelPayload OpenAck(string id, bool success)
        {
            return new ProxyTunnelPayload { CorrelationId = id, Success = success, StatusCode = success ? 101 : 0 };
        }

        // ---- registration and open ----

        [Fact]
        public async Task Register_then_await_open_completes_with_the_ack_outcome()
        {
            var registry = new ProxyTunnelCorrelation();
            Assert.True(registry.TryRegister("t1"));

            var waiting = registry.AwaitOpenAsync("t1");
            Assert.False(waiting.IsCompleted);

            registry.CompleteOpen("t1", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });

            var outcome = await waiting;
            Assert.True(outcome.Success);
            Assert.Equal(101, outcome.StatusCode);
        }

        [Fact]
        public void Register_refuses_a_duplicate_id()
        {
            var registry = new ProxyTunnelCorrelation();
            Assert.True(registry.TryRegister("t1"));
            Assert.False(registry.TryRegister("t1"));
        }

        [Fact]
        public void Open_ack_for_an_unknown_id_closes_the_handle_it_came_with()
        {
            // The client abandoned the open (deadline) while the Event Server was still
            // connecting: the late ack must not orphan a live target socket.
            var registry = new ProxyTunnelCorrelation();
            var handle = new FakeHandle();

            registry.CompleteOpen("gone", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101, Handle = handle });

            Assert.True(handle.ClosedLocally);
        }

        [Fact]
        public async Task Await_open_on_an_unknown_id_returns_null()
        {
            var registry = new ProxyTunnelCorrelation();
            Assert.Null(await registry.AwaitOpenAsync("nope"));
        }

        // ---- data ordering ----

        [Fact]
        public async Task Target_data_queues_in_order_before_the_pump_attaches()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("t1");
            registry.CompleteOpen("t1", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });

            registry.OnTargetData("t1", new byte[] { 1, 2 });
            registry.OnTargetData("t1", new byte[] { 3 });

            Assert.Equal(new byte[] { 1, 2 }, await registry.NextDataAsync("t1"));
            Assert.Equal(new byte[] { 3 }, await registry.NextDataAsync("t1"));
        }

        [Fact]
        public async Task A_waiting_pump_gets_the_next_bytes_immediately()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("t1");
            registry.CompleteOpen("t1", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });

            var waiting = registry.NextDataAsync("t1");
            Assert.False(waiting.IsCompleted);

            registry.OnTargetData("t1", new byte[] { 9 });

            Assert.Equal(new byte[] { 9 }, await waiting);
        }

        [Fact]
        public void Data_for_an_unknown_tunnel_is_ignored()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.OnTargetData("gone", new byte[] { 1 }); // must not throw
        }

        // ---- close paths ----

        [Fact]
        public async Task Target_close_ends_the_pump_with_null()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("t1");
            registry.CompleteOpen("t1", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });

            var waiting = registry.NextDataAsync("t1");
            registry.OnTargetClose("t1", "target went away");

            Assert.Null(await waiting);
            Assert.Null(await registry.NextDataAsync("t1"));
            Assert.True(registry.IsClosed("t1"));
        }

        [Fact]
        public void Local_end_closes_the_session_and_later_data_is_ignored()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("t1");
            registry.CompleteOpen("t1", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });

            Assert.True(registry.EndLocal("t1"));
            registry.OnTargetData("t1", new byte[] { 1 }); // after local end: dropped
            Assert.True(registry.IsClosed("t1"));

            // The id is released, not resurrected: EndLocal removes it from the registry, so
            // late frames are unknown-id traffic, and the same id can be reused.
            Assert.True(registry.TryRegister("t1"));
        }

        [Fact]
        public async Task Open_failure_completes_the_waiter_with_the_failure()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("t1");

            var waiting = registry.AwaitOpenAsync("t1");
            registry.CompleteOpen("t1", new ProxyTunnelOpenOutcome { Success = false, ErrorCode = "HandshakeRefused", ErrorMessage = "401" });

            var outcome = await waiting;
            Assert.NotNull(outcome);
            Assert.False(outcome.Success);
            Assert.Equal("HandshakeRefused", outcome.ErrorCode);
            Assert.True(registry.IsClosed("t1"));
        }

        // ---- lifetime sweeps ----

        [Fact]
        public void Reclaim_idle_closes_only_the_stalled_sessions()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("old");
            registry.CompleteOpen("old", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });
            registry.TryRegister("fresh");
            registry.CompleteOpen("fresh", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });

            // Backdate the old session's activity past the idle bound.
            registry.DebugSetLastActivity("old", DateTime.UtcNow - TimeSpan.FromMinutes(10));

            var reclaimed = registry.ReclaimIdle(TimeSpan.FromMinutes(5));

            Assert.Contains("old", reclaimed);
            Assert.DoesNotContain("fresh", reclaimed);
            Assert.True(registry.IsClosed("old"));
            Assert.False(registry.IsClosed("fresh"));
        }

        [Fact]
        public void Any_traffic_moves_the_idle_deadline()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("t1");
            registry.CompleteOpen("t1", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });
            registry.DebugSetLastActivity("t1", DateTime.UtcNow - TimeSpan.FromMinutes(10));

            registry.OnTargetData("t1", new byte[] { 1 });

            var reclaimed = registry.ReclaimIdle(TimeSpan.FromMinutes(5));
            Assert.Empty(reclaimed);
        }

        [Fact]
        public void Close_all_empties_the_registry()
        {
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("a");
            registry.CompleteOpen("a", new ProxyTunnelOpenOutcome { Success = true, StatusCode = 101 });
            registry.TryRegister("b");

            registry.CloseAll("client shut down");

            Assert.Equal(0, registry.Count);
            Assert.True(registry.IsClosed("a"));
        }

        [Fact]
        public void Half_open_tunnels_are_reclaimed_on_the_open_deadline()
        {
            // Registered, never acked: the half-open case the acceptance criteria call out.
            var registry = new ProxyTunnelCorrelation();
            registry.TryRegister("half");
            registry.DebugSetLastActivity("half", DateTime.UtcNow - TimeSpan.FromMinutes(10));

            var reclaimed = registry.ReclaimIdle(TimeSpan.FromMinutes(5));

            Assert.Contains("half", reclaimed);
            Assert.True(registry.IsClosed("half"));
        }

        private sealed class FakeHandle : ProxyTunnelHandle
        {
            internal bool ClosedLocally;

            internal override bool IsClosed => ClosedLocally;

            internal override Task SendAsync(byte[] bytes) => Task.CompletedTask;

            internal override Task<byte[]> NextDataAsync() => Task.FromResult<byte[]>(null);

            internal override void Close(string reason)
            {
                ClosedLocally = true;
            }
        }
    }
}
