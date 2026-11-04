using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WebContent.Client
{
    internal enum ProxyListenerLeaseRole
    {
        /// <summary>
        /// This process atomically claimed the boundary slot: it must bind its listener on the
        /// lease's reserved port (the exact port the published claim names) and owns the claim
        /// until the browser is disposed.
        /// </summary>
        Owner,

        /// <summary>
        /// A live owner claim exists inside this boundary: bind NO listener and compose the
        /// identical --proxy-server argument from the owner's port (#95, ADR-0004 point 3).
        /// </summary>
        Joined,

        /// <summary>
        /// The claim protocol could not settle (or no port could be reserved): fall back to the
        /// pre-#95 posture - an ephemeral listener and NO claim written, so nobody joins a port
        /// that was never bound. Audited, never a silent own.
        /// </summary>
        UnclaimedFallback,
    }

    /// <summary>
    /// One consult-then-claim-or-join outcome for one proxied browser: which role this process
    /// takes for the boundary slot, the port to bake into the browser arguments, the claim path
    /// to release when the browser dies (Owner only), and the audit lines to journal verbatim.
    /// </summary>
    internal sealed class ProxyListenerLease
    {
        internal ProxyListenerLeaseRole Role { get; }

        /// <summary>
        /// Owner: the reserved port the listener must bind; Joined: the owner's live port;
        /// UnclaimedFallback: 0 (bind ephemeral exactly like before the claim existed).
        /// </summary>
        internal int Port { get; }

        /// <summary>Claim slot to release on browser teardown; null unless this process owns it.</summary>
        internal string ClaimPath { get; }

        /// <summary>
        /// #96: the generation the slot now names (Owner: the epoch this process published;
        /// Joined: the live owner's epoch; UnclaimedFallback: 0). The consuming browser records
        /// it and its claim watcher compares every consult against it.
        /// </summary>
        internal long Epoch { get; }

        /// <summary>#96: the pid the slot's liveness rides (own pid for Owner, the owner's for Joined; 0 on fallback).</summary>
        internal int OwnerPid { get; }

        internal IReadOnlyList<string> AuditLines { get; }

        internal ProxyListenerLease(ProxyListenerLeaseRole role, int port, string claimPath, IReadOnlyList<string> auditLines, long epoch = 0, int ownerPid = 0)
        {
            Role = role;
            Port = port;
            ClaimPath = claimPath;
            AuditLines = auditLines ?? new string[0];
            Epoch = epoch;
            OwnerPid = ownerPid;
        }
    }

    /// <summary>
    /// The wiring seam between a proxied browser creation and the #93 listener-claim protocol.
    /// Before a proxied browser binds a loopback listener, this consults the boundary's claim
    /// slot through <see cref="ListenerClaimStore"/> and answers the one question
    /// <see cref="BrowserManager"/> needs: owner (bind the reserved port), joined (no listener at
    /// all - every fetch rides the owner's listener and Event Server channel), or fallback
    /// (today's ephemeral listener, no claim). It also owns the release side: a dead owner's
    /// browser must not leave a claim pointing at a port nobody serves.
    ///
    /// The port is reserved BEFORE claiming because the claim record must publish the port the
    /// owner will actually bind (the joiner composes its identical --proxy-server argument from
    /// it). Reservation and bind are two steps with a race window against processes OUTSIDE the
    /// boundary (any local process may grab a free port); the manager handles a refused bind by
    /// releasing the claim and falling back to ephemeral, so a claim never outlives a lie.
    /// </summary>
    internal sealed class ProxyListenerLeaseProvider
    {
        private static readonly IListenerClaimFiles DefaultFiles = new FileSystemListenerClaimFiles();

        private readonly ListenerClaimStore _store;
        private readonly Func<int> _portAllocator;
        private readonly IListenerClaimFiles _files;
        private readonly int _ownerPid;

        internal ProxyListenerLeaseProvider(
            ListenerClaimStore store = null,
            Func<int> portAllocator = null,
            IListenerClaimFiles files = null,
            int? ownerPid = null)
        {
            _store = store ?? new ListenerClaimStore();
            _portAllocator = portAllocator ?? ReserveEphemeralLoopbackPort;
            _files = files ?? DefaultFiles;
            _ownerPid = ownerPid ?? Process.GetCurrentProcess().Id;
        }

        /// <summary>
        /// Runs one claim-protocol round for a proxied browser. The fingerprint identifies this
        /// process's browser-argument posture; a live claim whose fingerprint differs is JOINED
        /// anyway with exactly one logged audit - the owner's listener is the only listener this
        /// browser could ever reach, so refusing to join buys a blank view and nothing else
        /// (#95 triage decision).
        /// </summary>
        /// <param name="floorEpoch">#96: the generation this process last recorded for the slot
        /// (1 for a first consult). Takeovers publish at least the displaced claim's epoch + 1.</param>
        /// <param name="pinPort">#96: a port this process ALREADY serves (an owner re-publishing
        /// its vanished/corrupted slot). Skips the reserve-first dance: the intended claim names
        /// the bound port verbatim, so joiners keep composing the argument they already carry.</param>
        internal ProxyListenerLease Acquire(string claimPath, Guid browserId, string configFingerprint, long floorEpoch = 1, int? pinPort = null)
        {
            int port;
            if (pinPort != null && pinPort.Value >= 1 && pinPort.Value <= 65535)
            {
                port = pinPort.Value;
            }
            else
            {
                port = _portAllocator();
            }

            if (port < 1 || port > 65535)
            {
                return new ProxyListenerLease(ProxyListenerLeaseRole.UnclaimedFallback, 0, null, new[]
                {
                    AuditLine(claimPath, "no ephemeral loopback port could be reserved - binding an unpinned ephemeral listener and claiming nothing"),
                });
            }

            var attempt = _store.Claim(claimPath, browserId, port, configFingerprint, floorEpoch);
            switch (attempt.Outcome)
            {
                case ListenerClaimOutcome.Owner:
                    return new ProxyListenerLease(ProxyListenerLeaseRole.Owner, attempt.Claim.Port, claimPath, attempt.AuditLines, attempt.Claim.Epoch, attempt.Claim.OwnerPid);

                case ListenerClaimOutcome.Joined:
                    return new ProxyListenerLease(ProxyListenerLeaseRole.Joined, attempt.Claim.Port, null, attempt.AuditLines, attempt.Claim.Epoch, attempt.Claim.OwnerPid);

                default:
                    return new ProxyListenerLease(ProxyListenerLeaseRole.UnclaimedFallback, 0, null, attempt.AuditLines);
            }
        }

        /// <summary>Releases this process's claim slot (owner teardown). Never touches a slot a
        /// different live process has since taken over.</summary>
        internal void Release(string claimPath)
        {
            ReleaseOwnClaimFile(claimPath, _files, _ownerPid);
        }

        /// <summary>
        /// The teardown rule, static so browser teardown reaches it too: a claim is deleted ONLY
        /// when it parses trusted and names THIS process as owner. A missing slot is nothing to
        /// do; corrupt or foreign-schema content is not ours to judge (a newer build's live
        /// owner would be evicted); a claim naming another pid is a takeover to leave alone.
        /// Best-effort: an IO refusal leaves litter #94's sweep reaps.
        /// </summary>
        internal static void ReleaseOwnClaimFile(string claimPath, IListenerClaimFiles files = null, int? ownerPid = null)
        {
            if (string.IsNullOrEmpty(claimPath))
            {
                return;
            }

            try
            {
                var seam = files ?? DefaultFiles;
                var pid = ownerPid ?? Process.GetCurrentProcess().Id;

                var raw = seam.Read(claimPath);
                if (raw.Kind != ListenerClaimFileReadKind.Found)
                {
                    return;
                }

                var read = ListenerClaim.Read(raw.Content);
                if (read.Kind != ListenerClaimReadKind.Trusted || read.Claim.OwnerPid != pid)
                {
                    return;
                }

                seam.TryDelete(claimPath);
            }
            catch (Exception)
            {
                // Teardown cleanup never escalates: a stale claim dies by the pid/port probes
                // (and #94's sweep) exactly like a crashed owner's would.
            }
        }

        /// <summary>
        /// Production port reservation: bind 127.0.0.1:0, read the port the OS handed, release
        /// it. The claim then publishes that port and the owner binds it for real
        /// (<see cref="LoopbackProxyListener.Start(int)"/>).
        /// </summary>
        internal static int ReserveEphemeralLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                probe.Start();
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }

        private static string AuditLine(string claimPath, string reason)
        {
            return "Listener claim '" + claimPath + "': " + reason;
        }
    }
}
