using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

using WebContent.UxTests.Harness;

using Xunit;
using Xunit.Abstractions;

namespace WebContent.UxTests;

/// <summary>
/// Ticket #75 probe (MC-15): the Management Client's "Export proxy certificate" button on the Web
/// Content Workspaces overview asks the Event Server over MIP messaging and writes what it gets as a
/// Save As *.cer (DER). Proves live: the button realizes on the overview page; the server answers
/// (the MC's own MIP.log carries the answered marker with the thumbprint and DER size); the bytes on
/// disk are byte-identical to the deployment CA in the Event Server machine's LocalMachine\Personal
/// store - a CA this build did NOT create, so the export works whatever provisioned it (the ticket's
/// provenance AC); the Management Client logs the answer and the save; and the pane reports the saved
/// path plus thumbprint instead of an empty file. Read-only: no configuration is saved, no certificate
/// store is written, and the only file produced is inside the artifacts folder.
/// </summary>
public class McCaExportProbeTests
{
    private const string DefaultFileName = "WebContent-Proxy-Authority.cer";

    private readonly ITestOutputHelper _output;

    public McCaExportProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "probe")]
    public void ProbeProxyCaExportSavesTheEventServerAuthorityAsDer()
    {
        var config = UxConfig.Load();
        Artifacts.Init(Path.Combine(Path.GetTempPath(), "webcontent-mc-ca-export-probe"));
        _output.WriteLine($"Artifacts: {Artifacts.RunDirectory}");

        var log = new StringBuilder();

        // Server side FIRST: an export that matches a stale view of the store proves nothing, so the
        // store copy is read before the button is pressed.
        var serverAuthority = EventServerAuthority.ReadDeploymentAuthority(config, log);
        Assert.NotNull(serverAuthority);
        log.AppendLine($"event server authority before the export: thumbprint {serverAuthority.Thumbprint}, " +
                       $"{serverAuthority.RawData.Length} DER bytes, subject {serverAuthority.Subject}");

        ProcessKiller.KillClients("VideoOS.Administration");
        var session = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(session);
        LoginFlow.Login(session, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(session);
        var mcMain = LoginFlow.WaitForAdminMain(session);
        ManagementClientUi.CloseStrayLoginDialogs(session);
        ManagementClientUi.WaitForTreeReady(mcMain);

        var savedPath = Path.Combine(Artifacts.RunDirectory, "exported-authority.cer");
        try
        {
            ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
            ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
            ManagementClientUi.SelectTreeItem(mcMain, ManagementClientUi.WorkspacesNodeName);

            // 1. The overview page of the plugin's workspaces node carries the export button.
            var button = ManagementClientUi.ExpectControl(mcMain, "proxyCaExportButton", 30);
            log.AppendLine($"overview page shows the export button (name '{button.Name}')");
            Artifacts.WindowPrint(mcMain, "mc-ca-export-overview");

            var answeredBefore = McLog.CountMatches("Proxy authority certificate answered:");

            // 2. Press it. The page disables the button and shows the "asking" line while the
            //    messaging exchange runs, then opens the Save As dialog with the default name.
            button.Patterns.Invoke.Pattern.Invoke();
            log.AppendLine("invoked proxyCaExportButton");

            // 3. Either the Save As dialog opens (the server answered) or the pane's failure banner
            //    does. Both are asserted outcomes; a silent timeout is the failure, and the journal
            //    below says which one it was.
            var dialog = WaitForSaveDialog(session, mcMain, log, 90);
            // PrintWindow capture, not CopyFromScreen: this machine's desktop session can be
            // disconnected, where the screen-surface capture hangs (the #83 run learned this).
            Artifacts.WindowPrint(dialog.AsWindow(), "mc-ca-export-save-dialog");
            log.AppendLine($"save dialog open: name '{dialog.Name}', class '{dialog.ClassName}', pid {dialog.Properties.ProcessId.Value}");

            // 4. Name the file inside the artifacts folder and commit the dialog.
            var fileNameEdit = SetSaveDialogFileName(dialog, savedPath, log);
            CommitSaveDialog(dialog, fileNameEdit, savedPath, log);

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !File.Exists(savedPath)) Thread.Sleep(500);
            Assert.True(File.Exists(savedPath), $"the Save As dialog committed but no file appeared at '{savedPath}'.");

            // 5. What landed on disk is the server's authority certificate: same DER, and a real
            //    X.509 the platform can parse (the whole point of the .cer is that a trust import -
            //    a GPO among them - can consume it without the plugin in the picture).
            var savedDer = File.ReadAllBytes(savedPath);
            using var saved = X509CertificateLoader.LoadCertificate(savedDer);
            log.AppendLine($"saved file: {savedDer.Length} bytes, thumbprint {saved.Thumbprint}, subject {saved.Subject}, " +
                           $"valid {saved.NotBefore:u} .. {saved.NotAfter:u}");
            Assert.Equal(serverAuthority.RawData, savedDer);
            Assert.Equal(serverAuthority.Thumbprint, saved.Thumbprint);
            Assert.Contains(EventServerAuthority.AuthorityCommonName, saved.Subject);
            Assert.Equal("30", savedDer[0].ToString("x2")); // DER, not PEM/text

            // 6. The Management Client's own audit trail: an answer line and a save line. The path
            //    assertion doubles as proof the line belongs to THIS run (the artifacts folder name
            //    carries the run timestamp), so an older run's identical marker can never pass.
            var answered = McLog.WaitForNewLine(answeredBefore, 20, "Proxy authority certificate answered:");
            Assert.NotNull(answered);
            log.AppendLine("mc log: " + answered);
            var exportedLine = McLog.MatchingLines("Proxy authority certificate exported to")
                .LastOrDefault(line => line.Contains(savedPath, StringComparison.Ordinal));
            Assert.NotNull(exportedLine);
            log.AppendLine("mc log: " + exportedLine);
            Assert.Contains(saved.Thumbprint, exportedLine);

            // 7. The pane tells the admin where it wrote the file (and the trust-distribution
            //    follow-up) instead of leaving an empty file as the only signal.
            var result = ManagementClientUi.ExpectControl(mcMain, "txtProxyCaExportResult", 10);
            var resultText = result.Name ?? string.Empty;
            log.AppendLine($"pane result text: {resultText}");
            Assert.Contains(savedPath, resultText);
            Assert.Contains(saved.Thumbprint, resultText);
            Artifacts.WindowPrint(mcMain, "mc-ca-export-result");
        }
        finally
        {
            Artifacts.WriteText("mc-ca-export-probe-results.txt", log.ToString());
            _output.WriteLine(log.ToString());
            try { mcMain.Close(); } catch { }
            ManagementClientUi.AcceptPendingModal(session, 5);
            ProcessKiller.KillClients("VideoOS.Administration");
        }
    }

    /// <summary>
    /// Waits for the Save As dialog, escalating to a hard failure if the pane's failure banner appears
    /// instead - the failure half of the AC is then visible in the probe record with its sentence. The
    /// page rides an ElementHost inside a WinForms shell, so while waiting the probe journals what the
    /// pane says and which windows the shell process owns: a timeout with no journal is only a guess
    /// about where the dialog went.
    /// </summary>
    private static AutomationElement WaitForSaveDialog(ClientSession session, Window mcMain, StringBuilder log, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var poll = 0;
        while (DateTime.UtcNow < deadline)
        {
            var dialog = FindSaveDialog(session, log, record: poll % 6 == 0);
            if (dialog != null) return dialog;

            var resultText = ReadVisiblePaneText(mcMain, "txtProxyCaExportResult");
            var bannerText = ReadVisiblePaneText(mcMain, "txtProxyCaExportError");
            var button = mcMain.FindFirstDescendant(cf => cf.ByAutomationId("proxyCaExportButton"));
            log.AppendLine($"poll {poll}: button={(button == null ? "gone" : (button.IsEnabled ? "enabled" : "disabled"))} " +
                           $"result='{resultText ?? "(none)"}' error='{bannerText ?? "(none)"}'");

            if (bannerText != null)
            {
                foreach (var line in McLog.MatchingLines("Proxy authority certificate"))
                    log.AppendLine("mc log: " + line);
                Artifacts.WindowPrint(mcMain, "mc-ca-export-failure");
                throw new InvalidOperationException($"the export pane refused instead of answering: {bannerText}");
            }

            if (poll > 4 && resultText != null && resultText.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                DumpProcessWindows(session, mcMain, log, "cancelled");
                throw new InvalidOperationException($"the Save As dialog closed itself: '{resultText}'");
            }

            poll++;
            Thread.Sleep(1000);
        }

        FindSaveDialog(session, log, record: true);
        DumpProcessWindows(session, mcMain, log, "final");
        foreach (var line in McLog.MatchingLines("Proxy authority certificate"))
            log.AppendLine("mc log: " + line);
        Artifacts.WindowPrint(mcMain, "mc-ca-export-no-dialog");
        throw new TimeoutException($"no Save As dialog and no failure banner within {timeoutSeconds}s.");
    }

    /// <summary>
    /// The Save As dialog. A Win32 common dialog (#32770) owned by a page inside a WinForms
    /// ElementHost can appear as a top-level window, nested under the shell window, or as a desktop
    /// child, so all three places are scanned; the dialog's own title is the tell and its class the
    /// fallback. When recording, every desktop window UIA sees is journalled - a timeout answer
    /// "the shell never opened it" versus "it is there under another parent" only comes from that.
    /// </summary>
    private static AutomationElement FindSaveDialog(ClientSession session, StringBuilder log, bool record)
    {
        var candidates = new List<AutomationElement>();
        try
        {
            candidates.AddRange(session.Automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)));
        }
        catch (Exception exception)
        {
            log.AppendLine($"desktop window scan failed: {exception.GetType().Name}: {exception.Message}");
        }

        candidates.AddRange(session.Windows());
        foreach (var window in session.Windows())
        {
            try { candidates.AddRange(window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window))); } catch { }
        }

        AutomationElement titledMatch = null;
        AutomationElement anyDialog = null;
        foreach (var candidate in candidates)
        {
            string name = null;
            string className = null;
            var processId = -1;
            var offscreen = false;
            try { name = candidate.Name; } catch { }
            try { className = candidate.ClassName; } catch { }
            try { processId = candidate.Properties.ProcessId.Value; } catch { }
            try { offscreen = candidate.Properties.IsOffscreen.Value; } catch { }

            if (record)
            {
                string rect;
                try { rect = candidate.BoundingRectangle.ToString(); } catch { rect = "(unreadable)"; }
                log.AppendLine($"  window: name='{name}' class='{className}' pid={processId} offscreen={offscreen} rect={rect}");
            }

            var titled = (name ?? string.Empty).IndexOf("proxy authority certificate", StringComparison.OrdinalIgnoreCase) >= 0;
            var win32Dialog = (className ?? string.Empty).Contains("#32770") && processId == session.Process.Id;
            if (!titled && !win32Dialog) continue;

            if (titled) titledMatch ??= candidate;
            anyDialog ??= candidate;
        }

        return titledMatch ?? anyDialog;
    }

    /// <summary>Every window UIA attributes to the client process, plus the shell's own descendant
    /// windows (an owned common dialog can ride the main window's UIA subtree, not the desktop's).</summary>
    private static void DumpProcessWindows(ClientSession session, Window mcMain, StringBuilder log, string stage)
    {
        log.AppendLine($"--- {stage}: top-level windows of pid {session.Process.Id} ---");
        foreach (var window in session.Windows())
        {
            try
            {
                log.AppendLine($"  window name='{window.Name}' aid='{window.AutomationId}' class='{window.ClassName}' " +
                               $"rect={window.BoundingRectangle} visible={(!window.Properties.IsOffscreen.Value)} enabled={window.IsEnabled}");
            }
            catch (Exception exception)
            {
                log.AppendLine($"  window unreadable: {exception.GetType().Name}: {exception.Message}");
            }
        }

        log.AppendLine($"--- {stage}: descendant windows under the main window ---");
        try
        {
            foreach (var child in mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
                log.AppendLine($"  child window name='{child.Name}' aid='{child.AutomationId}' class='{child.ClassName}' rect={child.BoundingRectangle}");
        }
        catch (Exception exception)
        {
            log.AppendLine($"  descendant scan failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Types the destination path into the common dialog's File name edit and returns that edit. The
    /// Win32 dialog pre-fills the default name, so the default value is the most reliable tell.
    ///
    /// The path must arrive through KEYSTROKES, not ValuePattern.SetValue: live runs proved that a
    /// SetValue-committed edit is honoured only for the file NAME while the dialog saves into its own
    /// current folder (the file landed in Documents\Custom Office Templates instead of the requested
    /// path). Keyboard input drives the dialog's own path parsing, the same route a human uses.
    /// </summary>
    private static AutomationElement SetSaveDialogFileName(AutomationElement dialog, string path, StringBuilder log)
    {
        var edits = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).ToList();
        var interesting = edits.Where(e =>
        {
            var value = ReadEditText(e);
            return string.Equals(value, DefaultFileName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(e.AutomationId, "1001", StringComparison.OrdinalIgnoreCase)
                   || (e.Name ?? string.Empty).IndexOf("file name", StringComparison.OrdinalIgnoreCase) >= 0;
        }).ToList();
        foreach (var edit in interesting)
            log.AppendLine($"dialog file-name edit: aid='{edit.AutomationId}' name='{edit.Name}' value='{ReadEditText(edit)}' rect={edit.BoundingRectangle}");

        var fileNameEdit = interesting.FirstOrDefault(e => string.Equals(ReadEditText(e), DefaultFileName, StringComparison.OrdinalIgnoreCase))
            ?? interesting.FirstOrDefault(e => string.Equals(e.AutomationId, "1001", StringComparison.OrdinalIgnoreCase))
            ?? interesting.FirstOrDefault();
        Assert.NotNull(fileNameEdit);

        fileNameEdit.Click();
        Thread.Sleep(300);
        try { fileNameEdit.Focus(); } catch { }
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
            FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.BACK);
        FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.BACK);
        FlaUI.Core.Input.Keyboard.Type(path);
        Thread.Sleep(500);

        var readback = ReadEditText(fileNameEdit);
        log.AppendLine($"file name edit now reads: '{readback}'");
        Assert.Equal(path, readback);
        return fileNameEdit;
    }

    /// <summary>
    /// Commits the dialog and proves it by the file appearing. ENTER in the File name edit comes
    /// first: it runs through the dialog's own commit handler, so the typed path is parsed as typed.
    /// The UIA routes (IDOK, the Save button) are kept as fallbacks and are each judged only by the
    /// file appearing where it was asked for.
    /// </summary>
    private static void CommitSaveDialog(AutomationElement dialog, AutomationElement fileNameEdit, string path, StringBuilder log)
    {
        foreach (var button in dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
        {
            string name;
            string automationId;
            try { name = button.Name; automationId = button.AutomationId; } catch { continue; }
            if ((name ?? string.Empty).IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(automationId, "1", StringComparison.OrdinalIgnoreCase))
            {
                log.AppendLine($"dialog commit candidate: name='{name}' aid='{automationId}'");
            }
        }

        var routes = new Action[]
        {
            () =>
            {
                fileNameEdit.Click();
                Thread.Sleep(250);
                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                log.AppendLine("committed with ENTER in the File name edit");
            },
            () =>
            {
                var ok = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1"));
                if (ok == null) throw new InvalidOperationException("no IDOK control in the dialog");
                try { ok.Patterns.Invoke.Pattern.Invoke(); } catch { ok.Click(); }
                log.AppendLine("committed through the dialog's IDOK control");
            },
            () =>
            {
                var named = dialog.FindAllDescendants(cf => cf.ByName("Save"))
                    .Concat(dialog.FindAllDescendants(cf => cf.ByName("&Save")))
                    .FirstOrDefault();
                if (named == null) throw new InvalidOperationException("no button named Save");
                try { named.Patterns.Invoke.Pattern.Invoke(); } catch { named.Click(); }
                log.AppendLine("committed through the Save button");
            },
        };

        foreach (var route in routes)
        {
            try { route(); }
            catch (Exception exception)
            {
                log.AppendLine($"commit route failed: {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path)) return;
                Thread.Sleep(500);
            }

            log.AppendLine("the file did not appear after that commit route");
        }
    }

    private static string ReadEditText(AutomationElement edit)
    {
        try { return edit.Patterns.Value.IsSupported ? edit.Patterns.Value.Pattern.Value.Value : edit.Name; }
        catch { return edit.Name; }
    }

    /// <summary>
    /// Pane text that is actually on screen. The failure banner and the result line are Collapsed
    /// when unused, and a collapsed WPF element still answers UIA with an empty Name - so offscreen
    /// is the signal that distinguishes "not shown" from "shown and empty".
    /// </summary>
    private static string ReadVisiblePaneText(Window mcMain, string automationId)
    {
        try
        {
            var element = mcMain.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (element == null) return null;
            if (element.Properties.IsOffscreen.Value) return null;
            return string.IsNullOrWhiteSpace(element.Name) ? "(shown, no text)" : element.Name;
        }
        catch { return null; }
    }
}
