using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace WebContent.Client
{
    /// <summary>
    /// Pure bookkeeping for browser instances and their reference counts. Decoupled from
    /// WebView2/VideoOS so it can be unit-tested and so the shared registry behind
    /// <see cref="BrowserManager"/> can be kept bounded over long sessions.
    ///
    /// Semantics:
    /// - A browser stays tracked once created, even after its last reference is released
    ///   ("background" retention), so a subsequently reopened view item reuses the same
    ///   browser instead of creating a fresh one.
    /// - <see cref="Reclaim"/> permanently removes a browser. If the browser is still
    ///   referenced it is deferred until the last reference is released; re-referencing
    ///   the browser cancels the deferred reclaim.
    /// - <see cref="RemoveTracking"/> drops the tracking entry without invoking the
    ///   reclaim callback (the caller is expected to have already closed the browser).
    /// </summary>
    public class BrowserRegistry<T> where T : class
    {
        private readonly ConcurrentDictionary<Guid, T> _instances = new ConcurrentDictionary<Guid, T>();
        private readonly ConcurrentDictionary<Guid, int> _references = new ConcurrentDictionary<Guid, int>();
        private readonly ConcurrentDictionary<Guid, byte> _reclaimPending = new ConcurrentDictionary<Guid, byte>();

        public int Count => _instances.Count;

        public bool IsTracked(Guid id)
        {
            return _instances.ContainsKey(id);
        }

        public bool TryGet(Guid id, out T instance)
        {
            return _instances.TryGetValue(id, out instance);
        }

        /// <summary>Copy of the currently tracked instances (teardown sweeps, e.g. stopping proxy listeners).</summary>
        public List<T> Snapshot()
        {
            return new List<T>(_instances.Values);
        }

        public T GetOrCreate(Guid id, Func<Guid, T> factory)
        {
            if (_instances.TryGetValue(id, out var existing))
            {
                return existing;
            }

            var instance = factory(id);
            if (_instances.TryAdd(id, instance))
            {
                return instance;
            }

            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }

            return _instances[id];
        }

        public int AddReference(Guid id)
        {
            _reclaimPending.TryRemove(id, out _);
            return _references.AddOrUpdate(id, 1, (key, count) => count + 1);
        }

        public int GetReferenceCount(Guid id)
        {
            return _references.TryGetValue(id, out var count) ? count : 0;
        }

        /// <summary>
        /// Releases one reference to the browser. When the count reaches zero the entry
        /// is kept for background reuse unless a <see cref="Reclaim"/> is pending, in
        /// which case the browser is reclaimed immediately.
        /// </summary>
        public int ReleaseReference(Guid id, Action<T> onReclaim)
        {
            while (true)
            {
                if (!_references.TryGetValue(id, out var count))
                {
                    return 0;
                }

                if (count <= 1)
                {
                    if (!_references.TryRemove(id, out _))
                    {
                        continue;
                    }

                    if (_reclaimPending.TryRemove(id, out _))
                    {
                        ReclaimNow(id, onReclaim);
                    }

                    return 0;
                }

                if (_references.TryUpdate(id, count - 1, count))
                {
                    return count - 1;
                }
            }
        }

        /// <summary>
        /// Permanently removes a browser from tracking. If the browser is still referenced,
        /// removal is deferred until the last reference is released.
        /// </summary>
        public void Reclaim(Guid id, Action<T> onReclaim)
        {
            if (GetReferenceCount(id) > 0)
            {
                _reclaimPending.TryAdd(id, 0);
                return;
            }

            ReclaimNow(id, onReclaim);
        }

        public void RemoveTracking(Guid id)
        {
            _instances.TryRemove(id, out _);
            _references.TryRemove(id, out _);
            _reclaimPending.TryRemove(id, out _);
        }

        private void ReclaimNow(Guid id, Action<T> onReclaim)
        {
            _reclaimPending.TryRemove(id, out _);
            if (_instances.TryRemove(id, out var instance))
            {
                _references.TryRemove(id, out _);
                onReclaim?.Invoke(instance);
            }
        }
    }
}
