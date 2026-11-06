using System;
using System.IO;
using System.Linq;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// The <see cref="ProfileSweep"/> IO seam over REAL files in a temp directory: the walk only
    /// touches the plugin's proxied shape classes at both the boundary (server-&lt;leg&gt;) and the
    /// pre-#92 old shape, deletes what the pure decision table says to reap (each deletion
    /// journalled exactly once), skips what it must (locked folders, live claims, fresh
    /// anything, the non-proxied EBWebView tree), and survives hostile content without throwing.
    /// All liveness is injected: no processes, no sockets, no Chromium here.
    /// </summary>
    public class ProfileSweepDiskTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
        private static readonly TimeSpan LitterRetention = TimeSpan.FromHours(1);
        private const int SelfPid = 4242;

        private static readonly Guid FreshId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid StaleId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        private static readonly Guid LockedId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
        private static readonly Guid OldShapeId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
        private static readonly Guid LiveClaimId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");
        private static readonly Guid DeadClaimId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006");
        private static readonly Guid ClaimWithoutSiblingId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000007");
        private static readonly Guid CorruptClaimId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000008");
        private static readonly Guid StaleFolderWithClaimId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009");
        private static readonly Guid OtherBrowserId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");

        private readonly string _root;
        private readonly string _userDir;
        private readonly string _serverDir;
        private string _lockedFolder;

        public ProfileSweepDiskTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "webcontent-profile-sweep-" + Guid.NewGuid().ToString("N"));
            _userDir = Path.Combine(_root, "S-1-5-21-test");
            _serverDir = Path.Combine(_userDir, "server-vms.test-80");
            Directory.CreateDirectory(_serverDir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        // ---- fixtures ----

        private string CreateProxiedProfile(string parent, Guid id, DateTime markerUtc)
        {
            var folder = Path.Combine(parent, "proxied-" + id.ToString("D"));
            var web = Path.Combine(folder, "EBWebView");
            var profile = Path.Combine(web, "Default");
            Directory.CreateDirectory(profile);
            var file = Path.Combine(web, "Local State");
            File.WriteAllText(file, "chatter");

            // Stamp leaf-first: a parent stamped afterwards keeps the stamp (the walk checks
            // every entry's timestamp, so all of them must read as this marker).
            File.SetLastWriteTimeUtc(file, markerUtc);
            Directory.SetLastWriteTimeUtc(profile, markerUtc);
            Directory.SetLastWriteTimeUtc(web, markerUtc);
            Directory.SetLastWriteTimeUtc(folder, markerUtc);
            return folder;
        }

        private string WriteClaim(Guid folderStemId, string content)
        {
            var path = Path.Combine(_serverDir, "proxied-" + folderStemId.ToString("D") + BrowserProfileKey.ListenerClaimExtension);
            File.WriteAllText(path, content);
            return path;
        }

        private static string TrustedClaimContent(Guid browserId, int ownerPid, int port)
        {
            return new ListenerClaim(browserId, port, ownerPid, "fp", 1).Serialize();
        }

        /// <summary>pid 1001 is dead, port 4321 refuses, everything else lives; the locked row's folder is lock-held.</summary>
        private ProfileSweep SweepFor()
        {
            return new ProfileSweep(
                ownerProcessAlive: pid => pid != 1001,
                loopbackPortConnectable: port => port != 4321,
                browserLockHeld: folder => string.Equals(folder, _lockedFolder, StringComparison.OrdinalIgnoreCase),
                selfPid: SelfPid);
        }

        static string Dump(ProfileSweepReport report) =>
            "seen=" + report.ProxiedFoldersSeen + " reaped=" + report.ProxiedFoldersReaped +
            " skipped=" + report.ProxiedFoldersSkippedLocked + " claims=" + report.ClaimFilesReaped +
            " lines[" + string.Join(" || ", report.LogLines) + "]";

        // ---- folder rows over the boundary shape ----

        [Fact]
        public void Fresh_Stale_Locked_Rows_Keep_Reap_And_Skip_On_Real_Files()
        {
            var nowUtc = Now.UtcDateTime;
            var fresh = CreateProxiedProfile(_serverDir, FreshId, nowUtc.AddDays(-1));
            var stale = CreateProxiedProfile(_serverDir, StaleId, nowUtc.AddDays(-31));
            _lockedFolder = CreateProxiedProfile(_serverDir, LockedId, nowUtc.AddDays(-45));

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.True(Directory.Exists(fresh), "the in-retention folder must survive");
            Assert.False(Directory.Exists(stale), "the 31-day-orphan folder must be reaped: " + Dump(report));
            Assert.True(Directory.Exists(_lockedFolder), "the lock-held folder is never forced");

            Assert.Equal(3, report.ProxiedFoldersSeen);
            Assert.Equal(1, report.ProxiedFoldersReaped);
            Assert.Equal(1, report.ProxiedFoldersSkippedLocked);

            // Each deletion logged exactly once; the skip journalled too.
            Assert.Single(report.LogLines, line => line.Contains("Swept orphaned proxied browser profile '" + stale + "'"));
            Assert.Single(report.LogLines, line => line.Contains("Skipped locked proxied browser profile '" + _lockedFolder + "'"));
            Assert.DoesNotContain(report.LogLines, line => line.Contains(fresh, StringComparison.Ordinal));
        }

        [Fact]
        public void Old_Shape_Folders_Directly_Under_The_User_Key_Are_Reaped_Too()
        {
            // The #92 handoff: no migration, the pre-#92 <user>\proxied-* shape simply becomes sweep orphans.
            var oldShape = CreateProxiedProfile(_userDir, OldShapeId, Now.UtcDateTime.AddDays(-40));

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.False(Directory.Exists(oldShape), "old-shape folder survived: " + Dump(report));
            Assert.Single(report.LogLines, line => line.Contains("Swept orphaned proxied browser profile '" + oldShape + "'"));
        }

        [Fact]
        public void The_Nonproxied_Ebwebview_Tree_Is_Never_Touched_However_Old()
        {
            var web = Path.Combine(_userDir, "EBWebView");
            var deep = Path.Combine(web, "Default", "OldCache");
            Directory.CreateDirectory(deep);
            var file = Path.Combine(deep, "ancient.txt");
            File.WriteAllText(file, "do not delete me");
            File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddDays(-365));
            Directory.SetLastWriteTimeUtc(deep, Now.UtcDateTime.AddDays(-365));
            Directory.SetLastWriteTimeUtc(web, Now.UtcDateTime.AddDays(-365));
            Directory.SetLastWriteTimeUtc(_userDir, Now.UtcDateTime.AddDays(-365));

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.True(File.Exists(file));
            Assert.Equal(0, report.ProxiedFoldersReaped);
        }

        [Fact]
        public void A_Root_Level_Boundary_Dir_Is_Walked_Without_Any_User_Key_Layer()
        {
            // Identity-unresolvable fallback (#92: missing-user keeps server separation at root level).
            var rootServer = Path.Combine(_root, "server-unknown");
            var stale = CreateProxiedProfile(rootServer, StaleId, Now.UtcDateTime.AddDays(-31));

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.False(Directory.Exists(stale), "root-level stale folder survived: " + Dump(report));
            Assert.Equal(1, report.ProxiedFoldersReaped);
        }

        // ---- claim rows over the boundary shape ----

        [Fact]
        public void Live_Claim_Kept_Dead_Owner_Claim_Reaped_With_The_Shared_Audit()
        {
            // Both siblings are FRESH folders, so only the claim staleness can differ (pid probes).
            CreateProxiedProfile(_serverDir, LiveClaimId, Now.UtcDateTime.AddDays(-1));
            CreateProxiedProfile(_serverDir, DeadClaimId, Now.UtcDateTime.AddDays(-1));
            var liveClaim = WriteClaim(LiveClaimId, TrustedClaimContent(LiveClaimId, 1000, 49321));
            var deadClaim = WriteClaim(DeadClaimId, TrustedClaimContent(DeadClaimId, 1001, 49321));

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.True(File.Exists(liveClaim), "the live-owner claim must survive");
            Assert.False(File.Exists(deadClaim), "the dead-owner claim must be reaped");
            Assert.Contains(report.LogLines, line =>
                line.Contains("Swept listener claim '" + deadClaim + "'") && line.Contains("no longer running"));
            Assert.DoesNotContain(report.LogLines, line => line.Contains(liveClaim, StringComparison.Ordinal));
        }

        [Fact]
        public void Refusing_Port_Claim_Is_Reaped_And_Cross_Id_Claim_Is_Reaped_As_Litter()
        {
            CreateProxiedProfile(_serverDir, LiveClaimId, Now.UtcDateTime.AddDays(-1));
            CreateProxiedProfile(_serverDir, DeadClaimId, Now.UtcDateTime.AddDays(-1));
            var byPort = WriteClaim(LiveClaimId, TrustedClaimContent(LiveClaimId, 1000, 4321)); // port refuses
            var crossId = WriteClaim(DeadClaimId, TrustedClaimContent(OtherBrowserId, 1000, 49321)); // names another browser

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.False(File.Exists(byPort));
            Assert.False(File.Exists(crossId));
            Assert.Contains(report.LogLines, line => line.Contains("connect probe"));
            Assert.Contains(report.LogLines, line => line.Contains("names browser"));
        }

        [Fact]
        public void Claim_Without_Its_Sibling_Profile_Is_Reaped_Corrupt_Claim_Is_Reaped()
        {
            CreateProxiedProfile(_serverDir, CorruptClaimId, Now.UtcDateTime.AddDays(-1));
            var orphanClaim = WriteClaim(ClaimWithoutSiblingId, TrustedClaimContent(ClaimWithoutSiblingId, 1000, 49321));
            var corruptClaim = WriteClaim(CorruptClaimId, "i am not a claim at all");

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.False(File.Exists(orphanClaim));
            Assert.False(File.Exists(corruptClaim));
            Assert.Contains(report.LogLines, line => line.Contains("sibling profile folder is gone"));
            Assert.Contains(report.LogLines, line => line.Contains("schema header"));
        }

        [Fact]
        public void Reaping_A_Stale_Folder_Leaves_Its_Sibling_Claim_Reaped_Through_The_Sibling_Rule()
        {
            var folder = CreateProxiedProfile(_serverDir, StaleFolderWithClaimId, Now.UtcDateTime.AddDays(-31));
            var claim = WriteClaim(StaleFolderWithClaimId, TrustedClaimContent(StaleFolderWithClaimId, 1000, 49321));

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.False(Directory.Exists(folder));
            Assert.False(File.Exists(claim));
            Assert.Equal(2, report.ClaimFilesReaped + report.ProxiedFoldersReaped);
            Assert.Contains(report.LogLines, line => line.Contains("sibling profile folder is gone"));
        }

        [Fact]
        public void This_Process_Own_Live_Claim_Is_Never_Reaped()
        {
            CreateProxiedProfile(_serverDir, LiveClaimId, Now.UtcDateTime.AddDays(-31)); // folder stale...
            var ownClaim = WriteClaim(LiveClaimId, TrustedClaimContent(LiveClaimId, SelfPid, 49321)); // ...but this pid owns the claim

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.True(File.Exists(ownClaim), "the sweep never reaps its own process's slot");
            Assert.Contains(report.LogLines, line => line.Contains("own live claim"));
        }

        [Fact]
        public void Old_Claim_Create_Litter_Is_Reaped_While_Young_Litter_Survives()
        {
            var oldLitter = Path.Combine(_serverDir, "proxied-" + StaleId.ToString("D") + ".claim.tmp-777-" + Guid.NewGuid().ToString("N"));
            var youngLitter = Path.Combine(_serverDir, "proxied-" + FreshId.ToString("D") + ".claim.tmp-778-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(oldLitter, "half-written");
            File.WriteAllText(youngLitter, "mid-create");
            File.SetLastWriteTimeUtc(oldLitter, Now.UtcDateTime.AddHours(-3));
            File.SetLastWriteTimeUtc(youngLitter, Now.UtcDateTime.AddMinutes(-5));

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.False(File.Exists(oldLitter));
            Assert.True(File.Exists(youngLitter), "young litter may belong to a create racing this sweep");
            Assert.Equal(1, report.ClaimTempFilesReaped);
            Assert.Contains(report.LogLines, line => line.Contains("temp litter '" + oldLitter + "'"));
        }

        [Fact]
        public void Hostile_Foreign_Files_Under_The_Root_Are_Left_Alon_Completely()
        {
            // Anything not matching a plugin shape class - even named to tempt a naive walk -
            // is never read, moved, or deleted.
            var foreign = Path.Combine(_userDir, "not-a-proxy");
            var foreignFile = Path.Combine(foreign, "keep.txt");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(foreignFile, "keep");
            File.SetLastWriteTimeUtc(foreignFile, Now.UtcDateTime.AddDays(-365));
            Directory.SetLastWriteTimeUtc(foreign, Now.UtcDateTime.AddDays(-365));
            File.WriteAllText(Path.Combine(_serverDir, "notes.txt"), "keep too");

            var report = SweepFor().Run(_root, Now, Retention, LitterRetention);

            Assert.True(File.Exists(foreignFile));
            Assert.Equal(0, report.ProxiedFoldersSeen);
            Assert.Equal(0, report.ProxiedFoldersReaped);
            Assert.Empty(report.LogLines);
        }

        [Fact]
        public void The_Lockfile_Probe_Reports_Held_Only_While_An_Exclusive_Open_Fails()
        {
            var folder = Path.Combine(_serverDir, "proxied-" + LockedId.ToString("D"));
            var lockfile = Path.Combine(folder, "EBWebView", "lockfile");
            Directory.CreateDirectory(Path.GetDirectoryName(lockfile));

            // No lockfile at all: quiet.
            Assert.False(ProfileSweep.BrowserLockFileHeld(folder));

            // A stale leftover a crashed browser never deleted: opens cleanly -> not held.
            File.WriteAllText(lockfile, "stale");
            Assert.False(ProfileSweep.BrowserLockFileHeld(folder));

            // Held by somebody: exclusive open refuses -> held.
            using (var hold = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(ProfileSweep.BrowserLockFileHeld(folder));
            }
        }
    }
}
