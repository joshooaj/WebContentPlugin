using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests
{
    /// <summary>
    /// LIVE (#80) probes for the untrusted-CA machine aggregate on the Management Client's
    /// Diagnostics tab: clients that hit "the loopback proxy's certificate is not trusted on
    /// THIS machine" report it once per trust flip to the Event Server, which keeps a per-
    /// workspace table of MACHINES (ten views on one workstation count once), and the Diagnostics
    /// tab surfaces it as a banner naming the machines with the #75 deployment-CA export button
    /// inline - the operator exports once and distributes to the fleet.
    ///
    /// Row 1 (runs in every suite run, non-mutating): with the lab's healthy trust state the
    /// banner is NOT shown - no aggregate text, the export button off-screen - while the rest of
    /// the Diagnostics tab renders. A CA-banner false positive on a healthy machine is exactly
    /// what an admin would drown in, so this half runs unguarded.
    ///
    /// Row 2 (guarded: WEBCONTENT_CA_UNTRUST_LIVE=1, same gate as the #72 SC-10 live row): the
    /// FULL cross-machine chain on the real lab. The deployment CA is pulled from BOTH root
    /// stores of this workstation, a Smart Client proxied view loads (the listener's posture
    /// flips and the ONE CertUntrusted report files), and the Management Client's Diagnostics
    /// banner must then name THIS machine - then the inline export writes the deployment CA file
    /// an operator would distribute. The CA stores are restored afterwards; the banner itself
    /// stays until an Event Server restart by design (the machine table is in-memory aggregate
    /// state, and re-flips dedup to one row per machine anyway). The CurrentUser\Root half is
    /// store-protected and Access-Denies for an unattended shell (verified live, same lesson as
    /// SC-10): the row then restores whatever it touched, records SKIPPED with the exact reason,
    /// and passes without asserting - the elevated human-step procedure lives in
    /// docs/verification-matrix.md. Nothing is left mutated on the skip route.
    /// </summary>
    public class McCaUntrustedAggregateProbeTests
    {
        private const string McCaBannerAutomationId = "caUntrustedBanner";
        private const string McCaBannerTextAutomationId = "caUntrustedBannerText";

        // The #75 Save-As dialog's default file name (ProxyCaExportViewBuilder pins it).
        private const string ExportDialogFile = "Milestone XProtect WebContent Proxy CA.cer";

        // The Diagnostics banner's export button AutomationId (pinned by the #80 guard tests).
        private const string McCaExportAutomationId = "proxyCaExportFromDiagnostics";

        private const string EsMarkerCaReport = "Proxy CA trust report recorded";
        private const string DeploymentCaSubject = "Milestone XProtect WebContent Proxy CA";

        private static bool UntrustedRowEnabled =>
            Environment.GetEnvironmentVariable("WEBCONTENT_CA_UNTRUST_LIVE") == "1";

        private readonly ITestOutputHelper _output;

        public McCaUntrustedAggregateProbeTests(ITestOutputHelper output) => _output = output;

        // =====================================================================================
        // Row 1: trusted lab state -> the Diagnostics tab shows NO CA banner.
        // =====================================================================================

        [Fact]
        [Trait("Category", "probe")]
        public void ProbeDiagnosticsTabShowsNoCaBannerWhileTheCaIsTrusted()
        {
            var config = UxConfig.Load();
            Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-ca-aggregate-probe"));
            _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

            var log = new StringBuilder();
            Window mcMain = null;
            ClientSession mc = null;
            try
            {
                mc = ClientSession.Launch(config.AdminClientExe);
                var login = LoginFlow.WaitForLoginWindow(mc);
                LoginFlow.Login(mc, login, config);
                LoginFlow.AcceptCertificateTrust(mc);
                mcMain = LoginFlow.WaitForAdminMain(mc);
                ManagementClientUi.CloseStrayLoginDialogs(mc);
                ManagementClientUi.WaitForTreeReady(mcMain);
                SmartClientUi.Maximize(mcMain);
                Thread.Sleep(2500);

                ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
                ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
                ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
                ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);
                Thread.Sleep(1500);
                SelectTabByAutomationId(mcMain, "tabDiagnostics");
                ManagementClientUi.ExpectControl(mcMain, "dgDiagnosticsBlocked", 15);

                // The banner peers EXIST (WPF collapsed elements still answer UIA in the MC pane -
                // the MC-16 quirk), so 'not shown' = the banner reports off-screen and no visible
                // pane text carries the banner's wording. ReadVisiblePaneText discipline.
                Assert.True(WaitUntil(() => !CaBannerVisible(mcMain), 10),
                    "the untrusted-CA banner is VISIBLE while the deployment CA is trusted - a healthy lab must never show the aggregate warning");
                log.AppendLine("banner not shown while the CA is trusted (off-screen banner + no visible aggregate text)");

                var export = Find(mcMain, McCaExportAutomationId);
                Assert.NotNull(export);
                Assert.True(SafeOffscreen(export),
                    "the Diagnostics CA export button is on-screen while no machine has reported - it belongs inside the collapsed banner");
                log.AppendLine("the inline export button is parked inside the collapsed banner while healthy");

                // The tab itself still works (the banner must not displace the blocked ring).
                ManagementClientUi.ExpectControl(mcMain, "txtDiagnosticsSummary", 5);
                log.AppendLine("Diagnostics tab renders normally with the banner collapsed");
                Artifacts.Window(mcMain, "mc-ca-healthy");
            }
            finally
            {
                Artifacts.WriteText("mc-ca-healthy-results.txt", log.ToString());
                _output.WriteLine(log.ToString());
                try { mcMain?.Close(); } catch { }
                if (mc != null) ManagementClientUi.RejectPendingModal(mc, 5);
                mc?.Dispose();
            }
        }

        // =====================================================================================
        // Row 2 (guarded): full untrusted-report -> machine aggregate -> inline export chain.
        // =====================================================================================

        [Fact]
        [Trait("Category", "probe")]
        public void ProbeUntrustedCaReportAggregatesIntoTheDiagnosticsBanner()
        {
            var config = UxConfig.Load();
            Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-ca-aggregate-probe"));
            _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

            if (!UntrustedRowEnabled)
            {
                _output.WriteLine("SKIPPED (opt-in row): set WEBCONTENT_CA_UNTRUST_LIVE=1 to mutate this machine's root stores for the #80 aggregate live row.");
                return;
            }

            var log = new StringBuilder();
            ClientSession sc = null;
            Window mcMain = null;
            ClientSession mc = null;
            byte[] authorityDer = null;
            string authorityThumbprint = null;
            var userRemoved = false;
            var lmRemoved = false;
            string skippedReason = null;
            var failures = new List<string>();
            try
            {
                // ---- 0. Locate the deployment CA (the plugin's minting authority, #56). ----
                using (var cu = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    cu.Open(OpenFlags.ReadWrite);
                    var found = cu.Certificates
                        .Find(X509FindType.FindBySubjectName, DeploymentCaSubject, false)
                        .OfType<X509Certificate2>()
                        .FirstOrDefault();
                    if (found == null)
                    {
                        skippedReason = $"no '{DeploymentCaSubject}' certificate in CurrentUser\\Root - this machine is not in the CA'd lab posture";
                        return;
                    }

                    authorityDer = found.RawData;
                    authorityThumbprint = found.Thumbprint;
                    log.AppendLine($"deployment CA {authorityThumbprint} found in CurrentUser\\Root");
                }

                // The elevated restore re-adds by thumbprint from a DER copy kept in the
                // artifacts folder (the trust decision only needs the public certificate).
                var derPath = Artifacts.PathOf("deployment-authority.der");
                File.WriteAllBytes(derPath, authorityDer);

                // ---- 1. Untrusted state: pull the CA from BOTH root stores. ----
                try
                {
                    using (var cu = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                    {
                        cu.Open(OpenFlags.ReadWrite);
                        foreach (var c in cu.Certificates.Find(X509FindType.FindByThumbprint, authorityThumbprint, false).OfType<X509Certificate2>())
                        {
                            cu.Remove(c);
                        }
                    }

                    userRemoved = true;
                    log.AppendLine("deployment CA removed from CurrentUser\\Root");
                }
                catch (Exception ex)
                {
                    // Same live-verified lesson as SC-10: the CA entry is store-protected, an
                    // unattended shell Access-Denies. Stores untouched -> honest skip.
                    skippedReason = $"CurrentUser\\Root removal refused unattended ({ex.Message}) - run this row from an elevated joshooaj shell for the #80 live aggregate";
                    return;
                }

                var lm = (ExitCode: -1, Output: string.Empty);
                try
                {
                    using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadWrite);
                    foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, authorityThumbprint, false).OfType<X509Certificate2>())
                    {
                        store.Remove(c);
                    }

                    lm = (0, "direct elevated removal");
                }
                catch (Exception direct)
                {
                    log.AppendLine($"direct LocalMachine\\Root removal unavailable ({direct.Message}); trying the elevated localhost remote channel...");
                    lm = RunElevatedLocalhostStoreOperation(config, authorityDer, authorityThumbprint, remove: true, log);
                }

                if (lm.ExitCode != 0)
                {
                    RestoreUserStoreAuthority(authorityDer, log);
                    userRemoved = false;
                    skippedReason = $"LocalMachine\\Root removal needs elevation and the unattended channel failed (exit {lm.ExitCode}): {Flatten(lm.Output)}";
                    return;
                }

                lmRemoved = true;
                log.AppendLine("deployment CA removed from LocalMachine\\Root - the machine is now in the untrusted posture");

                // ---- 2. The client: a proxied view loading while untrusted must file exactly
                // ONE CertUntrusted report (the flip report). ----
                var esBefore = EsLogCount(config, EsMarkerCaReport, config.McWorkspaceName);
                sc = ClientSession.Launch(config.SmartClientExe);
                var scLogin = LoginFlow.WaitForLoginWindow(sc);
                LoginFlow.Login(sc, scLogin, config);
                LoginFlow.AcceptCertificateTrust(sc);
                var scMain = LoginFlow.WaitForSmartClientMain(sc);
                SmartClientUi.Maximize(scMain);
                Thread.Sleep(1500);
                scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");

                AutomationElement item = null;
                try { item = SmartClientUi.WaitForMipViewItem(scMain, 20); } catch (TimeoutException) { }
                if (item == null)
                {
                    if (SmartClientUi.TrySelectWorkspaceWithoutInput(scMain, sc, config.WorkspaceName))
                    {
                        scMain = sc.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
                    }

                    item = SmartClientUi.WaitForMipViewItem(scMain);
                }

                Assert.NotNull(item);
                log.AppendLine("proxied workspace view is up while the CA is NOT trusted (flip report due)");

                var reportLine = WaitForEsLine(config, EsMarkerCaReport, esBefore, 90, config.McWorkspaceName);
                Assert.NotNull(reportLine);
                log.AppendLine($"ES marker: {Flatten(reportLine)}");
                Assert.Contains(Environment.MachineName, reportLine, StringComparison.OrdinalIgnoreCase);
                log.AppendLine($"the ES machine table names this workstation: {Environment.MachineName}");

                // ---- 3. The Management Client: the Diagnostics banner names the machine. ----
                mc = ClientSession.Launch(config.AdminClientExe);
                var mcLogin = LoginFlow.WaitForLoginWindow(mc);
                LoginFlow.Login(mc, mcLogin, config);
                LoginFlow.AcceptCertificateTrust(mc);
                mcMain = LoginFlow.WaitForAdminMain(mc);
                ManagementClientUi.CloseStrayLoginDialogs(mc);
                ManagementClientUi.WaitForTreeReady(mcMain);
                SmartClientUi.Maximize(mcMain);
                Thread.Sleep(2500);
                ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
                ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
                ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
                ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);
                Thread.Sleep(1500);
                SelectTabByAutomationId(mcMain, "tabDiagnostics");
                ManagementClientUi.ExpectControl(mcMain, "dgDiagnosticsBlocked", 15);

                var bannerText = WaitValue(() => CaBannerVisible(mcMain) ? PaneBannerText(mcMain) : null, 30);
                Assert.NotNull(bannerText);
                log.AppendLine($"banner text: {bannerText}");
                Assert.Contains("client machine", bannerText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(Environment.MachineName, bannerText, StringComparison.OrdinalIgnoreCase);
                log.AppendLine("the banner names the aggregate as machine reports and names THIS machine");
                Artifacts.Window(mcMain, "mc-ca-banner");

                // ---- 4. The inline export: one click writes the distributable CA file. ----
                var export = IfVisible(Find(mcMain, McCaExportAutomationId));
                Assert.NotNull(export);
                var tempDir = Path.Combine(Path.GetTempPath(), "webcontent-ca-export");
                Directory.CreateDirectory(tempDir);
                var certPath = Path.Combine(tempDir, ExportDialogFile);
                File.Delete(certPath);

                InvokeButton(export);
                var saved = AcceptSaveAsDialog(mc, certPath, 20);
                Assert.True(saved, "the Diagnostics banner's export button never produced the #75 Save-As dialog");
                Assert.True(WaitUntil(() => File.Exists(certPath), 10), $"the export never wrote '{certPath}'");

                var exported = new X509Certificate2(File.ReadAllBytes(certPath));
                Assert.Equal(authorityThumbprint, exported.Thumbprint);
                log.AppendLine($"the banner's export wrote the deployment CA ({exported.Thumbprint}) - the distributable file");
                try { File.Delete(certPath); } catch { }
                Artifacts.Window(mcMain, "mc-ca-export");

                // Dedup honesty: the banner listed THIS machine exactly once even though the
                // Smart Client's view (and any older flip) share this workstation.
                Assert.Equal(1, CountOccurrences(bannerText, Environment.MachineName));
                log.AppendLine("one machine row for this workstation regardless of session count");
            }
            catch (Exception ex)
            {
                failures.Add($"{ex.GetType().Name}: {ex.Message}");
                log.AppendLine($"probe failed: {ex}");
            }
            finally
            {
                try { mcMain?.Close(); } catch { }
                if (mc != null)
                {
                    ManagementClientUi.RejectPendingModal(mc, 5);
                    mc.Dispose();
                }

                try { sc?.Dispose(); } catch { }
                ProcessKiller.KillClients("Client", "VideoOS.Administration");

                // ---- Restore the stores (CA back in both) even on a mid-row failure. ----
                if (userRemoved)
                {
                    RestoreUserStoreAuthority(authorityDer, log);
                }

                if (lmRemoved)
                {
                    var restore = RunElevatedLocalhostStoreOperation(config, authorityDer, authorityThumbprint, remove: false, log);
                    if (restore.ExitCode != 0)
                    {
                        log.AppendLine($"!!! LocalMachine\\Root restore via the elevated channel FAILED (exit {restore.ExitCode}): {Flatten(restore.Output)} - import the deployment CA manually !!!");
                    }
                    else
                    {
                        log.AppendLine("deployment CA restored to LocalMachine\\Root");
                    }
                }

                Artifacts.WriteText("mc-ca-aggregate-results.txt", log.ToString());
                _output.WriteLine(log.ToString());

                if (skippedReason != null)
                {
                    _output.WriteLine($"SKIPPED: {skippedReason}");
                }
                else
                {
                    Assert.True(failures.Count == 0,
                        $"#80 aggregate chain failures:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}{Environment.NewLine}{log}");
                }
            }
        }

        // =====================================================================================
        // Helpers
        // =====================================================================================

        private static bool CaBannerVisible(Window mcMain)
        {
            var banner = Find(mcMain, McCaBannerAutomationId);
            if (banner == null) return false;
            try { if (banner.IsOffscreen) return false; } catch { }
            var rect = SafeRect(banner);
            return !rect.IsEmpty && rect.Width > 0 && rect.Height > 0;
        }

        private static string PaneBannerText(Window mcMain)
        {
            var banner = Find(mcMain, McCaBannerAutomationId);
            if (banner == null) return null;
            var texts = banner.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                              .Select(t => t.Name)
                              .Where(n => !string.IsNullOrWhiteSpace(n));
            var joined = string.Join(" ", texts);
            if (!string.IsNullOrWhiteSpace(joined)) return joined;

            // A collapsed WPF TextBlock answers UIA with an EMPTY Name even though its peer
            // exists (live MC-pane lesson); when the pane is expanded the summary TextBlock's
            // own Name carries the banner sentence - take it too.
            var summary = Find(mcMain, McCaBannerTextAutomationId);
            var name = summary?.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                at += needle.Length;
            }

            return count;
        }

        private static void InvokeButton(AutomationElement button)
        {
            try { button.Patterns.Invoke.Pattern.Invoke(); }
            catch { button.Click(); }
        }

        private static void RestoreUserStoreAuthority(byte[] der, StringBuilder log)
        {
            try
            {
                using var cu = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                cu.Open(OpenFlags.ReadWrite);
                cu.Add(new X509Certificate2(der));
                log.AppendLine("deployment CA restored to CurrentUser\\Root");
            }
            catch (Exception ex)
            {
                log.AppendLine($"!!! CurrentUser\\Root restore FAILED: {ex.Message} !!!");
            }
        }

        /// <summary>The SC-10 elevated-localhost convention (live-verified): WinRM sessions for
        /// the configured VMS admin are already elevated, so LocalMachine\Root surgery runs
        /// unattended with no UAC and no crypt32 prompt. No channel can substitute THIS user's
        /// CurrentUser\Root - that half stays a human step.</summary>
        private static (int ExitCode, string Output) RunElevatedLocalhostStoreOperation(
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
                FileName = "powershell",
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
                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch { }
                    return (-1, "timeout");
                }

                return (process.ExitCode, stdout + " " + stderr);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        private static int EsLogCount(UxConfig config, params string[] fragments) =>
            EsLogLines(config, fragments).Count;

        private static string WaitForEsLine(UxConfig config, string marker, int before, int timeoutSeconds, params string[] andAlso)
        {
            var fragments = new List<string> { marker };
            fragments.AddRange(andAlso);
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var lines = EsLogLines(config, fragments.ToArray());
                if (lines.Count > before) return lines[lines.Count - 1];
                Thread.Sleep(1500);
            }

            return null;
        }

        /// <summary>The Event Server's MIP.log read over the WinRM admin channel (the machine
        /// table lives only in ES memory; its log is the observable channel).</summary>
        private static List<string> EsLogLines(UxConfig config, string[] fragments)
        {
            var where = string.Join(" -and ", fragments.Select(f => $"$_ -like '*{EscapeLike(f)}*'"));
            var script = @"
$ErrorActionPreference = 'Stop'
$password = ConvertTo-SecureString $env:WEBCONTENT_ES_PASSWORD -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($env:WEBCONTENT_ES_USER, $password)
$session = New-PSSession -ComputerName localhost -Credential $credential
try {
    Invoke-Command -Session $session -ScriptBlock {
        $lines = Get-Content ""$env:ProgramData\Milestone\XProtect Event Server\logs\MIP.log"" -ErrorAction SilentlyContinue
        $lines | Where-Object { " + where + @" }
    }
} finally {
    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
}
";
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.Environment["WEBCONTENT_ES_USER"] = config.VmsUsername;
            startInfo.Environment["WEBCONTENT_ES_PASSWORD"] = config.VmsPassword;

            try
            {
                using var process = Process.Start(startInfo);
                var stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit(60000);
                return stdout
                    .Replace("\r", string.Empty)
                    .Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string EscapeLike(string fragment) =>
            fragment.Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("?", "[?]");

        private static bool AcceptSaveAsDialog(ClientSession session, string targetPath, int timeoutSeconds)
        {
            // The #75 export's SaveFileDialog: a Win32 dialog of the MC process. Type the full
            // path into the File name edit and press Save.
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var window in session.Windows())
                {
                    try
                    {
                        var title = window.Title ?? string.Empty;
                        if (!title.Contains("Save", StringComparison.OrdinalIgnoreCase) &&
                            !title.Contains("export", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var edit = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
                        var save = window.FindFirstDescendant(cf => cf.ByName("Save"));
                        if (edit == null || save == null) continue;
                        edit.Patterns.Value.Pattern.SetValue(targetPath);
                        InvokeButton(save);
                        return true;
                    }
                    catch { }
                }

                Thread.Sleep(500);
            }

            return false;
        }

        private static void SelectTabByAutomationId(Window window, string tabAutomationId)
        {
            var tab = Find(window, tabAutomationId);
            if (tab == null) throw new InvalidOperationException($"tab '{tabAutomationId}' not found");
            InvokeButton(tab);
        }

        private static AutomationElement Find(Window window, string automationId) =>
            window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

        private static AutomationElement IfVisible(AutomationElement element)
        {
            if (element == null) return null;
            var rect = SafeRect(element);
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return null;
            try { if (element.IsOffscreen) return null; } catch { }
            return element;
        }

        private static System.Drawing.Rectangle SafeRect(AutomationElement element)
        {
            try
            {
                var r = element.BoundingRectangle;
                return r == null ? System.Drawing.Rectangle.Empty : new System.Drawing.Rectangle(r.X, r.Y, r.Width, r.Height);
            }
            catch { return System.Drawing.Rectangle.Empty; }
        }

        private static bool SafeOffscreen(AutomationElement element)
        {
            try { return element.IsOffscreen; }
            catch
            {
                var rect = SafeRect(element);
                return rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0;
            }
        }

        private static string Flatten(string text) =>
            (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

        private static T WaitValue<T>(Func<T> read, int timeoutSeconds) where T : class
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var value = read();
                if (value != null) return value;
                Thread.Sleep(1000);
            }

            return null;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try { if (condition()) return true; } catch { }
                Thread.Sleep(1000);
            }

            return false;
        }

        private static (int ExitCode, string Output) Run(string file, string args, int timeoutSeconds)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var p = Process.Start(psi);
                var output = p.StandardOutput.ReadToEnd();
                var error = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutSeconds * 1000))
                {
                    try { p.Kill(); } catch { }
                    return (-1, $"timeout: {output} {error}");
                }

                return (p.ExitCode, output + " " + error);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }
    }
}
