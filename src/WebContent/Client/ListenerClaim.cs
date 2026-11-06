using System;
using System.Collections.Generic;
using System.Globalization;

namespace WebContent.Client
{
    /// <summary>
    /// ADR-0004 (#93): the listener-claim record one client process publishes for ONE proxied
    /// boundary, so the other processes on that boundary join its loopback listener instead of
    /// binding their own (the per-process ephemeral-port mismatch is the #87 blank view). The
    /// record names browser id, loopback port, owning process id, config fingerprint, the
    /// claim timestamp, and (#96) the listener GENERATION (epoch): every takeover publishes the
    /// previous epoch + 1 so all processes attached to the boundary notice the handoff. The
    /// <see cref="BrowserProfileKey.ComposeListenerClaimPath"/> derives the
    /// file path from the full boundary, so crossing the boundary is impossible by construction.
    ///
    /// This class is pure: schema serialize/parse only, no filesystem, no process. The schema is
    /// versioned so a FUTURE writer cannot brick an old reader: a header this build does not know
    /// parses as untrusted (the caller treats it as stale with an audit line), never a crash.
    /// Unknown KEY names inside a known version are tolerated for the same reason - future fields
    /// may be added without a version bump bricking older readers - while missing or malformed
    /// REQUIRED fields are untrusted.
    /// </summary>
    internal sealed class ListenerClaim
    {
        /// <summary>
        /// The only schema version this build writes and trusts. A file whose header names any
        /// other version is foreign: never trusted, treated as stale, audited.
        /// </summary>
        internal const int CurrentSchemaVersion = 2;

        /// <summary>
        /// First line of a claim file: <c>webcontent-listener-claim/&lt;version&gt;</c>.
        /// </summary>
        internal const string SchemaHeaderPrefix = "webcontent-listener-claim/";

        internal Guid BrowserId { get; private set; }

        /// <summary>The loopback port the owning process bound (127.0.0.1).</summary>
        internal int Port { get; private set; }

        /// <summary>The owning process id; a claim whose owner pid is gone is stale.</summary>
        internal int OwnerPid { get; private set; }

        /// <summary>
        /// Fingerprint of the owner's browser-argument configuration at claim time. The joiner
        /// needs the owner's port to compose the identical <c>--proxy-server</c> argument; the
        /// fingerprint lets it (and #96's handoff) tell whether the claim describes a matching
        /// configuration without re-deriving it.
        /// </summary>
        internal string ConfigFingerprint { get; private set; }

        /// <summary>
        /// When the claim was written (unix epoch ms). Staleness itself is decided by the live
        /// pid/port checks, never by this value; it is diagnostic now and the #96 handoff's
        /// ordering evidence later.
        /// </summary>
        internal long ClaimedAtUnixMs { get; private set; }

        /// <summary>
        /// #96: the generation of this boundary's listener. Every process attached to the
        /// boundary records the epoch it joined under; when a takeover replaces a claim the new
        /// claim names the previous epoch + 1, so EVERY attached process notices the change from
        /// its own watch (a live browser process cannot change its --proxy-server arguments -
        /// the epoch change is what triggers the forced re-creation of the boundary's views).
        /// Schema v1 had no epoch: it parses as a foreign version (stale, audited), which is
        /// exactly the #93 rule for older schemas.
        /// </summary>
        internal long Epoch { get; set; }

        internal ListenerClaim(Guid browserId, int port, int ownerPid, string configFingerprint, long claimedAtUnixMs, long epoch = 1)
        {
            BrowserId = browserId;
            Port = port;
            OwnerPid = ownerPid;
            ConfigFingerprint = configFingerprint ?? string.Empty;
            ClaimedAtUnixMs = claimedAtUnixMs;
            Epoch = epoch;
        }

        /// <summary>
        /// Renders the record in the on-disk schema (one line per field, invariant culture).
        /// </summary>
        internal string Serialize()
        {
            return SchemaHeaderPrefix + CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture) + "\n" +
                   "browserId=" + BrowserId.ToString("D") + "\n" +
                   "port=" + Port.ToString(CultureInfo.InvariantCulture) + "\n" +
                   "ownerPid=" + OwnerPid.ToString(CultureInfo.InvariantCulture) + "\n" +
                   "configFingerprint=" + ConfigFingerprint + "\n" +
                   "claimedAtUnixMs=" + ClaimedAtUnixMs.ToString(CultureInfo.InvariantCulture) + "\n" +
                   "epoch=" + Epoch.ToString(CultureInfo.InvariantCulture) + "\n";
        }

        /// <summary>
        /// Parses claim-file content into a decision input. Strict where it must be (header
        /// version, required fields, value ranges), tolerant where tolerance is the versioning
        /// contract (blank lines, unknown future field names). A null content means "file not
        /// there"; a read ERROR reaches the caller as <see cref="ListenerClaimReadKind.Untrusted"/>.
        /// Never throws on hostile content.
        /// </summary>
        internal static ListenerClaimRead Read(string content)
        {
            if (content == null)
            {
                return ListenerClaimRead.NotFound();
            }

            var lines = content.Replace("\r\n", "\n").Split('\n');
            var index = 0;
            while (index < lines.Length && lines[index].Trim().Length == 0)
            {
                index++;
            }

            if (index >= lines.Length)
            {
                return ListenerClaimRead.Untrusted("the file is empty - no schema header");
            }

            var header = lines[index].Trim();
            if (!header.StartsWith(SchemaHeaderPrefix, StringComparison.Ordinal))
            {
                return ListenerClaimRead.Untrusted("the first line is not the claim schema header");
            }

            var versionText = header.Substring(SchemaHeaderPrefix.Length);
            int version;
            if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out version))
            {
                return ListenerClaimRead.Untrusted("the schema header names no version");
            }

            if (version != CurrentSchemaVersion)
            {
                return ListenerClaimRead.Untrusted(
                    "the schema version is " + version.ToString(CultureInfo.InvariantCulture) +
                    ", this build only understands " + CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (index++; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var split = line.IndexOf('=');
                if (split <= 0)
                {
                    return ListenerClaimRead.Untrusted("a body line is not a name=value field: '" + line + "'");
                }

                var key = line.Substring(0, split);
                if (values.ContainsKey(key))
                {
                    return ListenerClaimRead.Untrusted("the field '" + key + "' appears twice");
                }

                // Unknown key names are tolerated: they are a future build's fields, and this
                // version must not brick on them (the version header is the compatibility gate).
                values.Add(key, line.Substring(split + 1));
            }

            string browserIdText, portText, ownerPidText, fingerprintText, claimedAtText, epochText;
            if (!values.TryGetValue("browserId", out browserIdText) ||
                !values.TryGetValue("port", out portText) ||
                !values.TryGetValue("ownerPid", out ownerPidText) ||
                !values.TryGetValue("configFingerprint", out fingerprintText) ||
                !values.TryGetValue("claimedAtUnixMs", out claimedAtText) ||
                !values.TryGetValue("epoch", out epochText))
            {
                return ListenerClaimRead.Untrusted("a required field is missing");
            }

            Guid browserId;
            if (!Guid.TryParse(browserIdText, out browserId))
            {
                return ListenerClaimRead.Untrusted("the browserId is not a guid");
            }

            int port;
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            {
                return ListenerClaimRead.Untrusted("the port is not a usable port number");
            }

            int ownerPid;
            if (!int.TryParse(ownerPidText, NumberStyles.None, CultureInfo.InvariantCulture, out ownerPid) || ownerPid < 1)
            {
                return ListenerClaimRead.Untrusted("the ownerPid is not a usable process id");
            }

            long claimedAt;
            if (!long.TryParse(claimedAtText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out claimedAt) || claimedAt < 0)
            {
                return ListenerClaimRead.Untrusted("the claimedAtUnixMs is not a usable timestamp");
            }

            long epoch;
            if (!long.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out epoch) || epoch < 1)
            {
                return ListenerClaimRead.Untrusted("the epoch is not a usable generation number");
            }

            return ListenerClaimRead.Trusted(new ListenerClaim(browserId, port, ownerPid, fingerprintText, claimedAt, epoch));
        }
    }

    internal enum ListenerClaimReadKind
    {
        /// <summary>No claim file exists (yet).</summary>
        NotFound,

        /// <summary>Parsed to this build's schema; liveness still has to be checked.</summary>
        Trusted,

        /// <summary>
        /// Corrupt, unreadable, or a foreign schema version: NEVER trusted, treated as stale
        /// with an audit line naming the file and the reason.
        /// </summary>
        Untrusted,
    }

    /// <summary>The parse result <see cref="ListenerClaimCore.Decide"/> and the store reason over.</summary>
    internal sealed class ListenerClaimRead
    {
        internal ListenerClaimReadKind Kind { get; private set; }

        /// <summary>Parsed claim (set when <see cref="Kind"/> is <see cref="ListenerClaimReadKind.Trusted"/>).</summary>
        internal ListenerClaim Claim { get; private set; }

        /// <summary>Why the file is not trusted (set when <see cref="Kind"/> is Untrusted).</summary>
        internal string Reason { get; private set; }

        private ListenerClaimRead()
        {
        }

        internal static ListenerClaimRead NotFound()
        {
            return new ListenerClaimRead { Kind = ListenerClaimReadKind.NotFound };
        }

        internal static ListenerClaimRead Trusted(ListenerClaim claim)
        {
            return new ListenerClaimRead { Kind = ListenerClaimReadKind.Trusted, Claim = claim };
        }

        internal static ListenerClaimRead Untrusted(string reason)
        {
            return new ListenerClaimRead { Kind = ListenerClaimReadKind.Untrusted, Reason = reason };
        }
    }

    internal enum ListenerClaimStep
    {
        /// <summary>No claim exists: create it atomically and own.</summary>
        ClaimNew,

        /// <summary>
        /// A claim exists but is provably stale, corrupt or foreign: delete it and re-claim
        /// (the re-claim itself goes through the same atomic create).
        /// </summary>
        ClaimOverStale,

        /// <summary>A live claim exists: join it by reading its port.</summary>
        Join,
    }

    /// <summary>One pure protocol step the store acts on.</summary>
    internal sealed class ListenerClaimMove
    {
        internal ListenerClaimStep Step { get; private set; }

        /// <summary>Audit-worthy reason (stale/corrupt/foreign handoffs); null for quiet steps.</summary>
        internal string Audit { get; private set; }

        /// <summary>The claim to join (set on <see cref="ListenerClaimStep.Join"/>).</summary>
        internal ListenerClaim JoinClaim { get; private set; }

        /// <summary>
        /// #96: the generation a replacement claim must publish. Set for a takeover of a claim
        /// this build could PARSE (previous epoch + 1, so the boundary's attached processes all
        /// notice the handoff); null where no previous generation is knowable (absent slot,
        /// corrupt or foreign-schema content) and the caller's floor epoch governs.
        /// </summary>
        internal long? NewEpoch { get; private set; }

        /// <summary>
        /// On a join: whether the owner's config fingerprint matches this process's intended
        /// fingerprint. Informational for the caller (a mismatch says the live claim describes a
        /// different browser-argument posture); deciding what to do about it is child C/#96 wiring.
        /// </summary>
        internal bool JoinFingerprintMatches { get; private set; }

        internal ListenerClaimMove(ListenerClaimStep step, string audit = null, ListenerClaim joinClaim = null, bool joinFingerprintMatches = false, long? newEpoch = null)
        {
            Step = step;
            Audit = audit;
            JoinClaim = joinClaim;
            JoinFingerprintMatches = joinFingerprintMatches;
            NewEpoch = newEpoch;
        }
    }

    /// <summary>
    /// The pure decision core of the listener-claim protocol (ADR-0004 point 3, #93): given the
    /// parse of the claim slot, what this process intends to claim, and the two injectable
    /// liveness answers, what to do next. No filesystem, no process, no socket is consulted
    /// here - the store supplies the facts, this decides. The staleness rule is exactly the
    /// ADR's: the owner process is gone OR the owner's port refuses a connect probe. A corrupt
    /// or foreign-schema claim is never trusted: stale-by-definition, with an audit line.
    /// </summary>
    internal static class ListenerClaimCore
    {
        /// <param name="read">What reading the claim slot yielded (NotFound / Trusted / Untrusted).</param>
        /// <param name="intended">This process's would-be claim (browser id, port, own pid, fingerprint).</param>
        /// <param name="ownerProcessAlive">Is this pid currently running? Injected seam; the store's production
        /// version asks the OS. Only consulted for a parsed claim whose owner pid is NOT this process.</param>
        /// <param name="loopbackPortConnectable">Does a TCP connect to this loopback port succeed? Injected
        /// seam; the store's production version actually connects. Only consulted for a parsed claim whose
        /// owner pid is NOT this process. Note the probe opens (and immediately closes) one connection the
        /// owner's listener will see as an empty request - harmless to the listener, journalled by it.</param>
        internal static ListenerClaimMove Decide(
            ListenerClaimRead read,
            ListenerClaim intended,
            Func<int, bool> ownerProcessAlive,
            Func<int, bool> loopbackPortConnectable)
        {
            switch (read.Kind)
            {
                case ListenerClaimReadKind.NotFound:
                    return new ListenerClaimMove(ListenerClaimStep.ClaimNew);

                case ListenerClaimReadKind.Untrusted:
                    return new ListenerClaimMove(ListenerClaimStep.ClaimOverStale, read.Reason);

                default:
                    var claim = read.Claim;

                    // The claim file path already keys the boundary and the browser id; a file
                    // that names a DIFFERENT browser id is foreign litter in this slot - never
                    // trusted, and audited like any other stale evidence.
                    if (claim.BrowserId != intended.BrowserId)
                    {
                        return new ListenerClaimMove(ListenerClaimStep.ClaimOverStale,
                            "the claim names browser " + claim.BrowserId.ToString("D") + ", not " + intended.BrowserId.ToString("D"),
                            null, false, claim.Epoch + 1);
                    }

                    if (claim.OwnerPid == intended.OwnerPid)
                    {
                        // Our own claim slot. Identical record: we already own exactly what we
                        // intend (an idempotent re-claim). A different port/fingerprint is our
                        // own stale leftover (e.g. a recreated browser bound a new port) - overwrite.
                        if (claim.Port == intended.Port && claim.ConfigFingerprint == intended.ConfigFingerprint)
                        {
                            return new ListenerClaimMove(ListenerClaimStep.Join, null, claim, true);
                        }

                        return new ListenerClaimMove(ListenerClaimStep.ClaimOverStale,
                            "the claim is this process's own earlier claim (port " +
                            claim.Port.ToString(CultureInfo.InvariantCulture) + ")",
                            null, false, claim.Epoch + 1);
                    }

                    if (!ownerProcessAlive(claim.OwnerPid))
                    {
                        return new ListenerClaimMove(ListenerClaimStep.ClaimOverStale,
                            "the owner process (pid " + claim.OwnerPid.ToString(CultureInfo.InvariantCulture) + ") is no longer running",
                            null, false, claim.Epoch + 1);
                    }

                    if (!loopbackPortConnectable(claim.Port))
                    {
                        return new ListenerClaimMove(ListenerClaimStep.ClaimOverStale,
                            "the owner's loopback port (" + claim.Port.ToString(CultureInfo.InvariantCulture) +
                            ") refused a connect probe",
                            null, false, claim.Epoch + 1);
                    }

                    return new ListenerClaimMove(
                        ListenerClaimStep.Join,
                        null,
                        claim,
                        string.Equals(claim.ConfigFingerprint, intended.ConfigFingerprint, StringComparison.Ordinal));
            }
        }
    }
}
