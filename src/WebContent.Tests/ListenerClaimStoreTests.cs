using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// File-backed tests for the listener-claim store (ADR-0004 point 3, #93): the full
    /// claim/join/stale-takeover protocol over REAL files in a temp directory, with the two
    /// liveness probes injected (the process and socket checks themselves are exercised by
    /// <see cref="Production_Probe_Smoke_Rows"/>). The race row hammers the atomic create from
    /// two would-be owners concurrently and requires exactly one file to survive the winner's
    /// record with the loser joining it.
    /// </summary>
    public class ListenerClaimStoreTests : IDisposable
    {
        private static readonly Guid BrowserId = Guid.Parse("2963db34-185a-4383-84c5-c9f7d780b425");

        private readonly string _dir;
        private readonly string _claimPath;

        public ListenerClaimStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "webcontent-listener-claim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _claimPath = BrowserProfileKey.ComposeListenerClaimPath(_dir, "S-1-5-21-test", "vms.test-80", BrowserId);

            // The boundary dir under the slot is created lazily by the store's atomic create; the
            // corrupt/foreign rows pre-seed the slot themselves, so give them the parent first.
            Directory.CreateDirectory(Path.GetDirectoryName(_claimPath));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>Store under test: fake pids are alive/dead per the row, ports always answer.</summary>
        private static ListenerClaimStore StoreFor(int ownPid, Func<int, bool> pidAlive = null, Func<int, bool> portAlive = null)
        {
            return new ListenerClaimStore(
                ownerPid: ownPid,
                ownerProcessAlive: pidAlive ?? (pid => true),
                loopbackPortConnectable: portAlive ?? (port => true));
        }

        private string ReadClaimFile()
        {
            return File.ReadAllText(_claimPath);
        }

        [Fact]
        public void No_Claim_Becomes_Owner_And_Writes_The_Claim_File()
        {
            var store = StoreFor(ownPid: 1000);

            var attempt = store.Claim(_claimPath, BrowserId, 49321, "fp");

            Assert.Equal(ListenerClaimOutcome.Owner, attempt.Outcome);
            Assert.Empty(attempt.AuditLines);
            Assert.True(File.Exists(_claimPath));

            var parsed = ListenerClaim.Read(ReadClaimFile());
            Assert.Equal(ListenerClaimReadKind.Trusted, parsed.Kind);
            Assert.Equal(1000, parsed.Claim.OwnerPid);
            Assert.Equal(49321, parsed.Claim.Port);
        }

        [Fact]
        public void Live_Owner_Claim_Is_Joined_By_A_Second_Process()
        {
            StoreFor(ownPid: 1000).Claim(_claimPath, BrowserId, 49321, "fp");

            var joiner = StoreFor(ownPid: 2000).Claim(_claimPath, BrowserId, 50999, "fp");

            Assert.Equal(ListenerClaimOutcome.Joined, joiner.Outcome);
            Assert.Equal(49321, joiner.Claim.Port);
            Assert.Empty(joiner.AuditLines);
            // The owner's file is untouched - a join never rewrites the slot.
            Assert.Contains("ownerPid=1000", ReadClaimFile());
        }

        [Fact]
        public void Dead_Owner_Pid_Is_Replaced_Stale_By_Pid()
        {
            StoreFor(ownPid: 1000).Claim(_claimPath, BrowserId, 49321, "fp");

            var taker = StoreFor(ownPid: 2000, pidAlive: pid => false)
                .Claim(_claimPath, BrowserId, 50999, "fp");

            Assert.Equal(ListenerClaimOutcome.Owner, taker.Outcome);
            var audit = Assert.Single(taker.AuditLines);
            Assert.Contains(_claimPath, audit);
            Assert.Contains("no longer running", audit);
            Assert.Contains("ownerPid=2000", ReadClaimFile());
            Assert.Contains("port=50999", ReadClaimFile());
        }

        [Fact]
        public void Refusing_Owner_Port_Is_Replaced_Stale_By_Port()
        {
            StoreFor(ownPid: 1000).Claim(_claimPath, BrowserId, 49321, "fp");

            var taker = StoreFor(ownPid: 2000, pidAlive: pid => true, portAlive: port => false)
                .Claim(_claimPath, BrowserId, 50999, "fp");

            Assert.Equal(ListenerClaimOutcome.Owner, taker.Outcome);
            Assert.Contains("connect probe", taker.AuditLines.Single());
            Assert.Contains("ownerPid=2000", ReadClaimFile());
        }

        [Fact]
        public void Corrupt_Claim_File_Is_Never_Trusted_And_Is_Replaced_With_An_Audit_Line()
        {
            File.WriteAllText(_claimPath, "i am not a claim at all");

            var taker = StoreFor(ownPid: 2000).Claim(_claimPath, BrowserId, 49321, "fp");

            Assert.Equal(ListenerClaimOutcome.Owner, taker.Outcome);
            Assert.Contains(_claimPath, taker.AuditLines.Single());
            Assert.Contains("ownerPid=2000", ReadClaimFile());
        }

        [Fact]
        public void Foreign_Schema_Claim_File_Is_Never_Trusted_And_Is_Replaced_With_An_Audit_Line()
        {
            File.WriteAllText(_claimPath,
                "webcontent-listener-claim/3\nbrowserId=" + BrowserId.ToString("D") +
                "\nport=49321\nownerPid=777\nconfigFingerprint=fp\nclaimedAtUnixMs=1\nepoch=1\n");

            var taker = StoreFor(ownPid: 2000).Claim(_claimPath, BrowserId, 49321, "fp");

            Assert.Equal(ListenerClaimOutcome.Owner, taker.Outcome);
            Assert.Contains("version", taker.AuditLines.Single(), StringComparison.OrdinalIgnoreCase);
        }

        // ---- #96: the generation the handoff rides, end to end over real files ---------------------

        [Fact]
        public void First_Claim_Of_A_Boundary_Is_Generation_One()
        {
            StoreFor(ownPid: 1000).Claim(_claimPath, BrowserId, 49321, "fp");

            var parsed = ListenerClaim.Read(ReadClaimFile());
            Assert.Equal(1, parsed.Claim.Epoch);
        }

        [Fact]
        public void Owner_Death_Takeover_Publishes_Previous_Epoch_Plus_One_On_Disk()
        {
            // The #96 handoff witness on real files: the displaced generation is one lower than
            // what the survivor writes, and the survivor's record hands the new epoch back.
            StoreFor(ownPid: 1000).Claim(_claimPath, BrowserId, 49321, "fp");
            var displaced = ListenerClaim.Read(ReadClaimFile()).Claim.Epoch;

            var survivor = StoreFor(ownPid: 2000, pidAlive: pid => pid != 1000)
                .Claim(_claimPath, BrowserId, 50999, "fp", floorEpoch: displaced);

            Assert.Equal(ListenerClaimOutcome.Owner, survivor.Outcome);
            Assert.Equal(displaced + 1, survivor.Claim.Epoch);
            Assert.Contains("epoch=" + (displaced + 1), ReadClaimFile());
        }

        [Fact]
        public void A_Known_Floor_Keeps_The_Generation_Monotone_Across_A_Corrupt_Handoff()
        {
            // Corrupt content erases the on-disk epoch, but a watcher that REMEMBERS generation 5
            // re-claims with a bumped floor, and the floor is what lands on disk - a joiner that
            // saw 5 always notices the 6.
            File.WriteAllText(_claimPath, "scrambled slot");

            var taker = StoreFor(ownPid: 2000).Claim(_claimPath, BrowserId, 50999, "fp", floorEpoch: 6);

            Assert.Equal(ListenerClaimOutcome.Owner, taker.Outcome);
            Assert.Equal(6, ListenerClaim.Read(ReadClaimFile()).Claim.Epoch);
        }

        [Fact]
        public void Floor_Never_Regresses_A_Known_Higher_On_Disk_Epoch()
        {
            // A stale floor (a watcher that lost track) must not reset the boundary's history:
            // the parsed epoch + 1 outranks it.
            File.WriteAllText(_claimPath,
                new ListenerClaim(BrowserId, 49321, 777, "fp", 1, 9).Serialize());

            var taker = StoreFor(ownPid: 2000, pidAlive: pid => pid != 777)
                .Claim(_claimPath, BrowserId, 50999, "fp", floorEpoch: 1);

            Assert.Equal(ListenerClaimOutcome.Owner, taker.Outcome);
            Assert.Equal(10, ListenerClaim.Read(ReadClaimFile()).Claim.Epoch);
        }

        [Fact]
        public void Idempotent_Self_Reclaim_Reports_The_On_Disk_Generation_Not_A_New_One()
        {
            var store = StoreFor(ownPid: 1000);
            store.Claim(_claimPath, BrowserId, 49321, "fp");

            var again = store.Claim(_claimPath, BrowserId, 49321, "fp");

            Assert.Equal(ListenerClaimOutcome.Owner, again.Outcome);
            Assert.Equal(1, again.Claim.Epoch);
            Assert.Equal(1, ListenerClaim.Read(ReadClaimFile()).Claim.Epoch);
        }

        [Fact]
        public void Own_Idempotent_Reclaim_Comes_Back_As_Owner_Not_Join()
        {
            var store = StoreFor(ownPid: 1000);
            store.Claim(_claimPath, BrowserId, 49321, "fp");

            var again = store.Claim(_claimPath, BrowserId, 49321, "fp");

            Assert.Equal(ListenerClaimOutcome.Owner, again.Outcome);
        }

        [Fact]
        public async Task Two_Concurrent_Claimers_Resolve_To_Exactly_One_Owner_With_The_Loser_Joining()
        {
            // The acceptance race: two processes hit the empty slot at (as close to) the same
            // instant as the filesystem allows. Exactly one atomic create wins; the loser
            // re-reads and joins the winner's port. One file survives, naming one owner.
            var start = new ManualResetEventSlim(false);
            var a = Task.Run(() =>
            {
                start.Wait();
                return StoreFor(ownPid: 1000).Claim(_claimPath, BrowserId, 49321, "fp");
            });
            var b = Task.Run(() =>
            {
                start.Wait();
                return StoreFor(ownPid: 2000).Claim(_claimPath, BrowserId, 50999, "fp");
            });

            start.Set();
            var attemptA = await a;
            var attemptB = await b;

            var outcomes = new[] { attemptA.Outcome, attemptB.Outcome };
            Assert.Equal(1, outcomes.Count(o => o == ListenerClaimOutcome.Owner));
            Assert.Equal(1, outcomes.Count(o => o == ListenerClaimOutcome.Joined));

            var winner = attemptA.Outcome == ListenerClaimOutcome.Owner ? attemptA : attemptB;
            var loser = attemptA.Outcome == ListenerClaimOutcome.Owner ? attemptB : attemptA;
            Assert.Equal(winner.Claim.Port, loser.Claim.Port);

            // Exactly ONE claim file under the slot's directory, and it parses to the winner.
            var claimFiles = Directory.GetFiles(_dir, "*" + BrowserProfileKey.ListenerClaimExtension, SearchOption.AllDirectories);
            Assert.Single(claimFiles);
            var survivor = ListenerClaim.Read(File.ReadAllText(claimFiles[0]));
            Assert.Equal(ListenerClaimReadKind.Trusted, survivor.Kind);
            Assert.Equal(winner.Claim.OwnerPid, survivor.Claim.OwnerPid);
        }

        [Fact]
        public async Task Concurrent_Attempts_On_A_Live_Owner_All_Join_Without_Touching_The_Slot()
        {
            StoreFor(ownPid: 1000).Claim(_claimPath, BrowserId, 49321, "fp");
            var ownerBytes = File.ReadAllBytes(_claimPath);

            var joiners = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
                StoreFor(ownPid: 2000 + i).Claim(_claimPath, BrowserId, 50999 + i, "fp"))).ToArray();
            var results = await Task.WhenAll(joiners);

            Assert.All(results, result =>
            {
                Assert.Equal(ListenerClaimOutcome.Joined, result.Outcome);
                Assert.Equal(49321, result.Claim.Port);
            });
            Assert.Equal(ownerBytes, File.ReadAllBytes(_claimPath));
        }

        [Fact]
        public void Path_Composition_Puts_The_Claim_Beside_The_Profile_Folder()
        {
            var profile = BrowserProfileKey.ComposeProxiedProfilePath(_dir, "S-1-5-21-test", "vms.test-80", BrowserId);
            var claim = BrowserProfileKey.ComposeListenerClaimPath(_dir, "S-1-5-21-test", "vms.test-80", BrowserId);

            // Sibling in the server-<leg> dir: same parent, one extension apart, never inside the profile.
            Assert.Equal(Path.GetDirectoryName(profile), Path.GetDirectoryName(claim));
            Assert.Equal(Path.GetFileName(profile) + BrowserProfileKey.ListenerClaimExtension, Path.GetFileName(claim));
        }

        [Fact]
        public void Production_Pid_Probe_Sees_This_Process_And_Not_A_Ghost()
        {
            Assert.True(ListenerClaimStore.ProcessExists(System.Diagnostics.Process.GetCurrentProcess().Id));
            Assert.False(ListenerClaimStore.ProcessExists(int.MaxValue));
        }

        [Fact]
        public void Production_Port_Probe_Distinguishes_A_Real_Listener_From_A_Dead_Port()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                Assert.True(ListenerClaimStore.LoopbackPortConnectable(port));

                // Nothing listens on 1: a connect is refused, which IS the stale-by-port signal.
                Assert.False(ListenerClaimStore.LoopbackPortConnectable(1));
                Assert.False(ListenerClaimStore.LoopbackPortConnectable(-5));
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
