using System;

namespace WebContent.Client
{
    /// <summary>
    /// ADR-0004 consequence (#94): what to do with ONE proxied browser-profile folder found
    /// under a per-user browser data root. The folder decision never runs against the
    /// filesystem, a process, or a socket here - <see cref="ProfileSweep"/> gathers the facts
    /// and this decides, so every row is unit-testable without either.
    /// </summary>
    internal enum ProxiedFolderStep
    {
        /// <summary>Too fresh to touch, or its activity could not be established - leave it alone.</summary>
        Keep,

        /// <summary>A live browser lock was seen - never forced, skipped this sweep.</summary>
        SkipLocked,

        /// <summary>Untouched for the full retention window with nobody holding its lock - delete.</summary>
        Reap,
    }

    /// <summary>One pure folder decision plus the audit line the sweep logs for it (null = quiet keep).</summary>
    internal sealed class ProxiedFolderMove
    {
        internal ProxiedFolderStep Step { get; private set; }

        internal string Audit { get; private set; }

        internal ProxiedFolderMove(ProxiedFolderStep step, string audit = null)
        {
            Step = step;
            Audit = audit;
        }
    }

    /// <summary>What to do with ONE listener-claim (or claim-temp-litter) file found beside a profile.</summary>
    internal enum ClaimFileStep
    {
        /// <summary>A live owner's slot (or fresh temp litter mid-create) - never reaped.</summary>
        Keep,

        /// <summary>Provably stale, untrusted, or its sibling profile folder is gone - delete.</summary>
        Reap,
    }

    /// <summary>One pure claim-file decision plus the audit line the sweep logs for it.</summary>
    internal sealed class ClaimFileMove
    {
        internal ClaimFileStep Step { get; private set; }

        internal string Audit { get; private set; }

        internal ClaimFileMove(ClaimFileStep step, string audit = null)
        {
            Step = step;
            Audit = audit;
        }
    }

    /// <summary>
    /// The pure decision seam of the #94 disk sweep (ADR-0004: "the proxied profile folders
    /// need an owner for disk cleanup, which nothing does today"). Given, for one candidate:
    /// age answers from a clock the caller supplies, a lock answer, and claim-liveness answers
    /// through the SAME liveness seams the #93 claim store injects, decide keep / skip / reap.
    ///
    /// The claim staleness is NOT re-invented here: <see cref="DecideClaimFile"/> delegates to
    /// <see cref="ListenerClaimCore.Decide"/>, so the sweep and the claim protocol agree by
    /// construction that stale = owner pid gone OR owner port refuses a connect, and that a
    /// corrupt/foreign-schema/cross-browser-id claim is never trusted.
    /// </summary>
    internal static class ProfileSweepCore
    {
        // ---- shape recognition (the classes the sweep is allowed to touch at all) ----

        /// <summary>The plugin-requested proxied UDF leaf name shape: <c>proxied-&lt;browser id&gt;</c>.</summary>
        internal static bool IsProxiedFolderName(string name)
        {
            return name != null && name.StartsWith(BrowserProfileKey.ProxiedFolderPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The boundary dir shape from #92: <c>server-&lt;server key&gt;</c>.</summary>
        internal static bool IsServerFolderName(string name)
        {
            return name != null && name.StartsWith(BrowserProfileKey.ServerFolderPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The claim-file shape: <c>proxied-&lt;browser id&gt;.claim</c> (the #93 sibling slot).
        /// Claim-TEMP litter also starts this way, so callers must ask
        /// <see cref="IsListenerClaimTempName"/> first.
        /// </summary>
        internal static bool IsListenerClaimName(string name)
        {
            return name != null
                   && name.StartsWith(BrowserProfileKey.ProxiedFolderPrefix, StringComparison.OrdinalIgnoreCase)
                   && name.EndsWith(BrowserProfileKey.ListenerClaimExtension, StringComparison.OrdinalIgnoreCase)
                   && !IsListenerClaimTempName(name);
        }

        /// <summary>
        /// The atomic-create litter shape from #93: <c>proxied-&lt;browser id&gt;.claim.tmp-&lt;pid&gt;-&lt;guid&gt;</c>.
        /// The #93 store always deletes its temp in a finally block; only a process killed
        /// mid-create leaves one behind.
        /// </summary>
        internal static bool IsListenerClaimTempName(string name)
        {
            return name != null
                   && name.StartsWith(BrowserProfileKey.ProxiedFolderPrefix, StringComparison.OrdinalIgnoreCase)
                   && name.IndexOf(".claim.tmp-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Extracts the browser id a <c>proxied-&lt;guid&gt;</c> folder name or a
        /// <c>proxied-&lt;guid&gt;.claim</c> file name claims to key. <see cref="Guid.Empty"/>
        /// for a stem that names no guid (foreign litter - the claim decision then treats it
        /// exactly like the claim protocol treats a cross-browser-id slot: never trusted).
        /// </summary>
        internal static Guid TryParseProxiedLeafBrowserId(string name)
        {
            if (name == null)
            {
                return Guid.Empty;
            }

            var leaf = name;
            if (leaf.EndsWith(BrowserProfileKey.ListenerClaimExtension, StringComparison.OrdinalIgnoreCase))
            {
                leaf = leaf.Substring(0, leaf.Length - BrowserProfileKey.ListenerClaimExtension.Length);
            }

            if (!leaf.StartsWith(BrowserProfileKey.ProxiedFolderPrefix, StringComparison.OrdinalIgnoreCase)
                || leaf.Length <= BrowserProfileKey.ProxiedFolderPrefix.Length)
            {
                return Guid.Empty;
            }

            Guid browserId;
            return Guid.TryParse(leaf.Substring(BrowserProfileKey.ProxiedFolderPrefix.Length), out browserId)
                ? browserId
                : Guid.Empty;
        }

        // ---- the decision tables ----

        /// <summary>
        /// The folder table (acceptance rows: fresh keep, stale reap, locked skip): a folder a
        /// live browser still holds is skipped first, whatever its age (never forced); a folder
        /// whose last activity could not be established is kept (unknown is never evidence of
        /// staleness); otherwise untouched strictly longer than the retention reaps. A last
        /// activity exactly ON the retention boundary is still stale.
        /// </summary>
        /// <param name="lastActivityUtc">Max last-write timestamp under the folder, null when the
        /// scan could not establish one.</param>
        /// <param name="browserLockHeld">Did the lock probes name a live browser on this folder?</param>
        internal static ProxiedFolderMove DecideFolder(
            DateTimeOffset? lastActivityUtc,
            bool browserLockHeld,
            DateTimeOffset nowUtc,
            TimeSpan retention)
        {
            if (browserLockHeld)
            {
                return new ProxiedFolderMove(ProxiedFolderStep.SkipLocked,
                    "a live browser process still holds this profile - never forced");
            }

            if (!lastActivityUtc.HasValue)
            {
                return new ProxiedFolderMove(ProxiedFolderStep.Keep,
                    "its last activity could not be established (IO); an unknown age is not evidence of staleness");
            }

            if (lastActivityUtc.Value > nowUtc - retention)
            {
                return new ProxiedFolderMove(ProxiedFolderStep.Keep);
            }

            return new ProxiedFolderMove(ProxiedFolderStep.Reap,
                "untouched for " + (nowUtc - lastActivityUtc.Value).TotalDays.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                " day(s), past the " + retention.TotalDays.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " day(s) retention");
        }

        /// <summary>
        /// The claim-file table (acceptance rows: dead-owner claim reap, live-owner claim keep),
        /// sharing the #93 store's decision seam: this process's OWN claim is never reaped (a
        /// wiring-time race must not destroy a live slot), a claim whose sibling profile folder
        /// is gone is reaped (the #93 handoff's orphan shape), and everything else goes through
        /// <see cref="ListenerClaimCore.Decide"/> - Join means a live owner (keep),
        /// ClaimOverStale means dead pid / refusing port / corrupt / foreign / cross-id (reap
        /// with that audit). The intended claim fed to Decide carries port 0 deliberately:
        /// Decide only consults the browser id and the owner pid, never the intended port, once
        /// the own-pid case is already handled here.
        /// </summary>
        internal static ClaimFileMove DecideClaimFile(
            ListenerClaimRead read,
            Guid expectedBrowserId,
            int selfPid,
            bool siblingProfileFolderExists,
            Func<int, bool> ownerProcessAlive,
            Func<int, bool> loopbackPortConnectable)
        {
            if (read.Kind == ListenerClaimReadKind.NotFound)
            {
                return new ClaimFileMove(ClaimFileStep.Keep,
                    "the claim file vanished between enumeration and read");
            }

            if (read.Kind == ListenerClaimReadKind.Trusted && read.Claim.OwnerPid == selfPid)
            {
                return new ClaimFileMove(ClaimFileStep.Keep,
                    "this process's own live claim - the sweep never reaps its own slot");
            }

            if (!siblingProfileFolderExists)
            {
                return new ClaimFileMove(ClaimFileStep.Reap,
                    "its sibling profile folder is gone");
            }

            var intended = new ListenerClaim(expectedBrowserId, 0, selfPid, string.Empty, 0);
            var move = ListenerClaimCore.Decide(read, intended, ownerProcessAlive, loopbackPortConnectable);
            if (move.Step == ListenerClaimStep.Join)
            {
                return new ClaimFileMove(ClaimFileStep.Keep);
            }

            return new ClaimFileMove(ClaimFileStep.Reap, move.Audit);
        }

        /// <summary>
        /// The temp-litter table: the atomic create (#93) renames its temp away and its finally
        /// block deletes it, so a temp file older than the litter retention cannot belong to an
        /// in-flight create - reap. Younger litter is kept: it may belong to a create racing
        /// this very sweep.
        /// </summary>
        internal static ClaimFileMove DecideClaimTemp(
            DateTimeOffset lastWriteUtc,
            DateTimeOffset nowUtc,
            TimeSpan litterRetention)
        {
            if (lastWriteUtc > nowUtc - litterRetention)
            {
                return new ClaimFileMove(ClaimFileStep.Keep);
            }

            return new ClaimFileMove(ClaimFileStep.Reap,
                "claim-create temp litter untouched for " +
                (nowUtc - lastWriteUtc).TotalMinutes.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " minute(s)");
        }
    }
}
