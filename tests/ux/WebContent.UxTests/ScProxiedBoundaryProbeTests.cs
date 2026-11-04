using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Live acceptance probe for ADR-0004 / #92 (SC-11 in docs/verification-matrix.md): the proxied
/// browser profile must be keyed to the FULL proxied boundary - the on-disk (and live browser
/// command line) shape is <c>&lt;root&gt;\&lt;user key&gt;\server-&lt;server key&gt;\proxied-&lt;browser id&gt;</c>,
/// so two Smart Clients logged into different VMS servers can never share a proxied profile.
/// The row launches the real Smart Client, opens the proxied lab workspace, and reads the LIVE
/// msedgewebview2 command line (same witness the SC-8 row uses): the browser was launched with a
/// boundary-keyed --user-data-dir naming the configured VMS host in its server segment. Old-shape
/// folders (<c>&lt;root&gt;\&lt;user&gt;\proxied-*</c>) that exist from before the change are only
/// tolerated, never rewritten - #94 reaps them. Artifacts land in webcontent-sc-proxy-probe\&lt;run&gt;.
/// </summary>
public class ScProxiedBoundaryProbeTests
{
    private readonly ITestOutputHelper _output;

    public ScProxiedBoundaryProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeProxiedBoundaryProfilePath()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new System.Text.StringBuilder();
        string failure = null;
        var startedAt = DateTime.Now;

        try
        {
            var baseline = SnapshotProxiedFolders(config.WebView2DataRoot);
            log.AppendLine($"baseline proxied-* folders before the run ({baseline.Count}):");
            foreach (var folder in baseline)
            {
                log.AppendLine($"  baseline: {folder}");
            }

            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

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
            if (item == null)
            {
                throw new InvalidOperationException("No MIP view item opened; cannot observe a proxied browser.");
            }

            // Precondition: the lab workspace must be proxy-enabled - wait for a browser process
            // that carries the loopback --proxy-server switch (the #54 posture SC-1..SC-10 rely on).
            string proxiedCommandLine = null;
            var deadline = DateTime.Now.AddSeconds(90);
            while (proxiedCommandLine == null && DateTime.Now < deadline)
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
                throw new InvalidOperationException(
                    "precondition failed: no msedgewebview2 process on this data root carries a loopback --proxy-server switch - the lab workspace is not proxy-enabled right now");
            }

            log.AppendLine("proxied browser process: " + BrowserProcessArgs.Summarize(proxiedCommandLine));

            var userDataDir = MatchUserDataDir(proxiedCommandLine);
            if (string.IsNullOrEmpty(userDataDir))
            {
                throw new InvalidOperationException("the proxied browser command line carries no --user-data-dir");
            }

            // Harness pitfall for the next reader: the WebView2 RUNTIME process command line
            // carries the runtime's own data subfolder (<requested UDF>\EBWebView), not the
            // folder the plugin requested. The boundary key lives in the requested UDF.
            if (userDataDir.EndsWith("\\EBWebView", StringComparison.OrdinalIgnoreCase))
            {
                userDataDir = userDataDir.Substring(0, userDataDir.Length - "\\EBWebView".Length);
            }

            log.AppendLine($"boundary profile path observed live: {userDataDir}");

            // Shape: <root>\<user key>\server-<server key>\proxied-<guid D>. The root itself and
            // the browser-id guid are known; the user leg is whatever the login resolved (identity
            // guid, DOMAIN\user fallback, ...), so it is one opaque safe segment; the server leg
            // must name the configured VMS host.
            var rootPrefix = config.WebView2DataRoot.TrimEnd('\\');
            if (!userDataDir.StartsWith(rootPrefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"--user-data-dir '{userDataDir}' is not under the plugin data root '{rootPrefix}'");
            }

            var tail = userDataDir.Substring(rootPrefix.Length + 1);
            var segments = tail.Split('\\');
            if (segments.Length != 3)
            {
                throw new InvalidOperationException(
                    $"boundary profile path '{tail}' is not the three-leg shape <user>\\server-<server>\\proxied-<browser id>");
            }

            var guidPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
            if (!System.Text.RegularExpressions.Regex.IsMatch(segments[2], "^proxied-" + guidPattern + "$"))
            {
                throw new InvalidOperationException($"leaf segment '{segments[2]}' is not proxied-<lowercase-D guid>");
            }

            if (!segments[1].StartsWith("server-", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"server segment '{segments[1]}' lacks the server- prefix - this is the old pre-#92 shape");
            }

            // The server leg must name the configured VMS host (sanitized lower-case host, then
            // the port the Smart Client logged into). The login box carries exactly VMS_ADDRESS.
            var expectedHost = SanitizeHost(config.VmsAddress);
            if (string.IsNullOrEmpty(expectedHost))
            {
                throw new InvalidOperationException("could not derive the expected host leg from VMS_ADDRESS");
            }

            var serverLeg = segments[1].Substring("server-".Length);
            var hostMatches = serverLeg.Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
                           || serverLeg.StartsWith(expectedHost + "-", StringComparison.OrdinalIgnoreCase);
            if (!hostMatches)
            {
                throw new InvalidOperationException(
                    $"server segment '{serverLeg}' does not carry the configured VMS host leg '{expectedHost}'");
            }

            if (!Directory.Exists(userDataDir))
            {
                throw new InvalidOperationException($"the observed boundary profile folder does not exist on disk: {userDataDir}");
            }

            // And nothing wrote an OLD-shape folder (proxied-* directly under a user key) during
            // this run - the migration must be complete, old folders are only pre-existing litter.
            var rewrites = SnapshotProxiedFolders(config.WebView2DataRoot)
                .Where(f => IsOldShape(f, rootPrefix)
                            && Directory.GetLastWriteTimeUtc(f) >= startedAt.ToUniversalTime().AddSeconds(-5))
                .ToList();
            if (rewrites.Count > 0)
            {
                throw new InvalidOperationException(
                    "old-shape proxied folders were WRITTEN during this run: " + string.Join("; ", rewrites));
            }

            log.AppendLine($"VERIFIED: live proxied browser uses boundary-keyed profile '{tail}' (host leg '{expectedHost}' present); no old-shape writes.");
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            log.AppendLine("probe failed: " + failure);
            log.AppendLine(ex.ToString());
        }
        finally
        {
            foreach (var folder in SnapshotProxiedFolders(config.WebView2DataRoot))
            {
                log.AppendLine($"after-run: {folder}");
            }

            Artifacts.WriteText("sc-boundary-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    /// <summary>All proxied-* folders under the data root, root-relative.</summary>
    private static List<string> SnapshotProxiedFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return new List<string>();
        }

        return Directory.GetDirectories(root, "proxied-*", SearchOption.AllDirectories)
            .Select(d => d.Substring(root.TrimEnd('\\').Length + 1))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsOldShape(string rootRelativeFolder, string rootPrefix)
    {
        // Old shape: exactly two levels under the root (user key, proxied leaf). New shape: three.
        return rootRelativeFolder.Split('\\').Length == 2;
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
    /// Mirrors BrowserProfileKey.DeriveServerKey's host leg (the harness never links plugin
    /// internals): lower-case, letters/digits/./-/_ survive, everything else becomes _.
    /// </summary>
    private static string SanitizeHost(string address)
    {
        var host = address;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
        }
        else if (Uri.TryCreate("http://" + address, UriKind.Absolute, out var withScheme))
        {
            host = withScheme.Host;
        }

        host = (host ?? string.Empty).Trim().ToLowerInvariant();
        return new string(host.Select(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' ? ch : '_').ToArray());
    }
}
