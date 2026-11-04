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
            var claim = new ListenerClaim(BrowserId, 49321, 4242, "fp-abc-123", 1762000000123L);

            var read = ListenerClaim.Read(claim.Serialize());

            Assert.Equal(ListenerClaimReadKind.Trusted, read.Kind);
            Assert.Equal(BrowserId, read.Claim.BrowserId);
            Assert.Equal(49321, read.Claim.Port);
            Assert.Equal(4242, read.Claim.OwnerPid);
            Assert.Equal("fp-abc-123", read.Claim.ConfigFingerprint);
            Assert.Equal(1762000000123L, read.Claim.ClaimedAtUnixMs);
        }

        [Fact]
        public void Foreign_Schema_Version_Is_Untrusted_Never_A_Crash()
        {
            // A future build's version: an old reader must treat it as untrusted, never brick.
            var future = "webcontent-listener-claim/2\nbrowserId=" + BrowserId.ToString("D") +
                         "\nport=49321\nownerPid=4242\nconfigFingerprint=fp\nclaimedAtUnixMs=1\n";

            var read = ListenerClaim.Read(future);

            Assert.Equal(ListenerClaimReadKind.Untrusted, read.Kind);
            Assert.Contains("version", read.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Unknown_Future_Fields_In_The_Current_Version_Are_Tolerated()
        {
            // The version header is the compatibility gate; extra fields inside version 1 must not
            // brick the reader (that is what "future fields don't brick old readers" means).
            var withFutureField = "webcontent-listener-claim/1\nbrowserId=" + BrowserId.ToString("D") +
                                  "\nport=49321\nownerPid=4242\nconfigFingerprint=fp\nclaimedAtUnixMs=1\nepoch=7\n";

            var read = ListenerClaim.Read(withFutureField);

            Assert.Equal(ListenerClaimReadKind.Trusted, read.Kind);
        }

        [Fact]
        public void Corrupt_Content_Is_Untrusted_With_A_Reason()
        {
            Assert.Equal(ListenerClaimReadKind.Untrusted, ListenerClaim.Read("this is not a claim").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted, ListenerClaim.Read(string.Empty).Kind);

            var missingField = "webcontent-listener-claim/1\nport=49321\nownerPid=4242\n";
            var read = ListenerClaim.Read(missingField);
            Assert.Equal(ListenerClaimReadKind.Untrusted, read.Kind);
            Assert.Contains("missing", read.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Malformed_Values_Are_Untrusted()
        {
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/1\nbrowserId=not-a-guid\nport=1\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/1\nbrowserId=" + BrowserId.ToString("D") + "\nport=99999\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/1\nbrowserId=" + BrowserId.ToString("D") + "\nport=-5\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/1\nbrowserId=" + BrowserId.ToString("D") + "\nport=1\nownerPid=0\nconfigFingerprint=x\nclaimedAtUnixMs=0\n").Kind);
            Assert.Equal(ListenerClaimReadKind.Untrusted,
                ListenerClaim.Read("webcontent-listener-claim/1\nbrowserId=" + BrowserId.ToString("D") + "\nport=1\nport=2\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=0\n").Kind);
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
    }
}
