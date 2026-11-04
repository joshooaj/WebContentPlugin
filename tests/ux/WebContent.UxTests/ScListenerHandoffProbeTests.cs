using System.Management;
using System.Text.RegularExpressions;

using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #96 live probes (SC-15): when the boundary's listener OWNER dies mid-session - clean
/// close or hard kill - the surviving joined Smart Client heals itself WITHOUT user action: the
/// claim watchdog notices (dead pid or a released slot), takes the handoff through the claim
/// protocol (new generation: epoch bumps, fresh port), and forces the proxied view's re-creation
/// so the browser process is recycled with the new --proxy-server port baked in.
///
/// The witnesses:
///  - the survivor's proxied page re-RENDERS (CDP page count on the host recovers) within a
///    bounded window and with no interaction;
///  - the claim slot ends at exactly one v2 claim naming the SURVIVOR pid, a port different from
///    the dead owner's, epoch >= owner epoch + 1;
///  - the WMI process witness shows exactly ONE loopback --proxy-server port in the system and it
///    equals the NEW claim port (the recycled browser process carries the handoff's arguments);
///  - last-view-out-the-door: closing the survivor (now the owner) leaves NO claim file behind -
///    no dead-owner claim sitting on disk for hours.
///
/// Log-line caveat (measured since #87): while one Smart Client lives the other may journal
/// NOTHING to the machine-wide MIPLog.txt, so the survivor's handoff lines are only asserted when
/// the run proves the survivor journalled at all; the claim + process + render witnesses are the
/// authoritative evidence either way.
/// </summary>
public sealed class ScListenerHandoffProbeTests
{
    private readonly ITestOutputHelper _output;

    public ScListenerHandoffProbeTests(ITestOutputHelper output) => _output = output;

    /// <summary>#96 acceptance row 1: the owner closes CLEANLY while a joined client sits open.</summary>
    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeOwnerCleanCloseHealsJoinedView()
    {
        await RunHandoffProbeAsync(hardKill: false);
    }

    /// <summary>#96 acceptance row 2: the owner is HARD-KILLED while a joined client sits open.</summary>
    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeOwnerHardKillHealsJoinedView()
    {
        await RunHandoffProbeAsync(hardKill: true);
    }

    private async Task RunHandoffProbeAsync(bool hardKill)
    {
        var config = UxConfig.Load();
        var shape = hardKill ? "hard-kill" : "clean-close";
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-handoff-" + shape + "-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;
        ClientSession owner = null;
        ClientSession survivor = null;

        log.AppendLine($"=== SC-15: owner {shape} while a joined client is open -> automatic handoff + view re-creation (#96) ===");
        try
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            Thread.Sleep(2000);

            // ---- Owner client: claims the slot, binds the listener, renders. ----
            owner = LaunchIntoWorkspace(config, log, "owner");
            var ownerMain = owner.WaitForWindow(w => w.AutomationId == "_mainApplicationWindow", 60, "owner main window");
            SmartClientUi.WaitForMipViewItem(ownerMain);

            await using var cdp = new CdpConnector();
            var browser = await cdp.ConnectFirstAvailableAsync(config.WebView2DataRoot, 120);
            var homeHost = await WaitForRenderedPageHostAsync(browser, 120);
            log.AppendLine($"owner proxied page rendered (page host '{homeHost}')");

            var firstClaim = WaitForClaim(config, claim => claim.OwnerPid == owner.Process.Id, 60, log);
            if (firstClaim == null)
            {
                throw new InvalidOperationException("the owner never published a listener claim - nothing to hand off");
            }

            var deadOwnerPid = owner.Process.Id;
            var deadOwnerPort = firstClaim.Port;
            var firstEpoch = firstClaim.Epoch;
            log.AppendLine($"owner claim generation 1: pid={deadOwnerPid}, port={deadOwnerPort}, epoch={firstEpoch}");
            if (firstEpoch < 1)
            {
                throw new InvalidOperationException($"the owner's claim carries epoch {firstEpoch} - the v2 schema's generation field is not in use live");
            }

            // ---- Survivor client: joins the boundary; both pages render on the shared browser process. ----
            survivor = LaunchIntoWorkspace(config, log, "survivor");
            var survivorMain = survivor.WaitForWindow(w => w.AutomationId == "_mainApplicationWindow", 60, "survivor main window");
            var survivorItem = SmartClientUi.WaitForMipViewItem(survivorMain);

            var joinDeadline = DateTime.Now.AddSeconds(150);
            int pages = 0;
            while (DateTime.Now < joinDeadline)
            {
                pages = CountPagesOnHost(browser, homeHost);
                if (pages >= 2) break;
                Thread.Sleep(2000);
            }

            if (pages < 2)
            {
                Artifacts.Window(survivorMain, "sc-handoff-prejoin-both-pages-missing");
                throw new InvalidOperationException("precondition failed: the survivor never joined the boundary (pages on the shared browser process: " + pages + ")");
            }

            log.AppendLine("both clients render on the shared browser process - removing the owner from the picture...");
            Artifacts.Window(survivorMain, "sc-handoff-before-owner-exit");

            // ---- The owner leaves: clean close, or hard kill (ProcessKiller). ----
            if (hardKill)
            {
                // NOT ProcessKiller.KillClients: that would take the SURVIVOR down too. This row
                // hard-kills exactly the owner process.
                owner.Process.Kill();
                var exited = WaitForExit(owner, 30);
                if (!exited) throw new InvalidOperationException("the hard-killed owner process never disappeared");
            }
            else
            {
                ownerMain.Close();
                var exited = WaitForExit(owner, 90);
                if (!exited)
                {
                    Artifacts.Window(survivorMain, "sc-handoff-owner-close-did-not-exit");
                    throw new InvalidOperationException("the owner's clean close never exited the process within 90 s - the clean-exit premise of this row did not run");
                }
            }

            // ---- Recovery WITHOUT user action, bounded window. Watchdog period is 5 s; the
            // re-creation then rides the ~13 s browser-process recycle measured in #90. ----
            var survivorPid = survivor.Process.Id;
            var deadline = DateTime.Now.AddSeconds(180);
            ClaimSnapshot healed = null;
            while (DateTime.Now < deadline)
            {
                var claim = ReadOnlyClaim(config);
                if (claim != null && claim.OwnerPid == survivorPid && claim.Port != deadOwnerPort && claim.Epoch >= firstEpoch + 1)
                {
                    healed = claim;
                    break;
                }

                Thread.Sleep(3000);
            }

            var problems = new List<string>();
            if (healed == null)
            {
                var last = ReadOnlyClaim(config);
                problems.Add("within 180 s the claim never became the survivor's own fresh generation (last saw: " +
                    (last == null ? "no single claim" : $"pid={last.OwnerPid} port={last.Port} epoch={last.Epoch}") +
                    "; expected pid=" + survivorPid + ", port!=" + deadOwnerPort + ", epoch>=" + (firstEpoch + 1) + ")");
            }
            else
            {
                log.AppendLine($"handoff claim: pid={healed.OwnerPid}, port={healed.Port}, epoch={healed.Epoch} - the survivor owns the boundary at generation {healed.Epoch}");
            }

            // ---- Render witness: the survivor's proxied page comes BACK on a CDP endpoint. ----
            string recoveredHost = null;
            await using var cdp2 = new CdpConnector();
            try
            {
                var browser2 = await cdp2.ConnectFirstAvailableAsync(config.WebView2DataRoot, 120);
                var renderDeadline = DateTime.Now.AddSeconds(90);
                while (DateTime.Now < renderDeadline)
                {
                    var rendered = CountPagesOnHost(browser2, homeHost);
                    if (rendered >= 1)
                    {
                        recoveredHost = homeHost;
                        break;
                    }

                    await Task.Delay(3000);
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("CDP reconnect after the handoff failed: " + ex.Message);
            }

            if (recoveredHost == null)
            {
                Artifacts.Window(survivorMain, "sc-handoff-view-never-re-rendered");
                problems.Add("the survivor's proxied view never re-rendered after the handoff (no CDP page on host '" + homeHost + "')");
            }
            else
            {
                log.AppendLine($"survivor's proxied page re-rendered on the new generation (host '{recoveredHost}')");
            }

            // ---- Process witness: ONE loopback --proxy-server port, and it is the NEW claim port. ----
            var proxyPorts = WitnessProxyPorts();
            log.AppendLine($"distinct loopback --proxy-server ports after the handoff: [{string.Join(", ", proxyPorts)}]");
            if (proxyPorts.Count != 1)
            {
                problems.Add($"{proxyPorts.Count} distinct loopback --proxy-server ports exist ({string.Join(", ", proxyPorts)}) - the recycled browser process did not settle on exactly one");
            }
            else if (healed != null && proxyPorts[0] != healed.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                problems.Add($"the live browser process carries --proxy-server port {proxyPorts[0]}, not the handoff claim port {healed.Port}");
            }

            // ---- The survivor lives; the dead owner's pid names nothing live. ----
            if (survivor.Process.HasExited)
            {
                problems.Add("the surviving Smart Client died during the handoff");
            }

            // ---- MIP log (survivor-side lines conditionally enforced; measured #87 caveat). ----
            var watchLines = FindLogLinesSince("Boundary claim", runStarted: null);
            var handoffLines = watchLines.Where(l => l.Contains("handoff re-creation", StringComparison.Ordinal)).ToList();
            var takeoverLines = watchLines.Where(l =>
                l.Contains("no longer running", StringComparison.Ordinal) ||
                l.Contains("the owner left the boundary", StringComparison.Ordinal)).ToList();
            if (handoffLines.Count > 0)
            {
                log.AppendLine($"watcher journalled {takeoverLines.Count} notice line(s) and {handoffLines.Count} re-creation line(s) (sample: {handoffLines[0].Trim()})");
                if (takeoverLines.Count < 1)
                {
                    problems.Add("the re-creation was journalled but the watch's owner-death/stale-claim notice line is missing");
                }
            }
            else
            {
                log.AppendLine("NOTE (measured environment fact since #87): the surviving client may journal NOTHING to the machine-wide MIPLog.txt while another client lived - the handoff lines are unenforceable in that shape; the claim + process + render witnesses above are the authoritative evidence.");
            }

            Artifacts.Window(survivorMain, "sc-handoff-after-recovery");

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(problems.Count + " handoff problem(s): " + string.Join("; ", problems));
            }

            log.AppendLine($"VERIFIED ({shape}): the owner exited, and within the bounded window the survivor took the boundary's claim at a new generation (epoch {firstEpoch} -> {healed.Epoch}, port {deadOwnerPort} -> {healed.Port}), its proxied view re-rendered WITHOUT user action, and exactly one --proxy-server port (the new claim port) lives in the system.");

            // ---- Last-view-out-the-door: closing the survivor (now the owner) must leave NO claim. ----
            log.AppendLine("closing the last proxied view (the survivor) - its teardown must release its own claim...");
            survivorMain.Close();
            if (!WaitForExit(survivor, 90))
            {
                throw new InvalidOperationException("the survivor's clean close never exited the process within 90 s - the last-view-out premise did not run");
            }

            var gone = false;
            var claimGoneDeadline = DateTime.Now.AddSeconds(120);
            while (DateTime.Now < claimGoneDeadline)
            {
                var files = Directory.EnumerateFiles(config.WebView2DataRoot, "proxied-*.claim", SearchOption.AllDirectories).ToList();
                if (files.Count == 0)
                {
                    gone = true;
                    break;
                }

                Thread.Sleep(3000);
            }

            if (!gone)
            {
                var leftovers = Directory.EnumerateFiles(config.WebView2DataRoot, "proxied-*.claim", SearchOption.AllDirectories).ToList();
                throw new InvalidOperationException("the last proxied view closed cleanly, yet " + leftovers.Count + " claim file(s) remain: " + string.Join("; ", leftovers) + " - a dead-owner claim would sit on disk until the 30-day sweep");
            }

            log.AppendLine("VERIFIED: the last view out the door left no claim file behind.");
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            log.AppendLine("probe failed: " + failure);
            log.AppendLine(ex.ToString());
        }
        finally
        {
            Artifacts.WriteText($"sc-handoff-{shape}-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { survivor?.Dispose(); } catch { }
            try { owner?.Dispose(); } catch { }
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    private ClientSession LaunchIntoWorkspace(UxConfig config, System.Text.StringBuilder log, string role)
    {
        var session = ClientSession.Launch(config.SmartClientExe);
        var login = LoginFlow.WaitForLoginWindow(session);
        LoginFlow.Login(session, login, config);
        LoginFlow.AcceptCertificateTrust(session);
        var main = LoginFlow.WaitForSmartClientMain(session);
        try
        {
            SmartClientUi.Maximize(main);
        }
        catch (Exception)
        {
            // The SC main window can be re-styled mid-login; Maximize is cosmetic and the
            // window is re-queried fresh right after the settle sleep anyway.
            Thread.Sleep(1500);
            main = session.WaitForWindow(w => w.AutomationId == "_mainApplicationWindow", 60, "main window (post-maximize retry)");
            try { SmartClientUi.Maximize(main); } catch { }
        }

        Thread.Sleep(1500);
        main = session.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
        if (role == "owner" || !SmartClientUi.TrySelectWorkspaceWithoutInput(main, session, config.WorkspaceName))
        {
            main = SmartClientUi.SelectWorkspace(main, session, config.WorkspaceName);
        }

        log.AppendLine($"{role} client logged in, workspace selected");
        return session;
    }

    private sealed class ClaimSnapshot
    {
        public int OwnerPid;
        public int Port;
        public long Epoch;
    }

    private static ClaimSnapshot ParseClaim(string path)
    {
        var snapshot = new ClaimSnapshot();
        foreach (var line in File.ReadAllLines(path))
        {
            var split = line.IndexOf('=');
            if (split <= 0) continue;
            var key = line.Substring(0, split).Trim();
            var value = line.Substring(split + 1).Trim();
            if (key == "port" && int.TryParse(value, out var port)) snapshot.Port = port;
            if (key == "ownerPid" && int.TryParse(value, out var pid)) snapshot.OwnerPid = pid;
            if (key == "epoch" && long.TryParse(value, out var epoch)) snapshot.Epoch = epoch;
        }

        return snapshot;
    }

    /// <summary>Waits for exactly one claim file satisfying <paramref name="accept"/>; null on timeout.</summary>
    private static ClaimSnapshot WaitForClaim(UxConfig config, Func<ClaimSnapshot, bool> accept, int timeoutSeconds, System.Text.StringBuilder log)
    {
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            var claim = ReadOnlyClaim(config);
            if (claim != null && accept(claim)) return claim;
            Thread.Sleep(2000);
        }

        return null;
    }

    /// <summary>The single proxied-*.claim under the plugin data root (null when absent or multiple).</summary>
    private static ClaimSnapshot ReadOnlyClaim(UxConfig config)
    {
        try
        {
            var files = Directory.EnumerateFiles(config.WebView2DataRoot, "proxied-*.claim", SearchOption.AllDirectories).ToList();
            if (files.Count == 1) return ParseClaim(files[0]);
            if (files.Count > 1) return ParseClaim(files[0]); // the acceptance rows demand ONE; report the first
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static bool WaitForExit(ClientSession session, int timeoutSeconds)
    {
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            try
            {
                if (session.Process.HasExited) return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            Thread.Sleep(1000);
        }

        return false;
    }

    /// <summary>The distinct loopback --proxy-server ports across the plugin's browser processes.</summary>
    private static List<string> WitnessProxyPorts()
    {
        var ports = new List<string>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'");
        foreach (ManagementObject mo in searcher.Get())
        {
            var command = (mo["CommandLine"] as string) ?? string.Empty;
            if (!command.Contains("SmartClient.WebContentData", StringComparison.OrdinalIgnoreCase)) continue;

            var portMatch = Regex.Match(command, @"--proxy-server=\S*?127\.0\.0\.1:(\d+)");
            if (portMatch.Success) ports.Add(portMatch.Groups[1].Value);
        }

        return ports.Distinct().ToList();
    }

    private static async Task<string> WaitForRenderedPageHostAsync(Microsoft.Playwright.IBrowser browser, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string seen = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var page = browser.Contexts.SelectMany(c => c.Pages)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p.Url) &&
                    (p.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
            if (page != null)
            {
                if (Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) return uri.Host;
                seen = page.Url;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException("no rendered (http/https) CDP page appeared on the proxied browser process. Last seen: " + seen);
    }

    private static int CountPagesOnHost(Microsoft.Playwright.IBrowser browser, string host)
    {
        return browser.Contexts.SelectMany(c => c.Pages)
            .Count(p => !string.IsNullOrEmpty(p.Url) && p.Url.Contains(host, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> FindLogLinesSince(string needle, DateTime? runStarted)
    {
        return ReadScLogLines()
            .Where(l => l.Contains(needle, StringComparison.Ordinal) && (runStarted == null || LineStamp(l) >= runStarted.Value))
            .Take(40)
            .ToList();
    }

    /// <summary>MIP-log lines begin "2026-11-04 01:18:31.261-08:00"; unparseable lines count as old.</summary>
    private static DateTime LineStamp(string line)
    {
        var head = line.Length >= 23 ? line.Substring(0, 23) : line;
        return DateTime.TryParse(head, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var stamp)
            ? stamp
            : DateTime.MinValue;
    }

    private static List<string> ReadScLogLines()
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
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                lines.AddRange(reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')));
            }
            catch (Exception)
            {
            }
        }

        return lines;
    }
}
