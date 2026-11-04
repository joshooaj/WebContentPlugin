using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Spike probe (#90): what happens to the shared WebView2 browser process when the host
/// process that CREATED it exits while another host process is still attached to the same
/// user data folder with identical environment options?
///
/// This is the question the #87 enablement design (#91) hangs on: if the process collection
/// dies with the creator, the surviving process's views die too but a fresh init can
/// re-claim cleanly; if it survives, joined views keep rendering with a possibly-dead
/// proxy target and need a forced re-creation story.
///
/// Rows drive the standalone two-role harness in spike\WebView2LifetimeProbe (no MIP/VMS
/// dependency): both roles get the SAME user data folder and the SAME additional browser
/// argument, so they MUST share one browser process; the roles journal their own WebView2
/// lifecycle (INIT_OK / TICK / PROCESS_FAILED / SECOND_INIT) into per-role status files,
/// and the orchestrator reads the live msedgewebview2 process set (parent pids from the
/// live command lines, the same WMI channel the #67 probe uses) as the independent witness.
/// The rows assert harness health and evidence completeness, not the lifetime answer
/// itself: the answer is recorded from the evidence for the #90 findings comment.
/// </summary>
public class WebView2LifetimeProbeTests
{
    /// <summary>Identical for both roles on purpose: this is the argument whose per-process
    /// mismatch is the #87 bug; shared here so the attachment under test can succeed.</summary>
    private const string SharedBrowserArgument = "--disable-renderer-backgrounding";

    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(15);

    private readonly ITestOutputHelper _output;

    public WebView2LifetimeProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Row 1: creator starts (INIT_OK + ticking), joiner starts with identical options and
    /// must ATTACH rather than spawn a second browser process: the joiner's init succeeds,
    /// both roles tick, and the machine's msedgewebview2 set contains no process whose live
    /// command line names the joiner as its parent host.
    /// </summary>
    [Fact]
    public void ProbeLifetime_JoinerWithIdenticalOptionsAttachesToCreatorBrowserProcess()
    {
        var dir = PrepareArtifactDir("join-attaches");
        using var creator = ProbeHost.Start("creator", dir);
        WaitForJournalLine(creator, "INIT_OK", InitTimeout);
        WaitForJournalLine(creator, "TICK", TickTimeout);
        var creatorBrowserPids = SnapshotWebView2Processes();
        Assert.NotEmpty(creatorBrowserPids);

        using var joiner = ProbeHost.Start("joiner", dir);
        WaitForJournalLine(joiner, "INIT_OK", InitTimeout);
        WaitForJournalLineCount(joiner, "TICK", 2, TickTimeout);

        var afterJoin = SnapshotWebView2Processes();
        AssertNoBrowserProcessParentedTo(joiner.Process.Id, afterJoin);

        DumpEvidence("join-attaches", creator, joiner);
        Cleanup(dir, creator, joiner);
    }

    /// <summary>
    /// Row 2: creator HARD-KILLED (host process only, crash simulation) while the joiner is
    /// attached. Evidence question: does WebView2 itself take the browser process down with
    /// the creating host, and can the survivor re-init. The kill deliberately leaves the
    /// msedgewebview2 children alone: only WebView2's own lifetime logic may end them.
    /// </summary>
    [Fact]
    public void ProbeLifetime_CreatorHardKill_RecordsSurvivorEvidence()
    {
        var dir = PrepareArtifactDir("hard-kill");
        using var creator = ProbeHost.Start("creator", dir);
        WaitForJournalLine(creator, "INIT_OK", InitTimeout);

        // second-webview attempt fires late enough that the kill below lands first
        using var joiner = ProbeHost.Start("joiner", dir, spawnSecondAfterMs: 15_000);
        WaitForJournalLine(joiner, "INIT_OK", InitTimeout);
        WaitForJournalLineCount(joiner, "TICK", 2, TickTimeout);

        // crash simulation: kill ONLY the host process. tree-kill would murder the browser
        // processes directly, which is the harness pulling the trigger, not WebView2 deciding
        creator.Process.Kill(entireProcessTree: false);
        creator.Process.WaitForExit(30_000);

        // the joiner's own SECOND_INIT line is the completion sentinel for this row
        WaitForJournalLineMatching(joiner, l => l.Contains(" SECOND_INIT"), TimeSpan.FromSeconds(45));
        Thread.Sleep(3_000); // settle: let any dying TICK_FAIL / PROCESS_FAILED lines land

        var evidence = ReadJournalLines(joiner);
        Assert.Contains(evidence, l => l.Contains(" SECOND_INIT"));
        _output.WriteLine($"post-kill joiner evidence:{Environment.NewLine}{string.Join(Environment.NewLine, evidence)}");

        DumpEvidence("hard-kill", creator, joiner);
        Cleanup(dir, creator, joiner);
    }

    /// <summary>
    /// Row 3: creator exits CLEANLY (window close) while the joiner is attached. Same
    /// evidence question as row 2 with the graceful half of host exit: does the shared
    /// browser process go down with the creator here too, and can the survivor re-init.
    /// </summary>
    [Fact]
    public void ProbeLifetime_CreatorCleanExit_RecordsSurvivorEvidence()
    {
        var dir = PrepareArtifactDir("clean-exit");
        using var creator = ProbeHost.Start("creator", dir);
        WaitForJournalLine(creator, "INIT_OK", InitTimeout);

        using var joiner = ProbeHost.Start("joiner", dir, spawnSecondAfterMs: 15_000);
        WaitForJournalLine(joiner, "INIT_OK", InitTimeout);
        WaitForJournalLineCount(joiner, "TICK", 2, TickTimeout);

        Assert.True(creator.Process.CloseMainWindow(), "creator window close request failed");
        creator.Process.WaitForExit(30_000);

        WaitForJournalLineMatching(joiner, l => l.Contains(" SECOND_INIT"), TimeSpan.FromSeconds(45));
        Thread.Sleep(3_000);

        var evidence = ReadJournalLines(joiner);
        Assert.Contains(evidence, l => l.Contains(" SECOND_INIT"));
        _output.WriteLine($"post-exit joiner evidence:{Environment.NewLine}{string.Join(Environment.NewLine, evidence)}");

        DumpEvidence("clean-exit", creator, joiner);
        Cleanup(dir, creator, joiner);
    }

    /// <summary>
    /// Row 4 (control): the JOINER exits cleanly; the creator's view must keep ticking
    /// with no ProcessFailed - the attached host leaving never disturbs the collection.
    /// This one asserts the outcome, because a failed control row means the harness lied.
    /// </summary>
    [Fact]
    public void ProbeLifetime_JoinerCleanExit_LeavesCreatorUnharmed()
    {
        var dir = PrepareArtifactDir("joiner-exits");
        using var creator = ProbeHost.Start("creator", dir);
        WaitForJournalLine(creator, "INIT_OK", InitTimeout);
        WaitForJournalLineCount(creator, "TICK", 2, TickTimeout);

        using var joiner = ProbeHost.Start("joiner", dir);
        WaitForJournalLine(joiner, "INIT_OK", InitTimeout);
        WaitForJournalLineCount(joiner, "TICK", 2, TickTimeout);

        var baselineTicks = CountLines(ReadJournalLines(creator), "TICK ");
        joiner.Process.CloseMainWindow();
        joiner.Process.WaitForExit(30_000);

        WaitForJournalLineCount(creator, "TICK", baselineTicks + 3, TickTimeout);
        var evidence = ReadJournalLines(creator);
        Assert.DoesNotContain(evidence, l => l.Contains("PROCESS_FAILED"));
        Assert.DoesNotContain(evidence, l => l.Contains("TICK_FAIL"));

        DumpEvidence("joiner-exits", creator, joiner);
        Cleanup(dir, creator, joiner);
    }

    /// <summary>
    /// Row 5 (control): the JOINER is hard-killed (host only); the creator's view must keep
    /// ticking with no ProcessFailed - a crashed attached host never disturbs the collection.
    /// </summary>
    [Fact]
    public void ProbeLifetime_JoinerHardKill_LeavesCreatorUnharmed()
    {
        var dir = PrepareArtifactDir("joiner-hard-kill");
        using var creator = ProbeHost.Start("creator", dir);
        WaitForJournalLine(creator, "INIT_OK", InitTimeout);
        WaitForJournalLineCount(creator, "TICK", 2, TickTimeout);

        using var joiner = ProbeHost.Start("joiner", dir);
        WaitForJournalLine(joiner, "INIT_OK", InitTimeout);
        WaitForJournalLineCount(joiner, "TICK", 2, TickTimeout);

        var baselineTicks = CountLines(ReadJournalLines(creator), "TICK ");
        joiner.Process.Kill(entireProcessTree: false);
        joiner.Process.WaitForExit(30_000);

        WaitForJournalLineCount(creator, "TICK", baselineTicks + 3, TickTimeout);
        var evidence = ReadJournalLines(creator);
        Assert.DoesNotContain(evidence, l => l.Contains("PROCESS_FAILED"));
        Assert.DoesNotContain(evidence, l => l.Contains("TICK_FAIL"));

        DumpEvidence("joiner-hard-kill", creator, joiner);
        Cleanup(dir, creator, joiner);
    }

    /// <summary>
    /// Row 6 (re-claim branch): the browser process collection IS gone while a host survives.
    /// The teardown here is deliberately the harness pulling the trigger - tree-kill of the
    /// creator host takes its msedgewebview2 children directly - because rows 2/3 proved
    /// WebView2 itself does not end the collection on creator exit. With the collection dead,
    /// the surviving joiner's attached view must die (ProcessFailed BrowserProcessExited) and
    /// a FRESH EnsureCoreWebView2Async in the survivor must respawn the collection.
    /// This is the reproducible evidence for #91's re-claim path.
    /// </summary>
    [Fact]
    public void ProbeLifetime_BrowserProcessGone_SurvivorRespawnsCollection()
    {
        var dir = PrepareArtifactDir("collection-gone");
        using var creator = ProbeHost.Start("creator", dir);
        WaitForJournalLine(creator, "INIT_OK", InitTimeout);

        using var joiner = ProbeHost.Start("joiner", dir, spawnSecondAfterMs: 15_000);
        WaitForJournalLine(joiner, "INIT_OK", InitTimeout);
        WaitForJournalLineCount(joiner, "TICK", 2, TickTimeout);

        creator.Process.Kill(entireProcessTree: true);
        creator.Process.WaitForExit(30_000);

        // the survivor's attached view dies honestly...
        WaitForJournalLineMatching(joiner, l => l.Contains("PROCESS_FAILED"), TimeSpan.FromSeconds(15));
        // ...and its delayed fresh init respawns the collection
        WaitForJournalLineMatching(joiner, l => l.Contains(" SECOND_INIT_OK"), TimeSpan.FromSeconds(45));

        DumpEvidence("collection-gone", creator, joiner);
        Cleanup(dir, creator, joiner);
    }

    // ---- harness: probe host process + status journal -------------------------------

    private sealed class ProbeHost : IDisposable
    {
        public string Role { get; }
        public Process Process { get; }
        public string JournalPath { get; }

        private ProbeHost(string role, Process process, string journalPath)
        {
            Role = role;
            Process = process;
            JournalPath = journalPath;
        }

        public static ProbeHost Start(string role, string artifactDir, int? spawnSecondAfterMs = null)
        {
            var exe = ProbeExePath();
            var args = $"--role {role} --dir \"{artifactDir}\"";
            if (spawnSecondAfterMs.HasValue)
            {
                args += $" --spawn-second-after {spawnSecondAfterMs.Value}";
            }

            var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            return new ProbeHost(role, process, Path.Combine(artifactDir, $"status-{role}.log"));
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(10_000);
                }
            }
            catch (Exception)
            {
                // best effort; probes must never leave the box loaded with stragglers
            }
        }
    }

    private static string ProbeExePath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !(Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "tests"))))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var exe = Path.Combine(dir ?? throw new InvalidOperationException("repo root not found"), "spike", "WebView2LifetimeProbe", "bin", "Debug", "net10.0-windows", "WebView2LifetimeProbe.exe");
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"lifetime probe host not built: {exe}");
        }

        return exe;
    }

    private static string PrepareArtifactDir(string runName)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "webview2-lifetime-probe",
            $"{runName}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WaitForJournalLine(ProbeHost host, string linePrefix, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var lines = ReadJournalLines(host);
            if (lines.Any(l => l.Contains(" " + linePrefix + " ") || l.EndsWith(linePrefix, StringComparison.Ordinal)))
            {
                return;
            }

            if (host.Process.HasExited)
            {
                Assert.Fail($"{host.Role} probe exited before {linePrefix}; journal:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
            }

            Thread.Sleep(250);
        }

        Assert.Fail($"timeout waiting for {linePrefix} on {host.Role}; journal:{Environment.NewLine}{string.Join(Environment.NewLine, ReadJournalLines(host))}");
    }

    private static int CountLines(IEnumerable<string> lines, string contains) =>
        lines.Count(l => l.Contains(contains, StringComparison.Ordinal));

    private static void WaitForJournalLineMatching(ProbeHost host, Func<string, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var lines = ReadJournalLines(host);
            if (lines.Any(match))
            {
                return;
            }

            if (host.Process.HasExited)
            {
                Assert.Fail($"{host.Role} probe exited early; journal:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
            }

            Thread.Sleep(250);
        }

        Assert.Fail($"timeout waiting for a matching line on {host.Role}; journal:{Environment.NewLine}{string.Join(Environment.NewLine, ReadJournalLines(host))}");
    }

    private static void WaitForJournalLineCount(ProbeHost host, string linePrefix, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var lines = ReadJournalLines(host);
            if (lines.Count(l => l.Contains(" " + linePrefix + " ") || l.EndsWith(linePrefix, StringComparison.Ordinal)) >= count)
            {
                return;
            }

            if (host.Process.HasExited)
            {
                Assert.Fail($"{host.Role} probe exited before {count}x {linePrefix}; journal:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
            }

            Thread.Sleep(250);
        }

        Assert.Fail($"timeout waiting for {count}x {linePrefix} on {host.Role}; journal:{Environment.NewLine}{string.Join(Environment.NewLine, ReadJournalLines(host))}");
    }

    private static List<string> ReadJournalLines(ProbeHost host)
    {
        try
        {
            if (!File.Exists(host.JournalPath))
            {
                return new List<string>();
            }

            // shared-read so the probe can keep appending while we poll
            using var stream = new FileStream(host.JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        }
        catch (IOException)
        {
            return new List<string>();
        }
    }

    // ---- independent witness: live msedgewebview2 process set -------------------------

    private sealed record WebView2Process(int ProcessId, int ParentProcessId, string CommandLine);

    private static List<WebView2Process> SnapshotWebView2Processes()
    {
        var results = new List<WebView2Process>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'");
        foreach (ManagementObject row in searcher.Get())
        {
            results.Add(new WebView2Process(
                Convert.ToInt32(row["ProcessId"]),
                Convert.ToInt32(row["ParentProcessId"]),
                row["CommandLine"] as string ?? string.Empty));
        }

        return results;
    }

    private static void AssertNoBrowserProcessParentedTo(int hostPid, List<WebView2Process> snapshot)
    {
        var offenders = snapshot
            .Where(p => p.ParentProcessId == hostPid || p.CommandLine.Contains($"--parent-pid={hostPid}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(
            offenders.Count == 0,
            $"joiner spawned its own browser process collection:{Environment.NewLine}" +
            string.Join(Environment.NewLine, offenders.Select(o => $"pid={o.ProcessId} parent={o.ParentProcessId} cmd={o.CommandLine}")));
    }

    // ---- evidence dump ---------------------------------------------------------------

    private void DumpEvidence(string runName, params ProbeHost[] hosts)
    {
        _output.WriteLine($"=== #90 evidence run '{runName}' ===");
        foreach (var host in hosts)
        {
            _output.WriteLine($"--- {host.Role} (pid {host.Process.Id}) journal ---");
            foreach (var line in ReadJournalLines(host))
            {
                _output.WriteLine(line);
            }
        }

        foreach (var p in SnapshotWebView2Processes())
        {
            _output.WriteLine($"msedgewebview2 pid={p.ProcessId} parent={p.ParentProcessId}");
        }
    }

    private static void Cleanup(string dir, params ProbeHost[] hosts)
    {
        foreach (var host in hosts)
        {
            host.Dispose();
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // UDF can outlive its browser processes by a moment; leave the artifacts for inspection
        }
    }
}
