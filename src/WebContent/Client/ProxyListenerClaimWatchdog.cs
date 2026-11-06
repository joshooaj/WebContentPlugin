using System;
using System.Threading;

namespace WebContent.Client
{
    /// <summary>
    /// #96: the once-per-client heartbeat that makes owner death HEALABLE without user action.
    /// While any proxied browser exists this process re-reads the boundary's listener claim on a
    /// bounded cadence and lets the pure <see cref="ClaimWatchCore"/> decide what (if anything)
    /// <see cref="BrowserManager"/> must do - re-claim through the untouched #93 protocol, re-
    /// publish a vanished own claim, or re-create the boundary's WebViews against the new
    /// generation. Detection NEVER waits on a user gesture: the browser process survives the
    /// owner host (#90) while its baked-in loopback port dies with it, and only a watch like this
    /// notices before the next page load does.
    ///
    /// The timer lives here (plugin-only, like the #94 sweep timer); every tick is guarded end to
    /// end - a throwing watch is journalled and skipped, never fatal, and simply retried next
    /// tick. The cadence and grace constants bound the protocol's footprint: one small file read
    /// plus at most two cheap liveness probes per proxied browser per tick.
    /// </summary>
    internal static class ProxyListenerClaimWatchdog
    {
        /// <summary>First consult lands after browser creation has settled; then a steady heartbeat.</summary>
        private static readonly TimeSpan DueTime = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan Period = TimeSpan.FromSeconds(5);

        private static readonly object _gate = new object();

        private static Timer _timer;

        /// <summary>Idempotent: the first proxied browser creation/re-enable arms the watch.</summary>
        internal static void EnsureStarted()
        {
            lock (_gate)
            {
                if (_timer != null)
                {
                    return;
                }

                _timer = new Timer(OnTick, null, DueTime, Period);
            }
        }

        /// <summary>Plugin teardown: the watch stops with the listeners it watches over.</summary>
        internal static void Stop()
        {
            lock (_gate)
            {
                var timer = _timer;
                _timer = null;
                if (timer != null)
                {
                    timer.Dispose();
                }
            }
        }

        private static void OnTick(object state)
        {
            try
            {
                BrowserManager.WatchBoundaryClaims();
            }
            catch (Exception)
            {
                // The timer must never die and never escalate: a missed tick just means the
                // handoff is noticed one tick later.
            }
        }
    }
}
