using System;
using Xunit;
using WebContent.Client;

namespace WebContent.Tests
{
    /// <summary>
    /// Tests for the pure browser tracking registry behind <see cref="BrowserManager"/>.
    /// These encode the ticket #15 invariants: browser tracking stays bounded across
    /// repeated open/close/remove cycles, background (unreferenced) browsers stay
    /// reusable until their owning view item is permanently reclaimed, and a reclaim
    /// issued while a browser is still referenced is deferred until the last reference
    /// is released (and is cancelled if the browser is re-referenced first).
    /// </summary>
    public class BrowserRegistryTests
    {
        private sealed class FakeBrowser : IDisposable
        {
            public int DisposeCount;
            public FakeBrowser(Guid id) { Id = id; }
            public Guid Id { get; }
            public void Dispose() { DisposeCount++; }
        }

        private static BrowserRegistry<FakeBrowser> CreateRegistry()
        {
            return new BrowserRegistry<FakeBrowser>();
        }

        private static FakeBrowser Create(Guid id, BrowserRegistry<FakeBrowser> registry)
        {
            return registry.GetOrCreate(id, guid => new FakeBrowser(guid));
        }

        [Fact]
        public void GetOrCreate_Returns_Same_Instance_For_Same_Id()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();

            var first = Create(id, registry);
            var second = Create(id, registry);

            Assert.Same(first, second);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void GetOrCreate_Returns_Distinct_Instances_For_Distinct_Ids()
        {
            var registry = CreateRegistry();

            var first = Create(Guid.NewGuid(), registry);
            var second = Create(Guid.NewGuid(), registry);

            Assert.NotSame(first, second);
            Assert.Equal(2, registry.Count);
        }

        [Fact]
        public void AddReference_Increments_And_Returns_Count()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();
            Create(id, registry);

            Assert.Equal(1, registry.AddReference(id));
            Assert.Equal(2, registry.AddReference(id));
            Assert.Equal(2, registry.GetReferenceCount(id));
        }

        [Fact]
        public void ReleaseReference_Keeps_Browser_Tracked_On_Last_Release()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();
            Create(id, registry);
            registry.AddReference(id);

            var remaining = registry.ReleaseReference(id, browser => browser.Dispose());

            Assert.Equal(0, remaining);
            Assert.True(registry.IsTracked(id));
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void ReleaseReference_When_Never_Referenced_Returns_Zero()
        {
            var registry = CreateRegistry();
            Assert.Equal(0, registry.ReleaseReference(Guid.NewGuid(), browser => browser.Dispose()));
        }

        [Fact]
        public void Reclaim_On_Unreferenced_Browser_Removes_And_Disposes()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();
            var browser = Create(id, registry);
            registry.AddReference(id);
            registry.ReleaseReference(id, b => b.Dispose());

            registry.Reclaim(id, b => b.Dispose());

            Assert.Equal(1, browser.DisposeCount);
            Assert.False(registry.IsTracked(id));
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void Reclaim_On_Referenced_Browser_Defers_Until_Last_Release()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();
            var browser = Create(id, registry);
            registry.AddReference(id);
            registry.AddReference(id);

            registry.Reclaim(id, b => b.Dispose());

            Assert.True(registry.IsTracked(id));
            Assert.Equal(0, browser.DisposeCount);

            Assert.Equal(1, registry.ReleaseReference(id, b => b.Dispose()));
            Assert.Equal(0, browser.DisposeCount);
            Assert.True(registry.IsTracked(id));

            Assert.Equal(0, registry.ReleaseReference(id, b => b.Dispose()));
            Assert.Equal(1, browser.DisposeCount);
            Assert.False(registry.IsTracked(id));
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void Re_Referencing_After_Pending_Reclaim_Cancels_The_Reclaim()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();
            var browser = Create(id, registry);
            registry.AddReference(id);

            registry.Reclaim(id, b => b.Dispose());
            Assert.Equal(0, browser.DisposeCount);

            registry.AddReference(id);
            Assert.Equal(1, registry.ReleaseReference(id, b => b.Dispose()));
            Assert.Equal(0, browser.DisposeCount);
            Assert.True(registry.IsTracked(id));

            Assert.Equal(0, registry.ReleaseReference(id, b => b.Dispose()));
            Assert.Equal(0, browser.DisposeCount);
            Assert.True(registry.IsTracked(id));
        }

        [Fact]
        public void Reclaim_On_Untracked_Id_Is_A_NoOp()
        {
            var registry = CreateRegistry();
            var called = false;

            registry.Reclaim(Guid.NewGuid(), browser => called = true);

            Assert.False(called);
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void RemoveTracking_Removes_Without_Invoking_Reclaim_Callback()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();
            var browser = Create(id, registry);
            registry.AddReference(id);
            var called = false;

            registry.RemoveTracking(id);

            Assert.Equal(0, browser.DisposeCount);
            Assert.False(called);
            Assert.False(registry.IsTracked(id));
            Assert.Equal(0, registry.GetReferenceCount(id));
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void Repeated_Create_Release_Reclaim_Cycles_Keep_Tracking_Bounded()
        {
            var registry = CreateRegistry();

            for (var i = 0; i < 100; i++)
            {
                var id = Guid.NewGuid();
                var browser = Create(id, registry);
                registry.AddReference(id);
                registry.ReleaseReference(id, b => b.Dispose());
                registry.Reclaim(id, b => b.Dispose());
            }

            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void Unreferenced_Background_Browser_Is_Reused_Until_Reclaimed()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();

            var first = Create(id, registry);
            registry.AddReference(id);
            registry.ReleaseReference(id, b => b.Dispose());

            var second = Create(id, registry);
            registry.AddReference(id);
            registry.ReleaseReference(id, b => b.Dispose());

            Assert.Same(first, second);
            Assert.Equal(1, registry.Count);

            registry.Reclaim(id, b => b.Dispose());
            Assert.Equal(0, registry.Count);
            Assert.False(registry.IsTracked(id));
        }

        [Fact]
        public void TryGet_Returns_Tracked_Instance_And_False_For_Unknown()
        {
            var registry = CreateRegistry();
            var id = Guid.NewGuid();
            var browser = Create(id, registry);

            Assert.True(registry.TryGet(id, out var resolved));
            Assert.Same(browser, resolved);

            Assert.False(registry.TryGet(Guid.NewGuid(), out _));
        }
    }
}
