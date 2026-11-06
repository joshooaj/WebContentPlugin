using System;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// The pure decision table of the #94 disk sweep (acceptance rows: fresh folder keep, stale
    /// unreaped reap, locked-folder skip, dead-owner claim reap, live-owner claim keep) with NO
    /// filesystem and NO process: the IO seam (<see cref="ProfileSweepDiskTests"/>) supplies the
    /// facts, this class pins what they mean. Claim staleness rows deliberately exercise the
    /// SHARED #93 seam (via ProfileSweepCore.DecideClaimFile -> ListenerClaimCore.Decide), so
    /// sweep and claim protocol cannot drift apart.
    /// </summary>
    public class ProfileSweepCoreTests
    {
        private static readonly Guid BrowserId = Guid.Parse("2963db34-185a-4383-84c5-c9f7d780b425");
        private static readonly Guid OtherBrowserId = Guid.Parse("9f55098a-7d87-4d1e-842e-f1d9bc41e633");

        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

        private static DateTimeOffset Days(int days) => Now.AddDays(-days);

        // ---- shape recognition ----

        [Fact]
        public void Shape_Predicates_Recognize_Only_The_Plant_Own_Proxied_Classes()
        {
            Assert.True(ProfileSweepCore.IsProxiedFolderName("proxied-" + BrowserId.ToString("D")));
            Assert.True(ProfileSweepCore.IsProxiedFolderName("PROXIED-" + BrowserId.ToString("D")));
            Assert.False(ProfileSweepCore.IsProxiedFolderName("server-vms.test-80"));
            Assert.False(ProfileSweepCore.IsProxiedFolderName("EBWebView"));
            Assert.False(ProfileSweepCore.IsProxiedFolderName(null));

            Assert.True(ProfileSweepCore.IsServerFolderName("server-vms.test-80"));
            Assert.False(ProfileSweepCore.IsServerFolderName("proxied-" + BrowserId.ToString("D")));
            Assert.False(ProfileSweepCore.IsServerFolderName(null));
        }

        [Fact]
        public void Claim_Name_Shapes_Separate_The_Slot_From_Its_Atomic_Create_Litter()
        {
            var claim = "proxied-" + BrowserId.ToString("D") + BrowserProfileKey.ListenerClaimExtension;
            var litter = claim + ".tmp-1234-deadbeef";

            Assert.True(ProfileSweepCore.IsListenerClaimName(claim));
            Assert.False(ProfileSweepCore.IsListenerClaimName(litter)); // litter is its own class
            Assert.False(ProfileSweepCore.IsListenerClaimName("somethingelse.claim"));
            Assert.False(ProfileSweepCore.IsListenerClaimName("proxied-x.claimx"));

            Assert.True(ProfileSweepCore.IsListenerClaimTempName(litter));
            Assert.False(ProfileSweepCore.IsListenerClaimTempName(claim));
        }

        [Fact]
        public void Browser_Id_Is_Parsed_From_Both_Folder_And_Claim_Names_And_Nothing_Else()
        {
            Assert.Equal(BrowserId, ProfileSweepCore.TryParseProxiedLeafBrowserId("proxied-" + BrowserId.ToString("D")));
            Assert.Equal(BrowserId, ProfileSweepCore.TryParseProxiedLeafBrowserId("proxied-" + BrowserId.ToString("D") + ".claim"));
            Assert.Equal(Guid.Empty, ProfileSweepCore.TryParseProxiedLeafBrowserId("proxied-not-a-guid"));
            Assert.Equal(Guid.Empty, ProfileSweepCore.TryParseProxiedLeafBrowserId("server-vms.test-80"));
            Assert.Equal(Guid.Empty, ProfileSweepCore.TryParseProxiedLeafBrowserId(null));
        }

        // ---- folder table: fresh keep, stale reap, locked skip ----

        [Fact]
        public void Fresh_Folder_Is_Quietly_Kept()
        {
            var move = ProfileSweepCore.DecideFolder(Days(1), false, Now, Retention);

            Assert.Equal(ProxiedFolderStep.Keep, move.Step);
            Assert.Null(move.Audit);
        }

        [Fact]
        public void Stale_Unlocked_Folder_Is_Reaped_With_Its_Age_Named()
        {
            var move = ProfileSweepCore.DecideFolder(Days(31), false, Now, Retention);

            Assert.Equal(ProxiedFolderStep.Reap, move.Step);
            Assert.Contains("31 day(s)", move.Audit);
            Assert.Contains("30 day(s)", move.Audit);
        }

        [Fact]
        public void Locked_Folder_Is_Skipped_Whatever_Its_Age_Never_Forced()
        {
            var stale = ProfileSweepCore.DecideFolder(Days(90), true, Now, Retention);
            var fresh = ProfileSweepCore.DecideFolder(Days(1), true, Now, Retention);

            Assert.Equal(ProxiedFolderStep.SkipLocked, stale.Step);
            Assert.Equal(ProxiedFolderStep.SkipLocked, fresh.Step);
            Assert.Contains("never forced", stale.Audit);
        }

        [Fact]
        public void Unknown_Activity_Is_Never_Evidence_Of_Staleness()
        {
            var move = ProfileSweepCore.DecideFolder(null, false, Now, Retention);

            Assert.Equal(ProxiedFolderStep.Keep, move.Step);
            Assert.Contains("could not be established", move.Audit);
        }

        [Fact]
        public void Activity_Exactly_On_The_Retention_Boundary_Is_Stale()
        {
            var move = ProfileSweepCore.DecideFolder(Now - Retention, false, Now, Retention);

            Assert.Equal(ProxiedFolderStep.Reap, move.Step);
        }

        [Fact]
        public void A_Future_Timestamp_Is_Kept_Not_Crashed_On()
        {
            var move = ProfileSweepCore.DecideFolder(Now.AddDays(5), false, Now, Retention);

            Assert.Equal(ProxiedFolderStep.Keep, move.Step);
        }

        // ---- claim table: dead-owner reap, live-owner keep (through the shared #93 seam) ----

        private static ListenerClaimRead TrustedClaim(int ownerPid, int port, Guid browserId = default)
        {
            var claim = new ListenerClaim(browserId == default ? BrowserId : browserId, port, ownerPid, "fp", 1);
            return ListenerClaim.Read(claim.Serialize());
        }

        private static bool AlwaysAlive(int pid) => true;

        private static bool AlwaysConnectable(int port) => true;

        [Fact]
        public void Live_Owner_Claim_Is_Kept()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                TrustedClaim(1000, 49321), BrowserId, selfPid: 4242, siblingProfileFolderExists: true,
                AlwaysAlive, AlwaysConnectable);

            Assert.Equal(ClaimFileStep.Keep, move.Step);
            Assert.Null(move.Audit);
        }

        [Fact]
        public void Dead_Owner_Claim_Is_Reaped_Naming_The_Shared_Staleness_Reason()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                TrustedClaim(1000, 49321), BrowserId, selfPid: 4242, siblingProfileFolderExists: true,
                pid => false, AlwaysConnectable);

            Assert.Equal(ClaimFileStep.Reap, move.Step);
            Assert.Contains("no longer running", move.Audit);
        }

        [Fact]
        public void Refusing_Owner_Port_Claim_Is_Reaped_Even_With_A_Live_Pid()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                TrustedClaim(1000, 49321), BrowserId, selfPid: 4242, siblingProfileFolderExists: true,
                AlwaysAlive, port => false);

            Assert.Equal(ClaimFileStep.Reap, move.Step);
            Assert.Contains("connect probe", move.Audit);
        }

        [Fact]
        public void Corrupt_Claim_Is_Never_Trusted_And_Is_Reaped()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                ListenerClaim.Read("i am not a claim at all"), BrowserId, selfPid: 4242, siblingProfileFolderExists: true,
                AlwaysAlive, AlwaysConnectable);

            Assert.Equal(ClaimFileStep.Reap, move.Step);
            Assert.Contains("schema header", move.Audit);
        }

        [Fact]
        public void Foreign_Schema_Claim_Is_Reaped_As_Untrusted()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                ListenerClaim.Read("webcontent-listener-claim/9\nbrowserId=" + BrowserId.ToString("D") + "\n"),
                BrowserId, selfPid: 4242, siblingProfileFolderExists: true,
                AlwaysAlive, AlwaysConnectable);

            Assert.Equal(ClaimFileStep.Reap, move.Step);
            Assert.Contains("version", move.Audit);
        }

        [Fact]
        public void Claim_Naming_A_Different_Browser_Than_Its_Name_Is_Reaped_Even_When_Live()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                TrustedClaim(1000, 49321, OtherBrowserId), BrowserId, selfPid: 4242, siblingProfileFolderExists: true,
                AlwaysAlive, AlwaysConnectable);

            Assert.Equal(ClaimFileStep.Reap, move.Step);
            Assert.Contains("names browser", move.Audit);
        }

        [Fact]
        public void This_Process_Own_Claim_Is_Never_Reaped_Even_If_The_Probes_Call_It_Dead()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                TrustedClaim(4242, 49321), BrowserId, selfPid: 4242, siblingProfileFolderExists: true,
                pid => false, port => false);

            Assert.Equal(ClaimFileStep.Keep, move.Step);
            Assert.Contains("own live claim", move.Audit);
        }

        [Fact]
        public void Claim_Whose_Sibling_Profile_Folder_Is_Gone_Is_Reaped_Even_If_It_Presents_Live()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                TrustedClaim(1000, 49321), BrowserId, selfPid: 4242, siblingProfileFolderExists: false,
                AlwaysAlive, AlwaysConnectable);

            Assert.Equal(ClaimFileStep.Reap, move.Step);
            Assert.Contains("sibling profile folder is gone", move.Audit);
        }

        [Fact]
        public void Claim_That_Vanished_Between_Enumeration_And_Read_Is_Quietly_Dropped()
        {
            var move = ProfileSweepCore.DecideClaimFile(
                ListenerClaimRead.NotFound(), BrowserId, selfPid: 4242, siblingProfileFolderExists: true,
                AlwaysAlive, AlwaysConnectable);

            Assert.Equal(ClaimFileStep.Keep, move.Step);
            Assert.Contains("vanished", move.Audit);
        }

        // ---- temp-litter table ----

        [Fact]
        public void Old_Claim_Create_Litter_Is_Reaped_Young_Litter_Is_Kept()
        {
            var litter = TimeSpan.FromHours(1);

            var old = ProfileSweepCore.DecideClaimTemp(Days(0).AddHours(-3), Now, litter);
            var young = ProfileSweepCore.DecideClaimTemp(Now.AddMinutes(-5), Now, litter);

            Assert.Equal(ClaimFileStep.Reap, old.Step);
            Assert.Contains("temp litter", old.Audit);
            Assert.Equal(ClaimFileStep.Keep, young.Step);
        }
    }
}
