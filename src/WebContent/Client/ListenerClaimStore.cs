using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace WebContent.Client
{
    internal enum ListenerClaimFileReadKind
    {
        /// <summary>The slot simply is not there.</summary>
        NotFound,

        /// <summary>The slot is there; <c>Content</c> carries it for the pure parse.</summary>
        Found,

        /// <summary>The slot exists but could not be read - treated as untrusted, with the IO reason.</summary>
        Error,
    }

    /// <summary>Raw slot read handed to the pure parse by <see cref="IListenerClaimFiles.Read"/>.</summary>
    internal sealed class ListenerClaimFileRead
    {
        internal ListenerClaimFileReadKind Kind { get; private set; }

        internal string Content { get; private set; }

        internal string ErrorReason { get; private set; }

        private ListenerClaimFileRead()
        {
        }

        internal static ListenerClaimFileRead NotFound()
        {
            return new ListenerClaimFileRead { Kind = ListenerClaimFileReadKind.NotFound };
        }

        internal static ListenerClaimFileRead Found(string content)
        {
            return new ListenerClaimFileRead { Kind = ListenerClaimFileReadKind.Found, Content = content };
        }

        internal static ListenerClaimFileRead Error(string reason)
        {
            return new ListenerClaimFileRead { Kind = ListenerClaimFileReadKind.Error, ErrorReason = reason };
        }
    }

    /// <summary>
    /// The ONE thin filesystem seam of <see cref="ListenerClaimStore"/>. The atomicity contract
    /// lives here: <see cref="TryCreateExclusive"/> must fail (return false) whenever ANY other
    /// process has already created the file - on the real filesystem that is
    /// <c>FileMode.CreateNew</c>, which two racing claimers resolve to exactly one winner.
    /// </summary>
    internal interface IListenerClaimFiles
    {
        ListenerClaimFileRead Read(string claimPath);

        bool TryCreateExclusive(string claimPath, string content);

        bool TryDelete(string claimPath);
    }

    /// <summary>
    /// Production <see cref="IListenerClaimFiles"/> over the real filesystem. Parent directories
    /// are created on demand (the boundary's <c>server-&lt;key&gt;</c> dir may not exist yet when
    /// the FIRST proxied browser of this boundary claims).
    /// </summary>
    internal sealed class FileSystemListenerClaimFiles : IListenerClaimFiles
    {
        public ListenerClaimFileRead Read(string claimPath)
        {
            if (!File.Exists(claimPath))
            {
                return ListenerClaimFileRead.NotFound();
            }

            try
            {
                return ListenerClaimFileRead.Found(File.ReadAllText(claimPath));
            }
            catch (IOException ex)
            {
                return ListenerClaimFileRead.Error("reading failed: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ListenerClaimFileRead.Error("reading is denied: " + ex.Message);
            }
        }

        public bool TryCreateExclusive(string claimPath, string content)
        {
            string tempPath = null;
            try
            {
                var parent = Path.GetDirectoryName(claimPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                // Atomic create WITH CONTENT: a plain CreateNew leaves a visible-but-empty
                // window between create and write, into which a joining reader would parse an
                // empty file, call it corrupt, and DELETE the fresh owner's claim. Writing the
                // full record to a temp name and renaming makes the slot transition empty ->
                // complete-file in one step; the rename itself throws when the destination
                // already exists, which is the atomic-create loser condition. The .tmp litter
                // is the same shape class #94's sweep reaps.
                tempPath = claimPath + ".tmp-" + Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                           "-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tempPath, content);
                File.Move(tempPath, claimPath);
                return true;
            }
            catch (IOException)
            {
                // The rename threw because the file EXISTS (a racer won the atomic create), or a
                // transient IO fault: either way this attempt did not own the slot.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                if (tempPath != null && File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // Leftover temp litter; #94's sweep reaps it.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        public bool TryDelete(string claimPath)
        {
            try
            {
                File.Delete(claimPath);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal enum ListenerClaimOutcome
    {
        /// <summary>This process owns the slot: bind the listener on the intended port and publish.</summary>
        Owner,

        /// <summary>A live claim exists: compose the browser arguments from <c>Attempt.Claim.Port</c>.</summary>
        Joined,

        /// <summary>The protocol could not reach a stable answer (bounded attempts); never a silent own.</summary>
        Failed,
    }

    /// <summary>The result of one full <see cref="ListenerClaimStore.Claim"/> protocol run.</summary>
    internal sealed class ListenerClaimAttempt
    {
        internal ListenerClaimOutcome Outcome { get; private set; }

        /// <summary>
        /// Joined: the owner's claim (its Port is what the caller bakes into the browser
        /// arguments). Owner: this process's own record. Failed: the last state seen.
        /// </summary>
        internal ListenerClaim Claim { get; private set; }

        /// <summary>
        /// Audit lines the caller must log (each names the claim file and the reason): stale
        /// takeovers, untrusted-file replacements, join fingerprints, protocol give-ups.
        /// </summary>
        internal IReadOnlyList<string> AuditLines { get; private set; }

        internal ListenerClaimAttempt(ListenerClaimOutcome outcome, ListenerClaim claim, IReadOnlyList<string> auditLines)
        {
            Outcome = outcome;
            Claim = claim;
            AuditLines = auditLines;
        }
    }

    /// <summary>
    /// The file store of the listener-claim protocol (ADR-0004 point 3, #93): consult BEFORE a
    /// proxied browser binds its loopback listener. It drives the pure
    /// <see cref="ListenerClaimCore"/> over one thin filesystem seam
    /// (<see cref="IListenerClaimFiles"/>) and the two injectable liveness probes, so every
    /// decision row is unit-testable with no filesystem and no process, while the atomic-create
    /// race itself is verified against real files.
    ///
    /// Protocol: read the slot - absent ⇒ atomic create and own; live claim ⇒ join by reading
    /// its port; stale (dead pid / dead port), corrupt or foreign-schema ⇒ audit, delete, re-claim
    /// through the same atomic create. Two racing claimers resolve on the atomic create: exactly
    /// one wins, the loser re-reads and joins. A join whose claim was actually published by THIS
    /// pid is reported as <see cref="ListenerClaimOutcome.Owner"/> (idempotent re-claim).
    /// </summary>
    internal sealed class ListenerClaimStore
    {
        /// <summary>
        /// Bound on protocol iterations so a pathological delete/re-create ping-pong with a racer
        /// ends in <see cref="ListenerClaimOutcome.Failed"/> (audited) instead of spinning.
        /// Real contention resolves in two iterations (winner creates, loser joins).
        /// </summary>
        private const int MaxProtocolAttempts = 4;

        private static readonly IListenerClaimFiles DefaultFiles = new FileSystemListenerClaimFiles();

        private readonly IListenerClaimFiles _files;
        private readonly Func<int, bool> _ownerProcessAlive;
        private readonly Func<int, bool> _loopbackPortConnectable;
        private readonly int _ownerPid;

        /// <param name="files">Filesystem seam; defaults to the real filesystem.</param>
        /// <param name="ownerProcessAlive">Liveness probe seam; defaults to <see cref="ProcessExists"/>.</param>
        /// <param name="loopbackPortConnectable">Port probe seam; defaults to
        /// <see cref="LoopbackPortConnectable"/>.</param>
        /// <param name="ownerPid">The pid to record as owner; defaults to this process. Tests use
        /// fake pids so two in-process racers can pretend to be two processes.</param>
        internal ListenerClaimStore(
            IListenerClaimFiles files = null,
            Func<int, bool> ownerProcessAlive = null,
            Func<int, bool> loopbackPortConnectable = null,
            int? ownerPid = null)
        {
            _files = files ?? DefaultFiles;
            _ownerProcessAlive = ownerProcessAlive ?? ProcessExists;
            _loopbackPortConnectable = loopbackPortConnectable ?? LoopbackPortConnectable;
            _ownerPid = ownerPid ?? Process.GetCurrentProcess().Id;
        }

        /// <summary>
        /// Runs the claim protocol for one boundary/browser slot. The caller supplies the port it
        /// INTENDS to bind (it binds only if the outcome is <see cref="ListenerClaimOutcome.Owner"/>,
        /// or composes the owner's port on <see cref="ListenerClaimOutcome.Joined"/>).
        /// </summary>
        /// <param name="floorEpoch">#96: the generation this process last knew for this slot (its
        /// own recorded epoch, or 1 for a first consult). A takeover of a PARSEABLE claim always
        /// bumps past the parsed epoch; where the previous generation is unknowable (absent,
        /// corrupt, or foreign-schema slot) the floor keeps the generation monotone for the
        /// watcher that remembers it.</param>
        internal ListenerClaimAttempt Claim(string claimPath, Guid browserId, int intendedPort, string configFingerprint, long floorEpoch = 1)
        {
            var intended = new ListenerClaim(
                browserId,
                intendedPort,
                _ownerPid,
                configFingerprint,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                floorEpoch < 1 ? 1 : floorEpoch);

            var audits = new List<string>();

            for (var attempt = 0; attempt < MaxProtocolAttempts; attempt++)
            {
                var raw = _files.Read(claimPath);
                var read = raw.Kind == ListenerClaimFileReadKind.Found
                    ? ListenerClaim.Read(raw.Content)
                    : raw.Kind == ListenerClaimFileReadKind.Error
                        ? ListenerClaimRead.Untrusted("the file could not be read: " + raw.ErrorReason)
                        : ListenerClaimRead.NotFound();

                var move = ListenerClaimCore.Decide(read, intended, _ownerProcessAlive, _loopbackPortConnectable);
                if (move.Audit != null)
                {
                    audits.Add(AuditLine(claimPath, move.Audit));
                }

                if (move.NewEpoch.HasValue && move.NewEpoch.Value > intended.Epoch)
                {
                    // #96: the replacement generation names the displaced claim's epoch + 1, so
                    // every process that JOINED the displaced claim notices the handoff.
                    intended.Epoch = move.NewEpoch.Value;
                }

                switch (move.Step)
                {
                    case ListenerClaimStep.Join:
                        // Our own published record came back: that is ownership, not a join.
                        // Report the ON-DISK record - it is the slot's truth (epoch included).
                        if (move.JoinClaim.OwnerPid == _ownerPid)
                        {
                            return new ListenerClaimAttempt(ListenerClaimOutcome.Owner, move.JoinClaim, audits);
                        }

                        if (!move.JoinFingerprintMatches)
                        {
                            audits.Add(AuditLine(claimPath,
                                "joining a live owner whose config fingerprint differs from this process's"));
                        }

                        return new ListenerClaimAttempt(ListenerClaimOutcome.Joined, move.JoinClaim, audits);

                    case ListenerClaimStep.ClaimOverStale:
                        // Drop the untrustworthy slot, then re-enter for the atomic create. The
                        // create is still exclusive, so racers still resolve to one winner.
                        _files.TryDelete(claimPath);
                        continue;

                    default: // ClaimNew
                        if (_files.TryCreateExclusive(claimPath, intended.Serialize()))
                        {
                            return new ListenerClaimAttempt(ListenerClaimOutcome.Owner, intended, audits);
                        }

                        // A racer won the atomic create (or an IO fault): re-read and re-decide.
                        continue;
                }
            }

            audits.Add(AuditLine(claimPath,
                "the claim protocol did not settle within " + MaxProtocolAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " attempts - neither owning nor joining this slot"));
            return new ListenerClaimAttempt(ListenerClaimOutcome.Failed, intended, audits);
        }

        private static string AuditLine(string claimPath, string reason)
        {
            return "Listener claim '" + claimPath + "': " + reason;
        }

        /// <summary>
        /// Production pid probe: does this pid name a live process? A pid that is simply gone is
        /// dead; a process we cannot inspect is assumed ALIVE (stealing a live owner's slot on a
        /// probe error is the worse failure).
        /// </summary>
        internal static bool ProcessExists(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Production port probe: does a TCP connect to 127.0.0.1:&lt;port&gt; succeed? A refused
        /// connect means the owning listener is gone even when its process still runs - the second
        /// half of the ADR-0004 staleness rule. The probe connects and closes without sending, so
        /// the owner's listener sees one empty request it terminates - journalled noise, no harm.
        /// </summary>
        internal static bool LoopbackPortConnectable(int port)
        {
            if (port < 1 || port > 65535)
            {
                return false;
            }

            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)))
                    {
                        return false;
                    }

                    client.EndConnect(connect);
                    return true;
                }
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
    }
}
