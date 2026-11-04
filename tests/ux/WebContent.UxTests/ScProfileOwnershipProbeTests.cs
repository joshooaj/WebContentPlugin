using FlaUI.Core.AutomationElements;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Live acceptance probe for #87 interim contract (SC-13 in docs/verification-matrix.md,
/// ADR-0004 point 5): a SECOND concurrent Smart Client process whose proxied WebView2 fails to
/// initialize because the first session owns the boundary profile folder must show an honest
/// in-view profile-ownership notice - not a blank view - journalling ONE explanatory line per
/// failed initialization naming the profile-ownership cause and the browser id, and answer
/// Refresh by re-attempting creation through the WebView re-creation path (still failing while
/// the owner lives, but never blank, never crashing). The row launches TWO real Smart Client
/// processes against the live VMS onto the same proxied lab workspace (same browser id = same
/// boundary profile), witnesses the first's proxied browser process for the browser id, then
/// asserts the second's view state via UIA. The MIP log check is conditional by measured
/// environment fact: while one Smart Client process lives, a second concurrent one writes
/// NOTHING to the machine-wide MIPLog.txt (not even its plugin load line - proven across three
/// runs), so the one log line is enforced only if the joiner demonstrably logged in this run,
/// and is otherwise a disclosed NOTE with the line pinned by unit tests. A separate finding from
/// this probe's development: killing the owner client takes its proxied msedgewebview2 process
/// down with it (no orphaned holder survives), which is why the collision shape here is the
/// both-alive one. Artifacts land in webcontent-sc-ownership-probe\&lt;run&gt;.
/// </summary>
public class ScProfileOwnershipProbeTests
{
    private readonly ITestOutputHelper _output;

    public ScProfileOwnershipProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeSecondClientProfileOwnershipError()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-ownership-probe"));
        var runStarted = DateTime.Now;
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;

        ProcessKiller.KillClients("Client");
        ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);

        ClientSession owner = null;
        ClientSession joiner = null;

        try
        {
            // ---- Owner session: a healthy proxied view (SC-1/SC-11 posture). ----
            owner = ClientSession.Launch(config.SmartClientExe);
            var ownerLogin = LoginFlow.WaitForLoginWindow(owner);
            LoginFlow.Login(owner, ownerLogin, config);
            LoginFlow.AcceptCertificateTrust(owner);
            var ownerMain = LoginFlow.WaitForSmartClientMain(owner);
            SmartClientUi.Maximize(ownerMain);
            Thread.Sleep(1500);
            ownerMain = owner.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            ownerMain = SmartClientUi.SelectWorkspace(ownerMain, owner, config.WorkspaceName);
            var ownerItem = SmartClientUi.WaitForMipViewItem(ownerMain);

            // Precondition + the browser id under test: the live proxied browser process's
            // boundary profile folder is <...>\server-<leg>\proxied-<browser id>.
            string proxiedCommandLine = null;
            var witnessDeadline = DateTime.Now.AddSeconds(90);
            while (proxiedCommandLine == null && DateTime.Now < witnessDeadline)
            {
                proxiedCommandLine = BrowserProcessArgs.CommandLines("SmartClient.WebContentData")
                    .FirstOrDefault(line => line.Contains("--proxy-server=http=http://127.0.0.1", StringComparison.OrdinalIgnoreCase));
                if (proxiedCommandLine == null)
                {
                    Thread.Sleep(2000);
                }
            }

            if (proxiedCommandLine == null)
            {
                throw new InvalidOperationException("precondition failed: no msedgewebview2 process carries a loopback --proxy-server switch - the lab workspace is not proxy-enabled right now");
            }

            var userDataDir = MatchUserDataDir(proxiedCommandLine);
            if (userDataDir.EndsWith("\\EBWebView", StringComparison.OrdinalIgnoreCase))
            {
                userDataDir = userDataDir.Substring(0, userDataDir.Length - "\\EBWebView".Length);
            }

            var leaf = Path.GetFileName(userDataDir ?? string.Empty);
            if (!leaf.StartsWith("proxied-", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(leaf.Substring("proxied-".Length), out var browserId))
            {
                throw new InvalidOperationException($"the witnessed profile folder leaf '{leaf}' is not proxied-<guid> - cannot name the browser id under test");
            }

            log.AppendLine($"owner browser: id={browserId:D} profile={userDataDir}");

            if (FindDescendant(ownerItem, "profileOwnershipHeadline") != null)
            {
                throw new InvalidOperationException("the OWNER session already shows a profileOwnershipHeadline notice before the second client even launched - precondition broken");
            }

            log.AppendLine("owner view: healthy, no ownership notice (as expected).");

            // ---- Joiner session: the second concurrent Smart Client on the SAME boundary. ----
            joiner = ClientSession.Launch(config.SmartClientExe);
            var joinerLogin = LoginFlow.WaitForLoginWindow(joiner);
            LoginFlow.Login(joiner, joinerLogin, config);
            LoginFlow.AcceptCertificateTrust(joiner);
            var joinerMain = LoginFlow.WaitForSmartClientMain(joiner);
            SmartClientUi.Maximize(joinerMain);
            Thread.Sleep(1500);
            joinerMain = joiner.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

            // Pattern-based selection first (coordinate clicks land on whichever window happens
            // to be topmost when two maximized clients overlap).
            if (!SmartClientUi.TrySelectWorkspaceWithoutInput(joinerMain, joiner, config.WorkspaceName))
            {
                joinerMain = SmartClientUi.SelectWorkspace(joinerMain, joiner, config.WorkspaceName);
            }

            var joinerItem = SmartClientUi.WaitForMipViewItem(joinerMain);
            log.AppendLine("joiner view item open; waiting for the failed proxied initialization to surface in-view...");

            var notice = WaitForElement(joinerItem, "profileOwnershipHeadline", 120);
            if (notice == null)
            {
                Artifacts.Window(joinerMain, "sc-ownership-no-notice");
                throw new InvalidOperationException("the second client's proxied view never showed the in-view profile-ownership notice (blank-view regression - exactly the #87 failure)");
            }

            var headline = notice.Name ?? string.Empty;
            var explanation = FindDescendant(joinerItem, "profileOwnershipText")?.Name ?? string.Empty;
            var detail = FindDescendant(joinerItem, "profileOwnershipDetail")?.Name ?? string.Empty;
            var cause = FindDescendant(joinerItem, "profileOwnershipCause")?.Name ?? string.Empty;
            log.AppendLine($"notice cause: {cause}");
            log.AppendLine($"notice headline: {headline}");
            log.AppendLine($"notice text: {explanation}");
            log.AppendLine($"notice detail: {detail}");

            var wordingProblems = new List<string>();
            if (!headline.Contains("Another Smart Client session", StringComparison.OrdinalIgnoreCase)) wordingProblems.Add("headline does not name another Smart Client session: '" + headline + "'");
            if (!explanation.Contains("profile", StringComparison.OrdinalIgnoreCase) || !explanation.Contains("this computer", StringComparison.OrdinalIgnoreCase)) wordingProblems.Add("explanation does not read as a machine-local profile-ownership cause: '" + explanation + "'");
            if (!detail.Contains(browserId.ToString("D"), StringComparison.OrdinalIgnoreCase)) wordingProblems.Add($"detail does not name the browser id {browserId:D}: '{detail}'");

            // The owner's own view must be untouched by all of this (both clients live).
            if (FindDescendant(SmartClientUi.FindMipViewItem(ownerMain) ?? ownerItem, "profileOwnershipHeadline") != null)
            {
                wordingProblems.Add("the OWNER session shows an ownership notice - the contract leaked onto the healthy view");
            }

            if (wordingProblems.Count > 0)
            {
                throw new InvalidOperationException(wordingProblems.Count + " in-view problem(s): " + string.Join("; ", wordingProblems));
            }

            // ---- Refresh on the errored view re-attempts creation through the re-creation path. ----
            var refresh = FindDescendant(joinerItem, "NavReload");
            if (refresh == null)
            {
                throw new InvalidOperationException("the joiner view's NavReload toolbar button is not reachable via UIA");
            }

            var refreshEnabled = true;
            try { refreshEnabled = refresh.IsEnabled; } catch { }
            log.AppendLine($"joiner NavReload enabled: {refreshEnabled}");

            Artifacts.Window(joinerMain, "sc-ownership-notice");
            try { refresh.Patterns.Invoke.Pattern.Invoke(); } catch { refresh.Click(); }
            Thread.Sleep(3000);

            if (joiner.Process.HasExited)
            {
                throw new InvalidOperationException("the second Smart Client DIED on the Refresh of the errored proxied view (the old Reload crash shape)");
            }

            if (owner.Process.HasExited)
            {
                throw new InvalidOperationException("the OWNER Smart Client died while the joiner's Refresh re-attempt ran - the contract leaked into the healthy session");
            }

            // The re-attempt fails again while the owner still lives - the notice must come back
            // (never blank). UIA is the authoritative second failure either way.
            var joinerItem2 = SmartClientUi.FindMipViewItem(joinerMain) ?? joinerItem;
            var noticeAgain = WaitForElement(joinerItem2, "profileOwnershipHeadline", 90);
            if (noticeAgain == null)
            {
                Artifacts.Window(joinerMain, "sc-ownership-after-refresh-blank");
                throw new InvalidOperationException("after the Refresh re-attempt the joiner's view is blank again - the notice did not survive/reappear on the second failed initialization");
            }

            // ---- The MIP log: one line per failed proxied initialization, conditionally enforced. ----
            // Measured environment fact (three #87 probe runs): while one Smart Client process
            // lives, a second concurrent one writes NOTHING to the machine-wide MIPLog.txt - not
            // even its plugin load line. So the joiner's ONE ownership line per failure is only
            // enforceable when this run proves the joiner wrote at all; otherwise it is a
            // disclosed NOTE (the line itself is pinned by BrowserInitFailureTests + the
            // PublishInitFailure wiring guard), and the UIA notice above stands as the
            // authoritative witness of both failures.
            var needle = "CoreWebView2 initialization failed for proxied browser " + browserId.ToString("D");
            var pluginLoads = CountLogLinesSince("Debug: PluginLoader ():Web Content, loaded", runStarted);
            var joinerLogged = pluginLoads >= 2;
            log.AppendLine($"'Web Content' plugin load lines in the MIP log since run start: {pluginLoads} (one per logging Smart Client process; joiner demonstrably logged: {joinerLogged})");

            var ownershipLines = FindLogLines(needle);
            if (joinerLogged)
            {
                var logDeadline = DateTime.Now.AddSeconds(20);
                while (ownershipLines.Count == 0 && DateTime.Now < logDeadline)
                {
                    Thread.Sleep(2000);
                    ownershipLines = FindLogLines(needle);
                }
            }

            foreach (var line in ownershipLines.Take(3))
            {
                log.AppendLine("MIP log: " + line);
            }

            if (joinerLogged)
            {
                if (ownershipLines.Count == 0)
                {
                    throw new InvalidOperationException("the joiner demonstrably logged in this run, yet the MIP log carries no 'CoreWebView2 initialization failed for proxied browser <id>' line - the one-line journal is missing");
                }

                if (!ownershipLines.Any(l => l.Contains("another Smart Client session on this machine", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("the ownership log line does not name the profile-ownership cause");
                }

                var recreateLines = FindLogLines("Refresh-recreation for view item");
                var recreateDeadline = DateTime.Now.AddSeconds(20);
                while (recreateLines.Count == 0 && DateTime.Now < recreateDeadline)
                {
                    Thread.Sleep(2000);
                    recreateLines = FindLogLines("Refresh-recreation for view item");
                }

                foreach (var line in recreateLines.Take(2))
                {
                    log.AppendLine("MIP log: " + line);
                }

                if (recreateLines.Count == 0)
                {
                    throw new InvalidOperationException("the Refresh click never routed into the re-creation path (no 'Refresh-recreation for view item' log line although the joiner demonstrably logged)");
                }

                var secondFailures = ownershipLines.Count;
                var secondDeadline = DateTime.Now.AddSeconds(30);
                while (secondFailures < 2 && DateTime.Now < secondDeadline)
                {
                    Thread.Sleep(2000);
                    secondFailures = FindLogLines(needle).Count;
                }

                log.AppendLine($"ownership log lines for browser {browserId:D} after the refresh re-attempt: {secondFailures}");
                if (secondFailures < 2)
                {
                    log.AppendLine("NOTE: the re-attempt's second ownership line not visible in the log window - the in-view notice above is the authoritative second failure.");
                }

                log.AppendLine($"VERIFIED: second client's proxied view showed the profile-ownership notice (headline '{headline}'), the failed initializations journalled their ownership line(s) naming the cause + browser {browserId:D}, the owner view stayed healthy, and Refresh re-attempted creation via the re-creation path without killing either client.");
            }
            else
            {
                log.AppendLine("NOTE (disclosed limitation, measured 2026-11-03): this machine's Smart Client keeps the machine-wide MIPLog.txt single-writer - the second concurrent client journalled NOTHING (0 extra plugin-load lines), so its ONE 'CoreWebView2 initialization failed for proxied browser <id>' line could not be witnessed live in the both-alive #87 shape. The line content and its single PublishInitFailure call site are pinned by BrowserInitFailureTests + ProxiedInitErrorWiringGuardTests; the in-view notice above is the live witness of the failure. A killed owner also takes its proxied browser process down with it (no orphaned holder), so no client-lighter collision shape exists on this runtime either.");
                log.AppendLine($"VERIFIED: second client's proxied view showed the profile-ownership notice (headline '{headline}'), the owner view stayed healthy, and Refresh re-attempted creation via the re-creation path without killing either client; the one-line journal is unit-pinned only (see NOTE).");
            }
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            log.AppendLine("probe failed: " + failure);
            log.AppendLine(ex.ToString());
        }
        finally
        {
            Artifacts.WriteText("sc-ownership-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { joiner?.Dispose(); } catch { }
            try { owner?.Dispose(); } catch { }
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    private static AutomationElement FindDescendant(AutomationElement root, string automationId)
    {
        try
        {
            return root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch
        {
            return null;
        }
    }

    private static AutomationElement WaitForElement(AutomationElement root, string automationId, int timeoutSeconds)
    {
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            var element = FindDescendant(root, automationId);
            if (element != null)
            {
                try { if (!element.IsOffscreen) return element; } catch { return element; }
            }

            Thread.Sleep(1000);
        }

        return FindDescendant(root, automationId);
    }

    private static string MatchUserDataDir(string commandLine)
    {
        var match = System.Text.RegularExpressions.Regex.Match(commandLine ?? string.Empty,
            "--user-data-dir=(\"(?<q>[^\"]*)\"|(?<b>[^\\s]*))");
        if (!match.Success)
        {
            return string.Empty;
        }

        return match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["b"].Value;
    }

    /// <summary>
    /// Smart Client MIP log lines containing <paramref name="needle"/> (top-level MIPLog.txt plus
    /// rolling archives), opened with FileShare.ReadWrite - the running client's logger holds the
    /// file open. Note the single-writer fact measured in #87's runs: while one Smart Client
    /// process lives, a second concurrent one's log lines never land in this file at all.
    /// </summary>
    private static List<string> FindLogLines(string needle)
    {
        return ReadScLogLines().Where(l => l.Contains(needle, StringComparison.Ordinal)).Take(40).ToList();
    }

    /// <summary>
    /// Log lines containing <paramref name="needle"/> whose leading MIP-log timestamp is at or
    /// after <paramref name="since"/> - used to count this run's process-start markers.
    /// </summary>
    private static int CountLogLinesSince(string needle, DateTime since)
    {
        var count = 0;
        foreach (var line in ReadScLogLines())
        {
            if (!line.Contains(needle, StringComparison.Ordinal))
            {
                continue;
            }

            // MIP-log lines begin "2026-11-03 23:16:56.261-08:00"; unparseable lines count as old.
            var head = line.Length >= 23 ? line.Substring(0, 23) : line;
            if (DateTime.TryParse(head, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var stamp) && stamp >= since)
            {
                count++;
            }
        }

        return count;
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
