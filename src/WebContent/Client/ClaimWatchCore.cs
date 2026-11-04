using System;

namespace WebContent.Client
{
    internal enum ClaimWatchAction
    {
        /// <summary>The browser's claim posture matches the live slot and its view is up - nothing to do.</summary>
        None,

        /// <summary>
        /// #96 handoff: the joined/owned posture no longer matches the boundary's live claim (the
        /// owner died, the port refused, the epoch changed, the slot vanished or went corrupt).
        /// The caller re-consults the lease and re-creates the WebView when the arguments change -
        /// a live browser process cannot adopt a new --proxy-server port any other way.
        /// </summary>
        Rewire,

        /// <summary>
        /// #96: THIS process was the slot's owner but its record vanished (or names a port other
        /// than the one it serves). Re-publish the identical claim through the protocol - the
        /// listener, the arguments, and every joiner's posture stay untouched.
        /// </summary>
        Republish,

        /// <summary>
        /// #96: the live claim matches this browser's arguments but the WebView never attached
        /// (the recycled browser process is still winding down or respawning). Attempt creation
        /// again within the bounded retry budget.
        /// </summary>
        Retry,

        /// <summary>
        /// #96: the retry budget for this claim generation is spent - stop auto-re-creating and
        /// leave the honest #87 in-view notice (manual Refresh stays).
        /// </summary>
        GiveUp,
    }

    /// <summary>The watch facts <see cref="ClaimWatchCore.Decide"/> runs over - no IO, no WPF.</summary>
    internal sealed class ClaimWatchFacts
    {
        /// <summary>This process's pid.</summary>
        internal int OwnPid { get; set; }

        /// <summary>True while this browser OWNS the slot (its claim path is tracked).</summary>
        internal bool OwnsClaimSlot { get; set; }

        /// <summary>The port this process's own loopback listener serves (0 when it has none).</summary>
        internal int OwnListenerPort { get; set; }

        /// <summary>The owner pid / port / epoch this browser recorded when it last owned or joined.</summary>
        internal int RecordedOwnerPid { get; set; }

        internal int RecordedPort { get; set; }

        internal long RecordedEpoch { get; set; }

        /// <summary>The WebView is initialized and attached (a live proxied view is up).</summary>
        internal bool ViewLive { get; set; }

        /// <summary>A failed initialization is standing in the view (the #87 notice) - re-creating it is the handoff remedy.</summary>
        internal bool InitFailurePending { get; set; }

        /// <summary>Clock seam (unix ms) so the rows are deterministic.</summary>
        internal long NowUnixMs { get; set; }

        /// <summary>When the last WebView creation attempt started (unix ms).</summary>
        internal long LastInitAttemptUnixMs { get; set; }

        /// <summary>Re-creation attempts already spent on this claim generation.</summary>
        internal int HandoffAttempts { get; set; }
    }

    internal sealed class ClaimWatchVerdict
    {
        internal ClaimWatchAction Action { get; private set; }

        /// <summary>One audit-worthy line naming the boundary reason; null for quiet Keeps.</summary>
        internal string Audit { get; private set; }

        internal ClaimWatchVerdict(ClaimWatchAction action, string audit = null)
        {
            Action = action;
            Audit = audit;
        }
    }

    /// <summary>
    /// #96: the PURE decision table of the boundary-claim watch - "does this proxied browser's
    /// recorded claim posture still describe the live boundary, and if not, what heals it?" The
    /// plugin's thin seam re-reads one claim file per proxied browser per tick and feeds the
    /// facts here; every action (re-consult the lease, re-publish, re-create the WebView) then
    /// runs through the EXISTING machinery: <see cref="ProxyListenerLeaseProvider"/>'s consult-
    /// before-bind gate and the #87 WebView re-creation route. There is deliberately no second
    /// recreation path and no claim side-door: stale takeovers ride the #93 protocol's own
    /// atomic create + audits.
    ///
    /// The liveness rule is the ADR-0004 staleness rule verbatim (dead owner pid OR refusing
    /// port), so a watch verdict that leads to a re-claim and a concurrent claimer converge on
    /// exactly one owner. Cycle termination: every handoff bump carries an epoch the previous
    /// generation never had, and the re-creation retry budget is bounded per generation
    /// (<see cref="MaxHandoffRetries"/> per generation, grace between attempts) - a survivor
    /// dying mid-handoff just starts the same bounded cycle again for the next generation.
    /// </summary>
    internal static class ClaimWatchCore
    {
        /// <summary>Automatic re-creation attempts per claim generation before the #87 notice stands.</summary>
        internal const int MaxHandoffRetries = 5;

        /// <summary>
        /// Minimum spacing between re-creation attempts: the WebView2 collection respawn measured
        /// ~13 s (#90) after the last attached host released it, so re-attempting sooner just
        /// collides with the still-winding-down browser process.
        /// </summary>
        internal const long ReCreationGraceMs = 15000;

        /// <param name="file">The raw slot read (parse happens here, over the #93 schema parse).</param>
        /// <param name="facts">This browser's recorded posture + liveness facts.</param>
        /// <param name="ownerProcessAlive">pid liveness seam (the #93 store's production probe in wiring).</param>
        /// <param name="loopbackPortConnectable">port liveness seam (the #93 store's production probe in wiring).</param>
        internal static ClaimWatchVerdict Decide(
            ListenerClaimFileRead file,
            ClaimWatchFacts facts,
            Func<int, bool> ownerProcessAlive,
            Func<int, bool> loopbackPortConnectable)
        {
            if (file == null || facts == null)
            {
                return new ClaimWatchVerdict(ClaimWatchAction.None);
            }

            // A read ERROR is transient IO, not stale content: wait for the next tick. Treating a
            // momentary read refusal as a corrupt slot would delete a live owner's claim - the
            // one thing the release discipline (#95) refuses to do too.
            if (file.Kind == ListenerClaimFileReadKind.Error)
            {
                return new ClaimWatchVerdict(ClaimWatchAction.None);
            }

            if (file.Kind == ListenerClaimFileReadKind.NotFound)
            {
                return facts.OwnsClaimSlot
                    ? new ClaimWatchVerdict(ClaimWatchAction.Republish,
                        "the boundary's claim slot vanished while this process was its owner and its listener still runs - re-publishing the claim for the port actually served")
                    : new ClaimWatchVerdict(ClaimWatchAction.Rewire,
                        "the boundary's claim slot is gone - the owner left the boundary (clean exit or a swept slot); re-consulting the claim to take the handoff");
            }

            var read = ListenerClaim.Read(file.Content);
            if (read.Kind == ListenerClaimReadKind.Untrusted)
            {
                return new ClaimWatchVerdict(ClaimWatchAction.Rewire,
                    "the boundary's claim slot no longer parses to this build's schema (" + read.Reason + ") - re-consulting the claim protocol");
            }

            var claim = read.Claim;

            if (claim.OwnerPid == facts.OwnPid)
            {
                if (!facts.OwnsClaimSlot)
                {
                    // The slot names US although this browser thinks it joined someone - another
                    // code path (or this process winning a race) made ours the boundary owner.
                    return new ClaimWatchVerdict(ClaimWatchAction.Rewire,
                        "the boundary's claim now names this process although this browser was joined - re-consulting to adopt the ownership posture");
                }

                if (claim.Port != facts.OwnListenerPort)
                {
                    return new ClaimWatchVerdict(ClaimWatchAction.Republish,
                        "the boundary's claim names port " + claim.Port + " but this process's listener serves " + facts.OwnListenerPort + " - re-publishing the claim for the port actually served");
                }

                // Own claim, own listener, exact match: healthy (or waiting out an in-flight
                // re-creation). Never probe our OWN port - the joiners' probes cover real death.
                return LiveOrRetry(facts);
            }

            // A foreign claim in the slot: the #93 staleness rule first...
            if (!ownerProcessAlive(claim.OwnerPid))
            {
                return new ClaimWatchVerdict(ClaimWatchAction.Rewire,
                    "the claim's owner process (pid " + claim.OwnerPid + ") is no longer running - a survivor takes the boundary's listener handoff through the claim protocol");
            }

            if (!loopbackPortConnectable(claim.Port))
            {
                return new ClaimWatchVerdict(ClaimWatchAction.Rewire,
                    "the claim's loopback port (" + claim.Port + ") refused a connect probe - re-consulting the claim protocol");
            }

            // ...then the posture agreement. An OWNERSHIP posture facing a live foreign owner was
            // taken over through the protocol (a stale verdict by another process) - step down.
            if (facts.OwnsClaimSlot)
            {
                return new ClaimWatchVerdict(ClaimWatchAction.Rewire,
                    "another live process (pid " + claim.OwnerPid + ") now owns the boundary's claim - stepping down to join it");
            }

            if (claim.OwnerPid != facts.RecordedOwnerPid || claim.Port != facts.RecordedPort || claim.Epoch != facts.RecordedEpoch)
            {
                return new ClaimWatchVerdict(ClaimWatchAction.Rewire,
                    "the boundary's claim changed hands while this browser was joined (owner " + facts.RecordedOwnerPid + " port " + facts.RecordedPort + " epoch " + facts.RecordedEpoch +
                    " -> owner " + claim.OwnerPid + " port " + claim.Port + " epoch " + claim.Epoch + ") - re-consulting to join the new generation");
            }

            // The claim still describes exactly what this browser carries: healthy, or an
            // in-flight/failed re-creation of the SAME generation (the browser-process recycle
            // window) - which is the Retry budget's business, not the claim's.
            return LiveOrRetry(facts);
        }

        private static ClaimWatchVerdict LiveOrRetry(ClaimWatchFacts facts)
        {
            if (facts.ViewLive)
            {
                return new ClaimWatchVerdict(ClaimWatchAction.None);
            }

            // Only a FAILED creation (the #87 notice standing) is the handoff's retry shape. A
            // deliberately closed view (WebView disposed without an init failure) is the user's
            // doing - the watch never resurrects it.
            if (!facts.InitFailurePending)
            {
                return new ClaimWatchVerdict(ClaimWatchAction.None);
            }

            if (facts.NowUnixMs - facts.LastInitAttemptUnixMs < ReCreationGraceMs)
            {
                // An attempt is in flight or barely settled; the browser-process recycle measured
                // ~13 s - do not pile up attempts inside the grace window.
                return new ClaimWatchVerdict(ClaimWatchAction.None);
            }

            if (facts.HandoffAttempts >= MaxHandoffRetries)
            {
                // Log the give-up exactly once at the boundary; later ticks stay quiet.
                return facts.HandoffAttempts == MaxHandoffRetries
                    ? new ClaimWatchVerdict(ClaimWatchAction.GiveUp,
                        "the boundary's proxied view never attached within " + MaxHandoffRetries + " re-creation attempts for this claim generation - leaving the in-view notice (Refresh remains the manual remedy)")
                    : new ClaimWatchVerdict(ClaimWatchAction.None);
            }

            return new ClaimWatchVerdict(ClaimWatchAction.Retry,
                "the live claim matches this browser's arguments but its WebView never attached (attempt " + (facts.HandoffAttempts + 1) + " of " + MaxHandoffRetries + ") - re-attempting creation");
        }
    }
}
