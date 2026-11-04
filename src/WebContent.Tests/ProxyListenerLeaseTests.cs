using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #95: the consult-then-claim-or-join wiring seam between proxied browser creation and
    /// the #93 claim protocol, over REAL files in a temp directory with injected pids/ports and a
    /// fake port allocator. Every row is the shape the SC-14 live probe witnesses: who owns the
    /// slot, which port the browser arguments must carry, which claim (if any) the process ends up
    /// holding, and exactly which audit lines get journalled. The listener bind itself
    /// (LoopbackProxyListener.Start(port)) is socket code pinned live; the BrowserManager
    /// composition that consumes these outcomes is pinned by <see cref="ListenerJoinWiringGuardTests"/>.
    /// </summary>
    public class ProxyListenerLeaseTests : IDisposable
    {
        private static readonly Guid BrowserId = Guid.Parse("2963db34-185a-4383-84c5-c9f7d780b425");

        private readonly string _dir;
        private readonly string _claimPath;

        public ProxyListenerLeaseTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "webcontent-listener-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _claimPath = BrowserProfileKey.ComposeListenerClaimPath(_dir, "S-1-5-21-test", "vms.test-80", BrowserId);
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

        /// <summary>Provider under test: fake pids alive/dead per the row, ports always answer.</summary>
        private static ProxyListenerLeaseProvider ProviderFor(
            int ownPid,
            int reservedPort,
            Func<int, bool> pidAlive = null,
            Func<int, bool> portAlive = null)
        {
            var store = new ListenerClaimStore(
                ownerPid: ownPid,
                ownerProcessAlive: pidAlive ?? (pid => true),
                loopbackPortConnectable: portAlive ?? (port => true));
            return new ProxyListenerLeaseProvider(store, () => reservedPort, ownerPid: ownPid);
        }

        private string ReadClaimFile()
        {
            return File.ReadAllText(_claimPath);
        }

        [Fact]
        public void No_Claim_Reserves_The_Port_Owns_The_Slot_And_Hands_Back_The_Reserve()
        {
            var lease = ProviderFor(ownPid: 2000, reservedPort: 50999).Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.Owner, lease.Role);
            Assert.Equal(50999, lease.Port);
            Assert.Equal(_claimPath, lease.ClaimPath);
            Assert.Empty(lease.AuditLines);

            var parsed = ListenerClaim.Read(ReadClaimFile());
            Assert.Equal(ListenerClaimReadKind.Trusted, parsed.Kind);
            Assert.Equal(2000, parsed.Claim.OwnerPid);
            Assert.Equal(50999, parsed.Claim.Port);
            Assert.Equal("fp", parsed.Claim.ConfigFingerprint);
        }

        [Fact]
        public void Live_Owner_Joins_On_The_Owners_Port_And_Holds_Nothing()
        {
            ProviderFor(ownPid: 1000, reservedPort: 49321).Acquire(_claimPath, BrowserId, "fp");
            var before = ReadClaimFile();

            var joiner = ProviderFor(ownPid: 2000, reservedPort: 50999).Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.Joined, joiner.Role);
            Assert.Equal(49321, joiner.Port); // the OWNER's port - the argument must be identical
            Assert.Null(joiner.ClaimPath);    // a joiner owns no slot to release
            Assert.Empty(joiner.AuditLines);
            Assert.Equal(before, ReadClaimFile()); // the join never rewrites the slot
        }

        [Fact]
        public void Fingerprint_Mismatch_On_A_Live_Claim_Joins_With_Exactly_One_Audit_And_No_Reclaim()
        {
            // The #95 triage rule: a live owner with a different config posture is JOINED anyway
            // (its listener is the only one this browser could ever reach), and the mismatch is
            // journalled exactly once. Not a refusal, not a re-claim.
            ProviderFor(ownPid: 1000, reservedPort: 49321).Acquire(_claimPath, BrowserId, "fp-owner-build");

            var joiner = ProviderFor(ownPid: 2000, reservedPort: 50999).Acquire(_claimPath, BrowserId, "fp-joiner-build");

            Assert.Equal(ProxyListenerLeaseRole.Joined, joiner.Role);
            Assert.Equal(49321, joiner.Port);
            var audit = Assert.Single(joiner.AuditLines);
            Assert.Contains(_claimPath, audit);
            Assert.Contains("fingerprint differs", audit);
            // Still the owner's slot, untouched.
            Assert.Contains("ownerPid=1000", ReadClaimFile());
            Assert.Contains("configFingerprint=fp-owner-build", ReadClaimFile());
        }

        [Fact]
        public void Dead_Owner_Pid_Is_Taken_Over_With_An_Audit_Naming_The_Slot()
        {
            ProviderFor(ownPid: 1000, reservedPort: 49321).Acquire(_claimPath, BrowserId, "fp");

            var taker = ProviderFor(ownPid: 2000, reservedPort: 50999, pidAlive: pid => false)
                .Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.Owner, taker.Role);
            Assert.Equal(50999, taker.Port);
            var audit = Assert.Single(taker.AuditLines);
            Assert.Contains("no longer running", audit);
            Assert.Contains("ownerPid=2000", ReadClaimFile());
        }

        [Fact]
        public void Refusing_Owner_Port_Is_Taken_Over_With_An_Audit_Naming_The_Slot()
        {
            ProviderFor(ownPid: 1000, reservedPort: 49321).Acquire(_claimPath, BrowserId, "fp");

            var taker = ProviderFor(ownPid: 2000, reservedPort: 50999, portAlive: port => false)
                .Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.Owner, taker.Role);
            Assert.Equal(50999, taker.Port);
            var audit = Assert.Single(taker.AuditLines);
            Assert.Contains("refused a connect probe", audit);
            Assert.Contains("ownerPid=2000", ReadClaimFile());
        }

        [Fact]
        public void Corrupt_Claim_Is_Replaced_And_Audited()
        {
            File.WriteAllText(_claimPath, "this is not a claim");

            var taker = ProviderFor(ownPid: 2000, reservedPort: 50999).Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.Owner, taker.Role);
            var audit = Assert.Single(taker.AuditLines);
            Assert.Contains(_claimPath, audit);
            Assert.Contains("ownerPid=2000", ReadClaimFile());
        }

        [Fact]
        public void Own_Earlier_Claim_On_Another_Port_Is_Replaced_Quietly()
        {
            ProviderFor(ownPid: 2000, reservedPort: 49321).Acquire(_claimPath, BrowserId, "fp");

            var again = ProviderFor(ownPid: 2000, reservedPort: 50999).Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.Owner, again.Role);
            Assert.Equal(50999, again.Port);
            Assert.Equal(_claimPath, again.ClaimPath);
            Assert.Contains("port=50999", ReadClaimFile());
        }

        [Fact]
        public void Unsettled_Protocol_Falls_Back_Unclaimed_With_The_GiveUp_Audit()
        {
            // Slot always reads corrupt and refuses every create: the store gives up inside its
            // bound, and the lease must NEVER turn that into a silent own.
            var provider = new ProxyListenerLeaseProvider(
                new ListenerClaimStore(new HostileClaimFiles(), ownerPid: 2000),
                () => 50999,
                new HostileClaimFiles(),
                ownerPid: 2000);

            var lease = provider.Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.UnclaimedFallback, lease.Role);
            Assert.Equal(0, lease.Port);
            Assert.Null(lease.ClaimPath);
            Assert.Contains(lease.AuditLines, l => l.Contains("did not settle"));
            Assert.False(File.Exists(_claimPath));
        }

        [Fact]
        public void Port_Reservation_Failure_Falls_Back_Without_Touching_The_Slot()
        {
            var provider = new ProxyListenerLeaseProvider(
                new ListenerClaimStore(ownerPid: 2000),
                () => 0,
                ownerPid: 2000);

            var lease = provider.Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.UnclaimedFallback, lease.Role);
            Assert.Equal(0, lease.Port);
            Assert.Null(lease.ClaimPath);
            var audit = Assert.Single(lease.AuditLines);
            Assert.Contains("no ephemeral loopback port could be reserved", audit);
            Assert.False(File.Exists(_claimPath));
        }

        [Fact]
        public void Release_Deletes_The_Owners_Own_Claim()
        {
            var provider = ProviderFor(ownPid: 2000, reservedPort: 50999);
            var lease = provider.Acquire(_claimPath, BrowserId, "fp");

            provider.Release(lease.ClaimPath);

            Assert.False(File.Exists(_claimPath));
        }

        [Fact]
        public void Release_Never_Evicts_Another_Live_Owners_Claim()
        {
            ProviderFor(ownPid: 1000, reservedPort: 49321).Acquire(_claimPath, BrowserId, "fp");
            var before = ReadClaimFile();

            // This process's browser dies late; the slot was taken over by pid 1000 in the meantime.
            ProviderFor(ownPid: 2000, reservedPort: 50999).Release(_claimPath);

            Assert.Equal(before, ReadClaimFile());
        }

        [Fact]
        public void Release_Tolerates_A_Missing_Slot_And_A_Null_Path()
        {
            var provider = ProviderFor(ownPid: 2000, reservedPort: 50999);

            provider.Release(_claimPath); // nothing there
            provider.Release(null);       // joined browser owns nothing

            Assert.False(File.Exists(_claimPath));
        }

        [Fact]
        public void Release_Leaves_A_Foreign_Schema_Slot_Alone()
        {
            // A FUTURE build's live owner (schema this build cannot parse) is not ours to delete.
            File.WriteAllText(_claimPath, "webcontent-listener-claim/99\nbrowserId=" + BrowserId.ToString("D") + "\nport=1\nownerPid=1\nconfigFingerprint=x\nclaimedAtUnixMs=1\n");

            ProviderFor(ownPid: 2000, reservedPort: 50999).Release(_claimPath);

            Assert.True(File.Exists(_claimPath));
        }

        [Fact]
        public void Two_Providers_On_One_Slot_Resolve_To_One_Owner_And_One_Joiner()
        {
            var first = ProviderFor(ownPid: 1000, reservedPort: 49321).Acquire(_claimPath, BrowserId, "fp");
            var second = ProviderFor(ownPid: 2000, reservedPort: 50999).Acquire(_claimPath, BrowserId, "fp");

            Assert.Equal(ProxyListenerLeaseRole.Owner, first.Role);
            Assert.Equal(ProxyListenerLeaseRole.Joined, second.Role);
            Assert.Equal(first.Port, second.Port);

            var slots = Directory.GetFiles(Path.GetDirectoryName(_claimPath), "*.claim");
            Assert.Single(slots);
        }

        /// <summary>Slot that always parses corrupt and never lets a create win - the give-up row's world.</summary>
        private sealed class HostileClaimFiles : IListenerClaimFiles
        {
            public ListenerClaimFileRead Read(string claimPath)
            {
                return ListenerClaimFileRead.Found("hostile garbage that is never a claim");
            }

            public bool TryCreateExclusive(string claimPath, string content)
            {
                return false;
            }

            public bool TryDelete(string claimPath)
            {
                return true;
            }
        }
    }
}
