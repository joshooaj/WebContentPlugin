using System.Net;
using System.Net.Sockets;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Live acceptance probe for ADR-0004 / #94 (SC-12 in docs/verification-matrix.md): the Smart
/// Client startup sweep reaps orphaned proxied browser-profile folders and stale listener-claim
/// files under the plugin data root, off the UI thread, journalled, never fatal, and NEVER
/// touching in-retention folders, live-owner claims, or the non-proxied EBWebView tree.
/// The row plants a full decoy table under the REAL data root in a dedicated probe user-key
/// folder (old-shape stale, boundary-shape stale, fresh, lock-held, dead/live/corrupt/cross-id
/// claims, old/fresh claim-temp litter, an ancient EBWebView tree), launches the real Smart
/// Client (whose plugin schedules the one-shot sweep ten seconds after Init), and reads the
/// outcomes back from disk. It also journalled whatever the Smart Client MIP log recorded for
/// the sweep source (log absence is journalled, not failed - the AGENTS caveat).
/// Artifacts land in webcontent-sc-sweep-probe\&lt;run&gt;; the whole probe folder is torn down
/// in the finally block.
/// </summary>
public class ScProfileSweepProbeTests
{
    private const string ProbeUserKey = "probe-94-sweep-user";
    private const string ProbeServerDir = "server-probe.sweep-7";

    private readonly ITestOutputHelper _output;

    public ScProfileSweepProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeProxiedProfileSweepStartup()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-sweep-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;

        var probeRoot = Path.Combine(config.WebView2DataRoot.TrimEnd('\\'), ProbeUserKey);
        TcpListener liveClaimListener = null;
        FileStream heldLockFile = null;

        try
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);

            // A clean slate for THIS run's probe folder only - real user state is never removed.
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, true);
            }

            var stale = DateTime.Now.AddDays(-45);
            var litter = DateTime.Now.AddDays(-3);

            // (a) old-shape stale folder: <user>\proxied-<guid>, pre-#92 shape -> REAPED.
            var oldShapeStale = CreateProfile(probeRoot, GuidA, stale);

            // (b) boundary-shape stale folder: <user>\server-<leg>\proxied-<guid> -> REAPED.
            var serverDir = Path.Combine(probeRoot, ProbeServerDir);
            var boundaryStale = CreateProfile(serverDir, GuidB, stale);

            // (c) fresh folder in retention -> KEPT.
            var fresh = CreateProfile(probeRoot, GuidC, DateTime.Now);

            // (d) the non-proxied EBWebView tree, however ancient -> NEVER touched.
            var nonProxiedWeb = Path.Combine(probeRoot, "EBWebView");
            var deep = Path.Combine(nonProxiedWeb, "Default", "OldCache");
            Directory.CreateDirectory(deep);
            var ancientFile = Path.Combine(deep, "ancient.txt");
            File.WriteAllText(ancientFile, "do not delete me");
            Stamp(deep, ancientFile, DateTime.Now.AddDays(-365));

            // (e) dead-owner claim beside a FRESH folder: only the claim is stale -> claim REAPED,
            // folder KEPT (proves the claim rule is independent of folder age).
            var deadClaimFolder = CreateProfile(serverDir, GuidD, DateTime.Now);
            var deadClaim = WriteClaim(serverDir, GuidD, 999999, 47811);

            // (f) live-owner claim: owner = this still-running probe process, port = a port this
            // process is actually listening on -> claim KEPT.
            liveClaimListener = new TcpListener(IPAddress.Loopback, 0);
            liveClaimListener.Start();
            var livePort = ((IPEndPoint)liveClaimListener.LocalEndpoint).Port;
            var liveClaimFolder = CreateProfile(serverDir, GuidE, DateTime.Now);
            var liveClaim = WriteClaim(serverDir, GuidE, Environment.ProcessId, livePort);

            // (g) cross-id claim (names another browser id) -> REAPED as foreign litter.
            var crossIdClaim = WriteClaim(serverDir, GuidF, Environment.ProcessId, livePort, namesBrowserId: Guid.Parse("99999999-9999-4999-8999-999999999999"));
            CreateProfile(serverDir, GuidF, DateTime.Now);

            // (h) corrupt claim content -> never trusted -> REAPED.
            CreateProfile(serverDir, GuidG, DateTime.Now);
            var corruptClaim = Path.Combine(serverDir, "proxied-" + GuidG.ToString("D") + ".claim");
            File.WriteAllText(corruptClaim, "i am not a claim at all");

            // (i) claim-temp litter older than the 24h litter retention -> REAPED.
            var oldLitter = Path.Combine(serverDir, "proxied-" + GuidH.ToString("D") + ".claim.tmp-4242-11111111-2222-4333-8444-555555555555");
            File.WriteAllText(oldLitter, "aborted create");
            File.SetLastWriteTimeUtc(oldLitter, litter.ToUniversalTime());

            // (j) fresh claim-temp litter (a create possibly mid-flight) -> KEPT.
            var freshLitter = Path.Combine(serverDir, "proxied-" + GuidI.ToString("D") + ".claim.tmp-4243-11111111-2222-4333-8444-555555555556");
            File.WriteAllText(freshLitter, "possibly mid-flight");

            // (k) stale folder whose lockfile this process holds exclusively -> skipped, KEPT.
            // The lockfile is stamped to the SAME stale marker: freshly-written it would make the
            // whole folder look in-retention, so the folder would be kept by the freshness rule
            // and the lock witness would never be consulted. Stamped stale, the ONLY thing that
            // can keep this folder is the lock probe - which is exactly what this row tests.
            var lockedStale = CreateProfile(probeRoot, GuidL, stale);
            var lockfilePath = Path.Combine(lockedStale, "EBWebView", "lockfile");
            File.WriteAllText(lockfilePath, string.Empty);
            File.SetLastWriteTimeUtc(lockfilePath, stale.ToUniversalTime());
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(lockfilePath), stale.ToUniversalTime());
            Directory.SetLastWriteTimeUtc(lockedStale, stale.ToUniversalTime());
            heldLockFile = new FileStream(lockfilePath, FileMode.Open, FileAccess.Read, FileShare.None);

            log.AppendLine("decoys planted under " + probeRoot);
            foreach (var dir in Directory.GetDirectories(probeRoot, "*", SearchOption.AllDirectories))
            {
                log.AppendLine("  decoy: " + dir.Substring(probeRoot.Length + 1));
            }

            // Launch the real Smart Client: plugin Init schedules the one-shot sweep 10 s in,
            // on the threadpool, never on the UI thread.
            using var sc = ClientSession.Launch(config.SmartClientExe);
            var login = LoginFlow.WaitForLoginWindow(sc);
            LoginFlow.Login(sc, login, config);
            LoginFlow.AcceptCertificateTrust(sc);
            var main = LoginFlow.WaitForSmartClientMain(sc);
            SmartClientUi.Maximize(main);
            Thread.Sleep(1500);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(main);
            log.AppendLine(item != null ? "MIP view item opened (plugin live)." : "no MIP view item; the sweep still rides plugin Init.");

            // The sweep fires ~10 s after plugin Init; poll for the reap of (a) and (b).
            var deadline = DateTime.Now.AddSeconds(120);
            while ((Directory.Exists(oldShapeStale) || Directory.Exists(boundaryStale)) && DateTime.Now < deadline)
            {
                Thread.Sleep(2000);
            }

            var problems = new List<string>();
            if (Directory.Exists(oldShapeStale)) problems.Add("(a) old-shape stale folder survived: " + oldShapeStale);
            if (Directory.Exists(boundaryStale)) problems.Add("(b) boundary-shape stale folder survived: " + boundaryStale);
            if (!Directory.Exists(fresh)) problems.Add("(c) in-retention folder was reaped: " + fresh);
            if (!File.Exists(ancientFile)) problems.Add("(d) non-proxied EBWebView tree was touched: " + ancientFile);
            if (File.Exists(deadClaim)) problems.Add("(e) dead-owner claim survived: " + deadClaim);
            if (!Directory.Exists(deadClaimFolder)) problems.Add("(e) fresh folder next to the dead claim was reaped: " + deadClaimFolder);
            if (!File.Exists(liveClaim)) problems.Add("(f) live-owner claim was reaped: " + liveClaim);
            if (!Directory.Exists(liveClaimFolder)) problems.Add("(f) folder next to the live claim was reaped: " + liveClaimFolder);
            if (File.Exists(crossIdClaim)) problems.Add("(g) cross-id claim survived: " + crossIdClaim);
            if (File.Exists(corruptClaim)) problems.Add("(h) corrupt claim survived: " + corruptClaim);
            if (File.Exists(oldLitter)) problems.Add("(i) stale claim-temp litter survived: " + oldLitter);
            if (!File.Exists(freshLitter)) problems.Add("(j) fresh claim-temp litter was reaped: " + freshLitter);
            if (!Directory.Exists(lockedStale)) problems.Add("(k) lock-held stale folder was forced: " + lockedStale);

            foreach (var problem in problems)
            {
                log.AppendLine("OUTCOME MISMATCH: " + problem);
            }

            // Journal whatever the Smart Client MIP log recorded for the sweep source. The log
            // can legitimately be absent (client logging posture - the AGENTS caveat), so its
            // absence is journalled, not failed; the disk outcomes above are authoritative.
            // The plugin flushes its lines slightly after the deletes land, so re-read for a
            // few seconds before concluding "none".
            var sweepLines = FindSweepLogLines();
            var logDeadline = DateTime.Now.AddSeconds(15);
            while (sweepLines.Count == 0 && DateTime.Now < logDeadline)
            {
                Thread.Sleep(2000);
                sweepLines = FindSweepLogLines();
            }

            if (sweepLines.Count == 0)
            {
                log.AppendLine("MIP log: no ProfileSweep lines found (log absent or client logging disabled) - journalled only.");
            }
            else
            {
                foreach (var line in sweepLines)
                {
                    log.AppendLine("MIP log: " + line);
                }
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(problems.Count + " decoy outcome(s) wrong: " + string.Join("; ", problems));
            }

            log.AppendLine($"VERIFIED: startup sweep reaped (a) old-shape stale, (b) boundary-shape stale, (e) dead-owner claim, (g) cross-id claim, (h) corrupt claim, (i) stale litter; kept (c) fresh, (d) non-proxied tree, (f) live-owner claim, (j) fresh litter, (k) lock-held folder.");
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            log.AppendLine("probe failed: " + failure);
            log.AppendLine(ex.ToString());
        }
        finally
        {
            try { heldLockFile?.Dispose(); } catch (Exception) { }
            try { liveClaimListener?.Stop(); } catch (Exception) { }

            Artifacts.WriteText("sc-sweep-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            // Tear the whole probe folder down (surviving decoys are litter now).
            var wipeDeadline = DateTime.Now.AddSeconds(30);
            while (Directory.Exists(probeRoot) && DateTime.Now < wipeDeadline)
            {
                try
                {
                    Directory.Delete(probeRoot, true);
                }
                catch (Exception)
                {
                    Thread.Sleep(2000);
                }
            }
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    // Fixed lowercase-D guids make the decoys greppable in the MIP log.
    private static readonly Guid GuidA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly Guid GuidB = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000b");
    private static readonly Guid GuidC = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000c");
    private static readonly Guid GuidD = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000d");
    private static readonly Guid GuidE = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000e");
    private static readonly Guid GuidF = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000f");
    private static readonly Guid GuidG = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000010");
    private static readonly Guid GuidH = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000011");
    private static readonly Guid GuidI = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000012");
    private static readonly Guid GuidL = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000013");

    /// <summary>A minimal UDF-shaped folder (proxied-&lt;guid&gt;\EBWebView\Default\state) stamped leaf-first.</summary>
    private static string CreateProfile(string parent, Guid id, DateTime markerLocal)
    {
        var folder = Path.Combine(parent, "proxied-" + id.ToString("D"));
        var web = Path.Combine(folder, "EBWebView");
        var profile = Path.Combine(web, "Default");
        Directory.CreateDirectory(profile);
        var file = Path.Combine(web, "state.txt");
        File.WriteAllText(file, "chatter");
        Stamp(profile, file, markerLocal);
        Stamp(web, null, markerLocal);
        Stamp(folder, null, markerLocal);
        return folder;
    }

    private static void Stamp(string dir, string file, DateTime markerLocal)
    {
        var utc = markerLocal.ToUniversalTime();
        if (file != null)
        {
            File.SetLastWriteTimeUtc(file, utc);
        }

        Directory.SetLastWriteTimeUtc(dir, utc);
    }

    /// <summary>
    /// The #93 claim schema written by hand (the harness never links plugin internals):
    /// header line + the five required fields.
    /// </summary>
    private static string WriteClaim(string serverDir, Guid folderStem, int ownerPid, int port, Guid? namesBrowserId = null)
    {
        var path = Path.Combine(serverDir, "proxied-" + folderStem.ToString("D") + ".claim");
        var content =
            "webcontent-listener-claim/1\n" +
            "browserId=" + (namesBrowserId ?? folderStem).ToString("D") + "\n" +
            "port=" + port + "\n" +
            "ownerPid=" + ownerPid + "\n" +
            "configFingerprint=probe-94\n" +
            "claimedAtUnixMs=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "\n";
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>ProfileSweep lines in the Smart Client MIP log (top-level plus rolling archives).</summary>
    private static List<string> FindSweepLogLines()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Milestone", "XProtect Smart Client");
        var candidates = new List<string>();
        try
        {
            var main = Path.Combine(dir, "MIPLog.txt");
            if (File.Exists(main))
            {
                candidates.Add(main);
            }

            var rolling = Path.Combine(dir, "MIPLogs");
            if (Directory.Exists(rolling))
            {
                candidates.AddRange(Directory.GetFiles(rolling, "MIPLog.*.txt")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .Take(3));
            }
        }
        catch (Exception)
        {
            return new List<string>();
        }

        var lines = new List<string>();
        foreach (var file in candidates)
        {
            // The running Smart Client's logger holds MIPLog.txt open - read it the way a
            // bystander must: ReadWrite sharing, swallow whatever the host refuses.
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                lines.AddRange(text.Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.Contains("ProfileSweep", StringComparison.Ordinal)
                                && (l.Contains("Swept", StringComparison.Ordinal) || l.Contains("Startup proxied-profile sweep", StringComparison.Ordinal)))
                    .Take(40));
            }
            catch (Exception)
            {
            }
        }

        return lines;
    }
}
