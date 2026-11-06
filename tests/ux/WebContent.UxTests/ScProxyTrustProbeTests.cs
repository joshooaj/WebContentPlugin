using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #72 live rows (verification-matrix SC-10): the untrusted-deployment-CA posture end to
/// end. Row 1 (unattended): with the CA trusted (the lab's normal state) a proxied view shows NO
/// CA banner at all - the ticket's "nothing worse than today" half. Row 2 (unattended, dialog
/// free): every negative path of the DEPLOYED WebContentTrustHelper.exe from the drop - usage,
/// unreadable, garbage, oversize, thumbprint mismatch, and the unprivileged store refusal - all
/// of which refuse BEFORE any UAC/crypt32 dialog could appear. Row 3 (unattended): the helper's
/// full success path executed ON the Event Server machine inside an elevated remote session
/// (like WssTargetTrust's import: remote admin sessions are already elevated, so no UAC and
/// crypt32's modal prompt has no interactive desktop to appear on): throwaway CN-scoped CA,
/// helper imports it, present-verified in LocalMachine\Root, removed, absent-verified. Row 4
/// (guarded, WEBCONTENT_CA_UNTRUST_LIVE=1): CA removed from BOTH root stores, Smart Client
/// shows the banner on the proxied view for the ordinary user, dismiss hides it for the session,
/// setup mode additionally reveals the Fix button (never clicked unattended - the UAC consent
/// would hang on the secure desktop; the declined/confirmed outcomes are unit+guard pinned).
/// </summary>
public class ScProxyTrustProbeTests
{
    /// <summary>The plugin drop's install folder (AGENTS.md INSTALLFOLDER); row 2 runs the real
    /// deployed exe from here, row 3 pins the same path on the Event Server machine.</summary>
    private const string PluginInstallFolder = @"C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin";

    private const string HelperExeName = "WebContentTrustHelper.exe";

    /// <summary>CN marking every helper-probe certificate (row 3); import rows purge by this CN.</summary>
    private const string ProbeCertificateCommonName = "WebContent trust-helper probe (test only)";

    /// <summary>CN of the deployment CA the plugin minted (#56 authority); row 4 removes and
    /// restores exactly this certificate.</summary>
    private const string DeploymentCaCommonName = "XProtect WebContent Plugin Loopback Proxy";

    /// <summary>The proxied https home of the lab workspace; a mint through the loopback listener
    /// (which publishes the trust posture) requires routing on + this origin on the allowlist.</summary>
    private const string ProxiedHttpsHome = "https://joshooaj.com/";

    private static bool UntrustedBannerRowEnabled =>
        Environment.GetEnvironmentVariable("WEBCONTENT_CA_UNTRUST_LIVE") == "1";

    private static bool HelperEndToEndRowEnabled =>
        Environment.GetEnvironmentVariable("UX_TRUST_HELPER_E2E") != "0";

    private readonly ITestOutputHelper _output;

    public ScProxyTrustProbeTests(ITestOutputHelper output) => _output = output;

    // -----------------------------------------------------------------------------------------
    // Row 1: CA trusted (lab normal) -> a proxied view shows no CA chrome at all.
    // -----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeTrustedCaProxiedViewShowsNoBanner()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        string failure = null;
        string previousHome = null;
        ClientSession sc = null;
        try
        {
            Assert.True(RootStoreContainsCaCn(StoreLocation.LocalMachine) || RootStoreContainsCaCn(StoreLocation.CurrentUser),
                "precondition: the deployment CA must be in a root store for this row (it is the lab's normal state)");

            previousHome = WorkspaceHomeAddress.Ensure(config, ProxiedHttpsHome, log);

            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            sc = ClientSession.Launch(config.SmartClientExe);
            var login = LoginFlow.WaitForLoginWindow(sc);
            LoginFlow.Login(sc, login, config);
            LoginFlow.AcceptCertificateTrust(sc);
            var main = LoginFlow.WaitForSmartClientMain(sc);
            SmartClientUi.Maximize(main);
            Thread.Sleep(1500);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

            main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(main);
            Assert.NotNull(item);

            // Let the https home commit and the leaf mint run (the mint publishes the posture);
            // then a collapsed WPF element must simply be ABSENT from the UIA tree.
            Thread.Sleep(12000);
            var banner = item.FindFirstDescendant(cf => cf.ByAutomationId("proxyCaBanner"));
            var fix = item.FindFirstDescendant(cf => cf.ByAutomationId("proxyCaFixButton"));
            log.AppendLine($"trusted CA -> proxyCaBanner present: {banner != null}, proxyCaFixButton present: {fix != null}");

            var windowShot = FlaUI.Core.Capturing.Capture.Element(main);
            windowShot.ToFile(Artifacts.PathOf("sc-trust-probe-trusted-no-banner.png"));
            windowShot.Dispose();

            Assert.True(banner == null, "the CA banner appeared even though the deployment CA is trusted");
            Assert.True(fix == null, "the fix button appeared even though the CA is trusted");
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}{Environment.NewLine}{ex}");
        }
        finally
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillClients("VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            sc?.Dispose();
            if (previousHome != null)
            {
                try
                {
                    WorkspaceHomeAddress.Ensure(config, previousHome.Trim(), log);
                    log.AppendLine($"workspace home address restored to '{previousHome.Trim()}'");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"!!! home restore FAILED ('{previousHome.Trim()}'): {ex.Message}");
                }
            }
            Artifacts.WriteText("sc-trust-probe-trusted-no-banner-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    // -----------------------------------------------------------------------------------------
    // Row 2: negative paths of the DEPLOYED helper exe. Every case refuses before any store or
    // dialog is reached, so the row is fully unattended on this (non-elevated) machine.
    // -----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeDeployedTrustHelperRefusesEveryNegativePath()
    {
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();
        var failures = new List<string>();
        try
        {
            var helper = Path.Combine(PluginInstallFolder, HelperExeName);
            Assert.True(File.Exists(helper), $"the deployed {HelperExeName} is missing from {PluginInstallFolder} - deploy first");
            log.AppendLine($"helper under test: {helper}");
            LogVersion(helper, log);

            string tempDir = Path.Combine(Path.GetTempPath(), "wc-trust-helper-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var garbagePath = Path.Combine(tempDir, "garbage.der");
                File.WriteAllText(garbagePath, "not a certificate at all");
                var oversizePath = Path.Combine(tempDir, "oversize.der");
                File.WriteAllBytes(oversizePath, new byte[9 * 1024]);

                var (realDer, realThumbprint) = CreateThrowawayCertificate(tempDir);

                // exit 1: usage family (no args, bad thumbprint shape).
                ExpectExit(RunHelper(helper, Array.Empty<string>()), 1, "no arguments -> usage", log, failures);
                ExpectExit(RunHelper(helper, new[] { garbagePath, "not-a-thumbprint" }), 1, "non-hex thumbprint -> usage", log, failures);

                // exit 3: unreadable certificate file.
                ExpectExit(RunHelper(helper, new[] { Path.Combine(tempDir, "missing.der"), new string('A', 40) }), 3, "missing file -> 3", log, failures);

                // exit 2: certificate refused before any store touch.
                ExpectExit(RunHelper(helper, new[] { garbagePath, new string('A', 40) }), 2, "garbage bytes -> refused", log, failures);
                ExpectExit(RunHelper(helper, new[] { oversizePath, new string('A', 40) }), 2, "oversize file -> refused", log, failures);

                var mismatch = RunHelper(helper, new[] { realDer, new string('A', 40) });
                ExpectExit(mismatch, 2, "thumbprint mismatch -> refused", log, failures);
                if (!mismatch.Output.Contains("does not match the expected", StringComparison.Ordinal))
                {
                    failures.Add("thumbprint-mismatch run did not state the refusal reason");
                }
                log.AppendLine("thumbprint-mismatch output: " + Flatten(mismatch.Output));

                // exit 4: everything validated, but THIS process (non-elevated) cannot even open
                // LocalMachine\Root for write - the negative shape the ticket's audit trail pins.
                // No dialog appears on this path (verified: the open simply Access-Denies).
                var unprivileged = RunHelper(helper, new[] { realDer, realThumbprint });
                ExpectExit(unprivileged, 4, "matched cert + non-elevated process -> store unavailable", log, failures);
                log.AppendLine("non-elevated run output: " + Flatten(unprivileged.Output));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
            log.AppendLine($"probe failed: {ex}");
        }
        finally
        {
            Artifacts.WriteText("sc-trust-probe-helper-negative-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
        }

        Assert.True(failures.Count == 0, $"helper negative-path failures:{Environment.NewLine}" + string.Join(Environment.NewLine, failures) + Environment.NewLine + log);
    }

    // -----------------------------------------------------------------------------------------
    // Row 3: the helper's SUCCESS path, end to end ON the Event Server machine inside an
    // elevated remote session (already-elevated, non-interactive: no UAC, crypt32's prompt has
    // no interactive desktop - same session shape as WssTargetTrust's import). CN-scoped CA,
    // import, presence check, removal, absence check. Set UX_TRUST_HELPER_E2E=0 to opt out.
    // -----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeTrustHelperElevatedEndToEndOnEventServer()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        if (!HelperEndToEndRowEnabled)
        {
            _output.WriteLine("SKIPPED: UX_TRUST_HELPER_E2E=0 - helper end-to-end row opted out.");
            return;
        }

        var log = new StringBuilder();
        string failure = null;
        try
        {
            var host = config.VmsAddress.Contains("://", StringComparison.Ordinal)
                && Uri.TryCreate(config.VmsAddress, UriKind.Absolute, out var uri) ? uri.Host : config.VmsAddress;
            log.AppendLine($"event server host: {host}");

            var script = $@"
$ErrorActionPreference = 'Stop'
$password = ConvertTo-SecureString $env:WEBCONTENT_ES_PASSWORD -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($env:WEBCONTENT_ES_USER, $password)
$session = New-PSSession -ComputerName '{host}' -Credential $credential
try {{
    Invoke-Command -Session $session -ScriptBlock {{
        $ErrorActionPreference = 'Stop'
        $commonName = '{ProbeCertificateCommonName}'
        $helper = Join-Path '{PluginInstallFolder}' '{HelperExeName}'
        if (-not (Test-Path -LiteralPath $helper)) {{ throw ('deployed helper missing on this machine: ' + $helper) }}
        Write-Output ('helper on server: ' + ((Get-Item $helper).VersionInfo.FileVersion + ' ' + (Get-FileHash $helper -Algorithm SHA256).Hash))

        # throwaway CN-scoped certificate, generated on the server itself
        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
        $request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
            ('CN=' + $commonName), $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
        $derPath = Join-Path $env:TEMP ('wc-trust-helper-e2e-' + $cert.Thumbprint + '.der')
        [System.IO.File]::WriteAllBytes($derPath, $cert.RawData)
        Write-Output ('probe certificate: ' + $cert.Thumbprint)

        # the helper, run from the deployed drop, inside this elevated non-interactive session
        # (EAP relaxed around the native call: PS 5.1 turns redirected stderr into terminating
        # error records otherwise; the exit code is the real verdict)
        $ErrorActionPreference = 'Continue'
        $output = & $helper $derPath $cert.Thumbprint 2>&1
        $exit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        Remove-Item -LiteralPath $derPath -Force -ErrorAction SilentlyContinue
        Write-Output ('helper exit=' + $exit + ' output: ' + (($output | Out-String).Trim() -replace [char]13, ' '))
        if ($exit -ne 0) {{ throw ('helper exit ' + $exit) }}

        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
        $store.Open('ReadWrite')
        try {{
            if (($store.Certificates.Find('FindByThumbprint', $cert.Thumbprint, $false)).Count -eq 0) {{
                throw ('helper claimed success but ' + $cert.Thumbprint + ' is NOT in LocalMachine\Root')
            }}
            Write-Output ('verified present in LocalMachine\Root: ' + $cert.Thumbprint)
            $stale = @($store.Certificates | Where-Object {{ $_.Subject -eq ('CN=' + $commonName) }})
            foreach ($old in $stale) {{
                $store.Remove($old)
                Write-Output ('removed: ' + $old.Thumbprint)
            }}
            if (($store.Certificates.Find('FindByThumbprint', $cert.Thumbprint, $false)).Count -ne 0) {{
                throw ('cleanup failed: ' + $cert.Thumbprint + ' still in LocalMachine\Root')
            }}
            Write-Output ('verified absent after cleanup: ' + $cert.Thumbprint)
        }} finally {{ $store.Close() }}
    }}
}} finally {{
    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
}}
";

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolvePowerShell(),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.Environment["WEBCONTENT_ES_USER"] = config.VmsUsername;
            startInfo.Environment["WEBCONTENT_ES_PASSWORD"] = config.VmsPassword;

            using var process = Process.Start(startInfo);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            foreach (var line in stdout.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line)) log.AppendLine("  es-helper: " + line.Trim());
            }
            if (process.ExitCode != 0)
            {
                log.AppendLine($"  remote phase FAILED (exit {process.ExitCode}): {stderr.Trim()}");
            }

            Assert.True(process.ExitCode == 0, $"Event Server helper end-to-end phase failed (exit {process.ExitCode}): {stderr.Trim()}");
            Assert.Contains("verified present in LocalMachine\\Root", stdout, StringComparison.Ordinal);
            Assert.Contains("verified absent after cleanup", stdout, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            log.AppendLine($"probe failed: {failure}{Environment.NewLine}{ex}");
        }
        finally
        {
            Artifacts.WriteText("sc-trust-probe-helper-endtoend-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
        }

        Assert.True(failure == null, $"probe run failed: {failure}{Environment.NewLine}{log}");
    }

    // -----------------------------------------------------------------------------------------
    // Row 4: the untrusted state made real on THIS machine (both root stores), the banner live,
    // dismiss per session, setup-mode-only fix button. Guarded: WEBCONTENT_CA_UNTRUST_LIVE=1.
    /// <remarks>
    /// The machine-store half of the mutation needs elevation: the row attempts an elevated
    /// localhost remote session (jhadmin credentials, same convention as the ES helpers). When
    /// that channel is unavailable unattended, the row restores the user store, records SKIPPED
    /// with the exact reason, and passes without asserting - the human-step procedure lands in
    /// docs/verification-matrix.md instead. Nothing is left mutated on the skip route.
    /// </remarks>
    // -----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "probe")]
    public async Task ProbeUntrustedCaBannerDismissAndSetupGate()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-sc-proxy-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        if (!UntrustedBannerRowEnabled)
        {
            _output.WriteLine("SKIPPED (opt-in row): set WEBCONTENT_CA_UNTRUST_LIVE=1 to mutate this machine's root stores for the untrusted-banner live row.");
            return;
        }

        var log = new StringBuilder();
        var failures = new List<string>();
        string skippedReason = null;
        string previousHome = null;
        ClientSession sc = null;
        byte[] authorityDer = null;
        string authorityThumbprint = null;
        var lmRemoved = false;
        try
        {
            // Locate the deployment CA the plugin minted (#56 authority) via the user store,
            // where this test's own token can read and remove without elevation.
            using (var cu = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
            {
                cu.Open(OpenFlags.ReadWrite);
                var found = cu.Certificates
                    .Find(X509FindType.FindBySubjectName, DeploymentCaCommonName, false)
                    .OfType<X509Certificate2>()
                    .FirstOrDefault(c => c.Subject.Contains(DeploymentCaCommonName, StringComparison.Ordinal));
                if (found == null)
                {
                    skippedReason = $"no '{DeploymentCaCommonName}' certificate in CurrentUser\\Root - this machine is not in the CA'd lab posture";
                    return;
                }

                authorityDer = found.RawData;
                authorityThumbprint = found.Thumbprint;
                log.AppendLine($"deployment CA: {authorityThumbprint} found in CurrentUser\\Root");

                if (!LocalMachineRootContains(authorityThumbprint, log))
                {
                    skippedReason = "the deployment CA is not in LocalMachine\\Root either - there is no untrusted-state transition to probe";
                    cu.Add(new X509Certificate2(authorityDer));
                    return;
                }
            }

            // Untrusted = absent from BOTH stores. User half first (works when this test host is
            // elevated; Access-Denied on a plain shell, verified live). Machine half through an
            // elevated localhost remote session - if that channel is unavailable unattended,
            // restore the user half and skip cleanly.
            try
            {
                using (var cu = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    cu.Open(OpenFlags.ReadWrite);
                    foreach (var stale in cu.Certificates.Find(X509FindType.FindByThumbprint, authorityThumbprint, false).OfType<X509Certificate2>())
                    {
                        cu.Remove(stale);
                    }
                }
                log.AppendLine("deployment CA removed from CurrentUser\\Root");
            }
            catch (Exception ex)
            {
                // A CA in CurrentUser\Root is store-protected: removing it needs an elevated
                // token of THIS user, which no unattended remote channel can substitute (the
                // WinRM session sees the remote-admin's user store, not this one). The live row
                // runs from an elevated shell (human step); unattended it skips with the stores
                // untouched.
                skippedReason = $"CurrentUser\\Root removal refused unattended ({ex.Message}) - run this row from an elevated joshooaj shell for the live untrusted state";
                RestoreUserStoreAuthority(authorityDer, log);
                return;
            }

            var removal = (ExitCode: -1, Output: string.Empty);
            try
            {
                using var lm = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                lm.Open(OpenFlags.ReadWrite);
                var found = lm.Certificates.Find(X509FindType.FindByThumbprint, authorityThumbprint, false);
                foreach (var c in found.OfType<X509Certificate2>())
                {
                    lm.Remove(c);
                }

                removal = (0, $"direct elevated removal of {found.Count} copy/copies from LocalMachine\\Root");
            }
            catch (Exception direct)
            {
                log.AppendLine($"direct LocalMachine\\Root removal unavailable ({direct.Message}); trying the elevated localhost remote channel...");
                removal = RunElevatedlocalhostStoreOperation(config, authorityDer, authorityThumbprint, remove: true, log);
            }
            if (removal.ExitCode != 0)
            {
                RestoreUserStoreAuthority(authorityDer, log);
                skippedReason = $"LocalMachine\\Root removal needs elevation and the unattended channel failed (exit {removal.ExitCode}): {Flatten(removal.Output)}";
                return;
            }

            lmRemoved = true;
            if (LocalMachineRootContains(authorityThumbprint, log) || UserStoreContains(authorityThumbprint))
            {
                failures.Add("the CA is still in a root store after removal - the untrusted state was never established");
                throw new InvalidOperationException(failures.Last());
            }

            log.AppendLine("untrusted state established (CA absent from BOTH root stores); launching Smart Client...");

            previousHome = WorkspaceHomeAddress.Ensure(config, ProxiedHttpsHome, log);

            CdpConnector.CleanStalePortFiles(config.WebView2DataRoot);
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");

            sc = ClientSession.Launch(config.SmartClientExe);
            var login = LoginFlow.WaitForLoginWindow(sc);
            LoginFlow.Login(sc, login, config);
            LoginFlow.AcceptCertificateTrust(sc);
            var main = LoginFlow.WaitForSmartClientMain(sc);
            SmartClientUi.Maximize(main);
            Thread.Sleep(1500);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

            main = SmartClientUi.SelectWorkspace(main, sc, config.WorkspaceName);
            var item = SmartClientUi.WaitForMipViewItem(main);
            Assert.NotNull(item);

            // The mint publishes the posture as soon as the first https tunnel terminates; the
            // banner must appear for this ORDINARY user (no setup needed to see it).
            var banner = WaitForElement(item, "proxyCaBanner", 60);
            log.AppendLine(banner == null
                ? "proxyCaBanner NEVER appeared in the UIA tree"
                : $"proxyCaBanner appeared (IsOffscreen={banner.IsOffscreen})");
            if (banner == null) failures.Add("banner missing in the live untrusted state");

            var bannerText = item.FindFirstDescendant(cf => cf.ByAutomationId("proxyCaBannerText"));
            var bannerMessage = bannerText?.Name;
            log.AppendLine($"banner text: '{bannerMessage}'");
            if (bannerMessage == null || !bannerMessage.Contains("not trusted", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("the banner text does not state the not-trusted condition");
            }

            // Browse mode: the fix affordance must be ABSENT for this user (it is setup-only).
            var fixBrowse = item.FindFirstDescendant(cf => cf.ByAutomationId("proxyCaFixButton"));
            log.AppendLine($"browse mode -> proxyCaFixButton present: {fixBrowse != null}");
            if (fixBrowse != null) failures.Add("the setup-only fix button was visible outside setup mode");

            Capture.Element(main).ToFile(Artifacts.PathOf("sc-trust-probe-untrusted-banner.png"));
            Thread.Sleep(250);

            // Dismiss for this session.
            var dismiss = item.FindFirstDescendant(cf => cf.ByAutomationId("proxyCaDismissButton"));
            if (dismiss == null) failures.Add("no dismiss affordance on the banner");
            else
            {
                Assert.True(SmartClientUi.TryInvokeById(main, "proxyCaDismissButton"), "dismiss button did not invoke");
                Thread.Sleep(2000);
                var afterDismiss = item.FindFirstDescendant(cf => cf.ByAutomationId("proxyCaBanner"));
                log.AppendLine($"after dismiss -> proxyCaBanner present: {afterDismiss != null}");
                if (afterDismiss != null) failures.Add("the banner survived the dismiss for this session");
            }

            // Setup mode: the same banner condition now ALSO reveals the fix button. (NOT clicked:
            // the UAC consent would block on the secure desktop unattended; declined/confirmed
            // outcomes are unit+guard pinned - see docs/verification-matrix.md human step.)
            SmartClientUi.ClickSetup(main);
            Thread.Sleep(4000);
            main = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            var item2 = SmartClientUi.WaitForMipViewItem(main);
            var fixSetup = item2?.FindFirstDescendant(cf => cf.ByAutomationId("proxyCaFixButton"));
            log.AppendLine($"setup mode -> proxyCaFixButton present: {fixSetup != null}");
            if (fixSetup == null) failures.Add("the fix button did not appear in setup mode while the CA is untrusted");
            else
            {
                log.AppendLine($"fix button name: '{fixSetup.Name}'");
            }

            Capture.Element(main).ToFile(Artifacts.PathOf("sc-trust-probe-untrusted-setup-fix.png"));

            var markerFound = MipLogContains("not trusted on this machine") || MipLogContains("trust posture");
            log.AppendLine(markerFound
                ? "MIPLog.txt carries the trust-posture marker"
                : "NOTE: no trust-posture marker found in the Smart Client MIPLog.txt snapshot (logging may be disabled or rotated)");
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
            log.AppendLine($"probe failed: {ex}");
        }
        finally
        {
            ProcessKiller.KillClients("Client");
            ProcessKiller.KillClients("VideoOS.Administration");
            ProcessKiller.KillPluginWebView2Browsers("SmartClient.WebContentData");
            sc?.Dispose();
            if (previousHome != null)
            {
                try { WorkspaceHomeAddress.Ensure(config, previousHome.Trim(), log); } catch (Exception ex) { log.AppendLine($"!!! home restore FAILED: {ex.Message}"); }
            }

            // Restore the machine to the trusted lab state BEFORE anything else: the CA must be
            // back in both stores or every later https probe breaks.
            if (authorityDer != null && authorityThumbprint != null)
            {
                try
                {
                    RestoreUserStoreAuthority(authorityDer, log);
                    if (lmRemoved)
                    {
                        var restore = TryDirectRestore(authorityDer, authorityThumbprint, log);
                        if (restore.ExitCode != 0)
                        {
                            restore = RunElevatedlocalhostStoreOperation(config, authorityDer, authorityThumbprint, remove: false, log);
                        }

                        if (restore.ExitCode != 0 || !LocalMachineRootContains(authorityThumbprint, log))
                        {
                            log.AppendLine($"!!! LocalMachine\\Root restore FAILED - manual step required: import the CA DER (thumbprint {authorityThumbprint}) back into LocalMachine\\Root. exit={restore.ExitCode} {Flatten(restore.Output)}");
                            failures.Add("LocalMachine\\Root restore failed - manual re-import required on this machine");
                        }
                        else
                        {
                            log.AppendLine("deployment CA restored to LocalMachine\\Root and CurrentUser\\Root");
                        }
                    }
                    else
                    {
                        log.AppendLine("machine store was never touched; user store restored");
                    }
                }
                catch (Exception ex)
                {
                    log.AppendLine($"!!! authority restore FAILED: {ex.Message}");
                }
            }

            var result = new StringBuilder();
            if (skippedReason != null)
            {
                result.AppendLine($"SKIPPED: {skippedReason}");
                result.AppendLine("The untrusted-state live row needs an elevated removal from LocalMachine\\Root, which this unattended channel could not perform; the human-step procedure is documented in docs/verification-matrix.md (SC-10).");
            }
            result.Append(log);
            Artifacts.WriteText("sc-trust-probe-untrusted-banner-results.txt", result.ToString());
            _output.WriteLine(result.ToString());
        }

        if (skippedReason != null)
        {
            return; // opt-in row honestly skipped: no live mutation happened, nothing to assert.
        }

        Assert.True(failures.Count == 0, $"untrusted-banner live failures:{Environment.NewLine}" + string.Join(Environment.NewLine, failures) + Environment.NewLine + log);
    }

    // -----------------------------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------------------------

    private static AutomationElement WaitForElement(AutomationElement scope, string automationId, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var found = scope.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (found != null) return found;
            Thread.Sleep(1000);
        }

        return null;
    }

    private static bool RootStoreContainsCaCn(StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Find(X509FindType.FindBySubjectName, DeploymentCaCommonName, false).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool LocalMachineRootContains(string thumbprint, StringBuilder log)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
        }
        catch (Exception ex)
        {
            log.AppendLine($"LocalMachine\\Root read failed: {ex.Message}");
            return false;
        }
    }

    private static bool UserStoreContains(string thumbprint)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
    }

    private static void RestoreUserStoreAuthority(byte[] der, StringBuilder log)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, new X509Certificate2(der).Thumbprint, false).Count == 0)
            {
                store.Add(new X509Certificate2(der));
                log.AppendLine("deployment CA re-added to CurrentUser\\Root");
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"!!! CurrentUser\\Root restore FAILED: {ex.Message}");
        }
    }

    private static (int ExitCode, string Output) TryDirectRestore(byte[] der, string thumbprint, StringBuilder log)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count == 0)
            {
                store.Add(new X509Certificate2(der));
                log.AppendLine("deployment CA re-added directly to LocalMachine\\Root");
            }

            return (0, "direct restore");
        }
        catch (Exception ex)
        {
            return (-1, $"direct LocalMachine\\Root restore unavailable: {ex.Message}");
        }
    }

    /// <summary>Elevated localhost remote session (jhadmin from .env/SecretStore): WinRM sessions
    /// for administrators are already elevated, so the store Remove/Add runs unattended with no
    /// UAC and no crypt32 prompt (no interactive desktop) - the same mechanism WssTargetTrust
    /// uses against the Event Server.</summary>
    private static (int ExitCode, string Output) RunElevatedlocalhostStoreOperation(
        UxConfig config, byte[] der, string thumbprint, bool remove, StringBuilder log)
    {
        var derBase64 = Convert.ToBase64String(der);
        var flagLiteral = remove ? "$true" : "$false";
        var script = $@"
$ErrorActionPreference = 'Stop'
$password = ConvertTo-SecureString $env:WEBCONTENT_ES_PASSWORD -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($env:WEBCONTENT_ES_USER, $password)
$session = New-PSSession -ComputerName localhost -Credential $credential
try {{
    Invoke-Command -Session $session -ArgumentList ([Convert]::FromBase64String($env:WEBCONTENT_CA_DER)), '{thumbprint}', {flagLiteral} -ScriptBlock {{
        param($certificateBytes, $expectedThumbprint, $removeIt)
        $ErrorActionPreference = 'Stop'
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(,$certificateBytes)
        if ($certificate.Thumbprint -ne $expectedThumbprint) {{ throw ('DER/thumbprint drift: ' + $certificate.Thumbprint) }}
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
        $store.Open('ReadWrite')
        try {{
            if ($removeIt) {{
                $found = $store.Certificates.Find('FindByThumbprint', $expectedThumbprint, $false)
                foreach ($c in $found) {{ $store.Remove($c) }}
                Write-Output ('removed ' + $found.Count + ' copy/copies from LocalMachine\Root (elevated)')
                if (($store.Certificates.Find('FindByThumbprint', $expectedThumbprint, $false)).Count -ne 0) {{
                    throw 'post-condition failed: still present after removal'
                }}
            }} else {{
                if (-not ($store.Certificates.Find('FindByThumbprint', $expectedThumbprint, $false)).Count) {{
                    $store.Add($certificate)
                    Write-Output 're-imported into LocalMachine\Root (elevated)'
                }} else {{ Write-Output 'already present' }}
                if (($store.Certificates.Find('FindByThumbprint', $expectedThumbprint, $false)).Count -eq 0) {{
                    throw 'post-condition failed: absent after add'
                }}
            }}
        }} finally {{ $store.Close() }}
    }}
}} finally {{
    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
}}
";

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShell(),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["WEBCONTENT_ES_USER"] = config.VmsUsername;
        startInfo.Environment["WEBCONTENT_ES_PASSWORD"] = config.VmsPassword;
        startInfo.Environment["WEBCONTENT_CA_DER"] = derBase64;

        try
        {
            using var process = Process.Start(startInfo);
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            foreach (var line in stdout.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line)) log.AppendLine("  elevated-store: " + line.Trim());
            }

            return (process.ExitCode, string.IsNullOrEmpty(stderr.Trim()) ? stdout : stderr.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static (int ExitCode, string Output) RunHelper(string helperPath, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static void ExpectExit((int ExitCode, string Output) run, int expected, string label, StringBuilder log, List<string> failures)
    {
        log.AppendLine($"{label}: exit={run.ExitCode} (expected {expected})");
        if (run.ExitCode != expected)
        {
            failures.Add($"{label}: exit {run.ExitCode}, expected {expected}; output: {Flatten(run.Output)}");
        }
    }

    private static (string DerPath, string Thumbprint) CreateThrowawayCertificate(string directory)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=" + ProbeCertificateCommonName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(directory, "real.der");
        File.WriteAllBytes(path, certificate.RawData);
        return (path, certificate.Thumbprint);
    }

    private static void LogVersion(string executable, StringBuilder log)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executable);
            log.AppendLine($"helper file version: {info.FileVersion}, sha256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)))}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"(version read failed: {ex.Message})");
        }
    }

    private static string Flatten(string text)
    {
        return (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string ResolvePowerShell()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh)) return pwsh;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    /// <summary>Searches the Smart Client MIP logs (MIPLog.txt plus rolling MIPLogs copies).</summary>
    private static bool MipLogContains(string marker)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Milestone", "XProtect Smart Client");
        var candidates = new List<string>();
        try
        {
            var top = Path.Combine(root, "MIPLog.txt");
            if (File.Exists(top)) candidates.Add(top);
            var rollDir = Path.Combine(root, "MIPLogs");
            if (Directory.Exists(rollDir)) candidates.AddRange(Directory.EnumerateFiles(rollDir, "MIPLog*.txt"));
        }
        catch { return false; }

        foreach (var file in candidates)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Contains(marker, StringComparison.Ordinal)) return true;
                }
            }
            catch { /* locked mid-write or unreadable: try the next file */ }
        }

        return false;
    }
}
