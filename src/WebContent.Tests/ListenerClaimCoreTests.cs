using System;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Decision-table tests for the listener-claim core (ADR-0004 point 3, #93): the schema
    /// parse, and the what-to-do decisions over the injectable liveness seams. No filesystem,
    /// no process, no socket is touched here - those live in <see cref="ListenerClaimStoreTests"/>.
    /// </summary>
    public class ListenerClaimCoreTests
    {
        private static readonly Guid BrowserId = Guid.Parse("2963db34-185a-4383-84c5-c9f7d780b425");

        // ---- schema round-trip and versioning -------------------------------------------------

        [Fact]
        public void Serialize_Parse_Roundtrips_Every_Field()
        {
            var claim = new ListenerClaim(BrowserId, 49321, 4242, "fp-abc-123", 1762000000123L, 7);

            var read = ListenerClaim.Read(claim.Serialize());

            Assert.Equal(ListenerClaimReadKind.Trusted, read.Kind);
            Assert.Equal(BrowserId, read.Claim.BrowserId);
            Assert.Equal(49321, read.Claim.Port);
            Assert.Equal(4242, read.Claim.OwnerPid);
            Assert.Equal("fp-abc-123", read.Claim.ConfigFingerprint);
            Assert.Equal(1762000000123L, read.Claim.ClaimedAtUnixMs);
            Assert.Equal(7, read.Claim.Epoch);
        }

        [Fact]
        public void Foreign_Schema_Version_Is_Untrusted_Never_A_Crash()
        {
            // A future build's version: an old reader must treat it as untrusted, never brick.
            var future = "webcontent-listener-claim/3\nbrowserId=" + BrowserId.ToString("D") +
                         "\nport=49321\nownerPid=4242\nconfigFingerprint=fp\nclaimedAtUnixMs=1\nepoch=1\n";

            var read = ListenerClaim.Read(future);

            Assert.Equal(ListenerClaimReadKind.Untrusted, read.Kind);
            Assert.Contains("version", read.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Pre_Epoch_Schema_One_Is_An_Older_Version_Not_Trusted()
        {
            // #96: schema v1 had no epoch. Per the versioning contract an OLDER schema parses
            // exactly like a foreign one - untrusted, treated as stale with an audit - which is
            // how a build predating the handoff generation is displaced on first consult.
            var v1 = "webcontent-listener-claim/1\nbrowserId=" + BrowserId.ToString("D") +
                     "\nport=49321\nownerPid=4242\nconfigFingerprint=fp\nclaimedAtUnixMs=1\n";

            var read = ListenerClaim.Read(v1);

            Assert.Equal(ListenerClaimReadKind.Untrusted, read.Kind);
            Assert.Contains("version", read.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Unknown_Future_Fields_In_The_Current_Version_Are_Tolerated()
        {
            // The version header is the compatibility gate; extra fields inside the known version
            // must not brick the reader (that is what "future fields don't brick old readers" means).
            var withFutureField = "webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") +
                                  "\nport=49321\nownerPid=4242\nconfigFingerprint=fp\nclaimedAtUnixMs=1\nepoch=7\nsomeFutureField=hello\n";

            var read = ListenerClaim.Read(withFutureField);

            Assert.Equal(ListenerClaimReadKind.Trusted, read.Kind);
        }

        [Fact]
        public void Corrupt_Content_Is_Untrusted_With_A_Reason()
        {
            Assert.Equal(ListenerClaimReadKind.Untrusted, ListenerClaim.Read("this is not a claim").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted, ListenerClaim.Read(string.Empty).Kind);

            var missingField = "webcontent-listener-claim/2\nport=49321\nownerPid=4242\n";
            var read = ListenerClaim.Read(missingField);
            Assert.Equal(ListenerClaimReadKind.Untrusted, read.Kind);
            Assert.Contains("missing", read.Reason, StringComparison.OrdinalIgnoreCase);

            // #96: an epoch-less body inside schema 2 is a missing REQUIRED field, not a default.
            var missingEpoch = "webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") +
                               "\nport=49321\nownerPid=4242\nconfigFingerprint=fp\nclaimedAtUnixMs=1\n";
            var epochRead = ListenerClaim.Read(missingEpoch);
            Assert.Equal(ListenerClaimReadKind.Untrusted, epochRead.Kind);
            Assert.Contains("missing", epochRead.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Malformed_Values_Are_Untrusted()
        {
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/2\nbrowserId=not-a-guid\nport=1\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\nepoch=1\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") + "\nport=99999\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\nepoch=1\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") + "\nport=-5\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\nepoch=1\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") + "\nport=1\nownerPid=0\nconfigFingerprint=x\nclaimedAtUnixMs=0\nepoch=1\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") + "\nport=1\nport=2\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\nepoch=1\n").Kind);

            // #96: the epoch must be a positive count - zero, negatives, and garbage are all
            // unusable generations (a takeover can never "reset" the boundary's history).
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") + "\nport=1\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\nepoch=0\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") + "\nport=1\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\nepoch=-3\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") + "\nport=1\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\nepoch=seven\n").Kind);
        }

        // ---- the decision table ----------------------------------------------------------------

        private static ListenerClaim Intended(int port = 49321, string fingerprint = "fp")
        {
            return new ListenerClaim(BrowserId, port, 1000, fingerprint, 1762000000000L);
        }

        private static bool Alive(int pid)
        {
            return true;
        }

        private static bool PortOk(int port)
        {
            return true;
        }

        [Fact]
        public void No_Claim_At_All_Claims_New()
        {
            var move = ListenerClaimCore.Decide(ListenerClaimRead.NotFound(), Intended(), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimNew, move.Step);
            Assert.Null(move.Audit);
        }

        [Fact]
        public void Live_Claim_Of_Another_Live_Process_Is_Joined()
        {
            var other = new ListenerClaim(BrowserId, 49321, 777, "fp", 1L);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(other), Intended(), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.Join, move.Step);
            Assert.Equal(49321, move.JoinClaim.Port);
            Assert.True(move.JoinFingerprintMatches);
        }

        [Fact]
        public void Owner_Pid_Gone_Is_Stale_By_Pid()
        {
            var deadOwner = new ListenerClaim(BrowserId, 49321, 777, "fp", 1L);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(deadOwner), Intended(), pid => false, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Contains("777", move.Audit);
            Assert.Contains("no longer running", move.Audit);
        }

        [Fact]
        public void Owner_Port_Refusing_Is_Stale_By_Port_Even_When_The_Pid_Lives()
        {
            var deadPort = new ListenerClaim(BrowserId, 49321, 777, "fp", 1L);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(deadPort), Intended(), Alive, port => false);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Contains("49321", move.Audit);
            Assert.Contains("connect probe", move.Audit);
        }

        [Fact]
        public void Corrupt_Or_Foreign_Claim_Is_Stale_With_An_Audit_Reason()
        {
            var move = ListenerClaimCore.Decide(ListenerClaim.Read("nonsense"), Intended(), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.False(string.IsNullOrEmpty(move.Audit));
        }

        [Fact]
        public void Claim_Naming_A_Different_Browser_Id_Is_Never_Trusted()
        {
            // The slot path already keys the browser id; a file naming a DIFFERENT id is foreign
            // litter and gets the stale treatment, never a join.
            var foreign = new ListenerClaim(Guid.NewGuid(), 49321, 777, "fp", 1L);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(foreign), Intended(), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Contains("browser", move.Audit, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Own_Claim_With_A_Different_Port_Is_Replaced()
        {
            // Same pid, different port: this process's own stale leftover (recreated browser bound
            // a new port) - overwrite rather than join our own dead record.
            var ownStale = new ListenerClaim(BrowserId, 49321, 1000, "fp", 1L);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(ownStale), Intended(port: 50111), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Contains("own earlier claim", move.Audit);
        }

        [Fact]
        public void Join_Fingerprint_Mismatch_Is_Reported_Not_Silently_Accepted()
        {
            var other = new ListenerClaim(BrowserId, 49321, 777, "fp-other-config", 1L);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(other), Intended(fingerprint: "fp"), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.Join, move.Step);
            Assert.False(move.JoinFingerprintMatches);
        }

        // ---- #96: generation (epoch) rules the handoff rides -------------------------------------

        [Fact]
        public void ClaimNew_Has_No_Generated_Epoch_The_Caller_Floor_Governs()
        {
            var move = ListenerClaimCore.Decide(ListenerClaimRead.NotFound(), Intended(), Alive, PortOk);

            Assert.Null(move.NewEpoch);
        }

        [Fact]
        public void Untrusted_Slot_Has_No_Knowable_Previous_Generation()
        {
            var move = ListenerClaimCore.Decide(ListenerClaim.Read("garbage"), Intended(), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Null(move.NewEpoch);
        }

        [Fact]
        public void Taking_Over_A_Dead_Owner_Publishes_The_Next_Generation()
        {
            // THE #96 handoff rule: the replacement claim names the displaced claim's epoch + 1,
            // so every process that joined the dead owner notices the change from its own watch.
            var deadOwner = new ListenerClaim(BrowserId, 49321, 777, "fp", 1L, 4);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(deadOwner), Intended(), pid => false, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Equal(5, move.NewEpoch);
        }

        [Fact]
        public void Taking_Over_A_Refusing_Port_Publishes_The_Next_Generation()
        {
            var deadPort = new ListenerClaim(BrowserId, 49321, 777, "fp", 1L, 9);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(deadPort), Intended(), Alive, port => false);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Equal(10, move.NewEpoch);
        }

        [Fact]
        public void Replacing_Own_Earlier_Claim_Also_Advances_The_Generation()
        {
            var ownStale = new ListenerClaim(BrowserId, 49321, 1000, "fp", 1L, 3);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(ownStale), Intended(port: 50111), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Equal(4, move.NewEpoch);
        }

        [Fact]
        public void Cross_Browser_Id_Litter_Takeover_Keeps_The_Generation_Monotone()
        {
            var foreign = new ListenerClaim(Guid.NewGuid(), 49321, 777, "fp", 1L, 6);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(foreign), Intended(), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.ClaimOverStale, move.Step);
            Assert.Equal(7, move.NewEpoch);
        }

        [Fact]
        public void Join_Hands_Back_The_Owner_Record_With_Its_Generation()
        {
            var live = new ListenerClaim(BrowserId, 49321, 777, "fp", 1L, 12);

            var move = ListenerClaimCore.Decide(ListenerClaimRead.Trusted(live), Intended(), Alive, PortOk);

            Assert.Equal(ListenerClaimStep.Join, move.Step);
            Assert.Equal(12, move.JoinClaim.Epoch);
        }
    }
}
