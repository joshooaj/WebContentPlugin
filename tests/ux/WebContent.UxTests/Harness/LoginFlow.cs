using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Drives the shared Milestone ServerLoginControl used by both desktop clients.
/// Smart Client uses _btnConnect, Management Client uses _connectButton.
/// </summary>
public static class LoginFlow
{
    /// <summary>
    /// Waits for the login dialog. Returns NULL only when the Smart Client main window (whose
    /// _mainApplicationWindow AutomationId no other window carries) is up while NO window of the
    /// process wears login controls - i.e. Auto-login connected on its own. That shortcut is
    /// deliberately NOT extended to the Management Client: MC's main shell window exists from
    /// launch, BEFORE login (login is a separate window), so its absence/presence proves nothing.
    /// </summary>
    public static Window WaitForLoginWindow(ClientSession session, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var window = session.FindWindow(HasLoginControls);
            if (window != null) return window;
            if (session.Windows().Any(w =>
                    string.Equals(w.AutomationId, "_mainApplicationWindow", StringComparison.OrdinalIgnoreCase)
                    && !HasLoginControls(w)))
            {
                LogEntry("Smart Client main window up with no login dialog anywhere (auto-login); no entry needed");
                return null;
            }
            Thread.Sleep(300);
        }
        throw new TimeoutException($"Timed out waiting for login dialog (process {session.Process.ProcessName}, pid {session.Process.Id}).");
    }

    public static void Login(ClientSession session, Window loginWindow, UxConfig config)
    {
        if (loginWindow == null || HasMainSessionWindow(session))
        {
            LogEntry("main window already up (client auto-login with remembered credentials); nothing to enter");
            return;
        }

        // The dialog may vanish mid-entry because the client's own auto-login just connected
        // (stale element: UIA throws InvalidOperationException / ElementNotAvailable). That is a
        // SUCCESS outcome, not a broken login - recognize it instead of faulting the probe.
        try
        {
        // A pre-filled dialog with a DEAD Connect button is the client mid-auto-login: the
        // fields hold the remembered credentials and Connect is disabled for the whole attempt.
        // Typing into that dialog (select-all, paste) has raced and cancelled the attempt -
        // leave it untouched and let the session window arrive.
        var earlyConnect = loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_btnConnect"))
                           ?? loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton"));
        if (earlyConnect != null && !TryIsEnabled(earlyConnect) && RememberedUserPresent(loginWindow, config))
        {
            LogEntry("dialog pre-filled with remembered user, Connect disabled - auto-login in progress; hands off 30 s");
            if (WaitForConnected(session, 30))
            {
                LogEntry("auto-login completed while hands-off waiting; nothing to enter");
                return;
            }
        }

        var serverBox = loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("PART_EditableTextBox"));
        if (serverBox != null && !string.IsNullOrWhiteSpace(config.VmsAddress))
        {
            serverBox.Focus();
            SetValueOrType(serverBox, config.VmsAddress);
        }

        var userCombo = loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_userNameComboBox"));
        var userBox = userCombo?.FindFirstDescendant(cf => cf.ByAutomationId("PART_EditableTextBox"));
        if (userBox != null)
        {
            userBox.Focus();
            SetValueOrType(userBox, config.VmsUsername);
        }

        var passwordBox = loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_passwordBox"));
        if (passwordBox == null) throw new InvalidOperationException("Login dialog has no _passwordBox.");
        FocusAndType(session, loginWindow, passwordBox, config.VmsPassword);

        var connect = loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_btnConnect"))
                      ?? loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton"));
        if (connect == null) throw new InvalidOperationException("Login dialog has no connect button.");

        // The client may ALREADY be auto-connecting (Auto-login was remembered from an
        // interactive session): the dialog shows filled remembered fields, Connect is disabled
        // for the duration of the attempt, and on success the main window simply appears.
        // Never fight that dialog - give the auto-login room, then fall back to entering.
        if (!WaitUntilEnabled(connect, 3))
        {
            if (WaitForConnected(session, 20))
            {
                LogEntry("auto-login completed while waiting; leaving the dialog alone");
                return;
            }
            LogEntry($"connect disabled; re-entering password (loginWindow aid='{loginWindow.AutomationId}')");
            Artifacts.Window(loginWindow, "login-filled-disabled");
            DumpLoginTree(loginWindow, "login-tree-disabled.txt");
            FocusAndType(session, loginWindow, passwordBox, config.VmsPassword);
            if (!WaitUntilEnabled(connect, 3) && !WaitForConnected(session, 10))
            {
                DumpLoginTree(loginWindow, "login-tree-disabled2.txt");
                throw new InvalidOperationException(
                    "Connect stayed disabled after two password entries and no session window appeared " +
                    "(see login-entry-log.txt + login-tree-disabled*.txt artifacts).");
            }
            if (HasMainSessionWindow(session)) return;
        }
        connect.Patterns.Invoke.Pattern.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   || ex is FlaUI.Core.Exceptions.ElementNotAvailableException)
        {
            // Stale mid-entry: the dialog closed because the client's own auto-login connected.
            if (HasMainSessionWindow(session) || WaitForConnected(session, 15))
            {
                LogEntry($"login dialog vanished mid-entry ({ex.GetType().Name}) but the session window is up - auto-login won; treating as signed in");
                return;
            }
            throw;
        }
    }

    private static bool WaitForConnected(ClientSession session, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (HasMainSessionWindow(session)) return true;
            Thread.Sleep(400);
        }
        return false;
    }

    private static bool TryIsEnabled(AutomationElement element)
    {
        try { return element.IsEnabled; }
        catch { return false; }
    }

    /// <summary>
    /// True when the login dialog's user combo already carries the configured username -
    /// the signature of the client's remembered-credentials auto-login attempt.
    /// </summary>
    private static bool RememberedUserPresent(Window loginWindow, UxConfig config)
    {
        try
        {
            var userCombo = loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_userNameComboBox"));
            var userBox = userCombo?.FindFirstDescendant(cf => cf.ByAutomationId("PART_EditableTextBox"));
            var shown = userBox == null
                ? (userCombo?.Name ?? string.Empty)
                : (ReadBoxValue(userBox) ?? userBox.Name ?? string.Empty);
            return !string.IsNullOrWhiteSpace(config.VmsUsername)
                   && shown.Contains(config.VmsUsername, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadBoxValue(AutomationElement box)
    {
        try
        {
            if (box.Patterns.Value.IsSupported) return box.Patterns.Value.Pattern.Value;
        }
        catch { }
        return null;
    }

    private static readonly string[] PreferredTrustButtonIds =
    {
        "_btnContinue",
        "_btnTrust",
        "_btnAccept",
        "_btnInstall",
        "_btnOK",
        "_btnYes",
        "_btnConnect", // the trust prompt's accept button is 'Connect' (paired with 'Close'/Cancel)
    };

    private static readonly string[] PreferredTrustButtonNames =
    {
        "continue",
        "trust",
        "accept",
        "install",
        "add",
        "ok",
        "yes",
        "connect",
    };

    /// <summary>
    /// Milestone clients show a certificate-trust dialog for the self-signed VMS cert
    /// on first connect. Accept it so the session gets a live server channel (the plugin
    /// tabs and config silently degrade without one).
    /// </summary>
    public static void AcceptCertificateTrust(ClientSession session, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var dumped = false;
        while (DateTime.UtcNow < deadline)
        {
            var candidates = new List<(AutomationElement button, string automationId, string name)>();

            foreach (var window in session.Windows())
            {
                try
                {
                    if (window.AutomationId == "_mainApplicationWindow") continue;
                    foreach (var button in window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                    {
                        var name = button.Name ?? string.Empty;
                        if (IsNegativeButton(name)) continue;
                        candidates.Add((button, button.AutomationId ?? string.Empty, name));
                    }
                }
                catch { }
            }

            if (candidates.Count > 0)
            {
                if (!dumped)
                {
                    dumped = true;
                    Artifacts.WriteText("cert-prompt-buttons.txt", string.Join(Environment.NewLine,
                        candidates.Select(c => $"aid='{c.automationId}' name='{c.name}'")));
                }

                var pick = candidates.FirstOrDefault(c => PreferredTrustButtonIds.Contains(c.automationId, StringComparer.OrdinalIgnoreCase));
                if (pick.button == null)
                {
                    pick = candidates.FirstOrDefault(c => PreferredTrustButtonNames.Any(n => c.name.Contains(n, StringComparison.OrdinalIgnoreCase)));
                }

                if (pick.button != null)
                {
                    Artifacts.Window(session.FindWindow(w =>
                    {
                        try
                        {
                            return w.FindFirstDescendant(cf => cf.ByName(pick.name)) != null;
                        }
                        catch { return false; }
                    }) ?? session.Windows().First(), "cert-prompt");
                    try { pick.button.Patterns.Invoke.Pattern.Invoke(); } catch { pick.button.Click(); }
                    Thread.Sleep(250);
                    return;
                }
            }
            else if (HasMainSessionWindow(session))
            {
                // Login completed with NO dialog pending (the cert is already in the local store
                // from an earlier session): polling to the full timeout here was 30 s of dead
                // time between login and every tree navigation.
                return;
            }

            Thread.Sleep(150);
        }
    }

    /// <summary>
    /// True when the process shows a session window AND no login dialog anywhere:
    /// Smart Client's _mainApplicationWindow or a >800px Management Client shell, with no
    /// window carrying _btnConnect/_connectButton. (MC's login window is separate from its
    /// main shell, so "no dialog" must be checked process-wide, not window-by-window.)
    /// </summary>
    private static bool HasMainSessionWindow(ClientSession session)
    {
        var windows = session.Windows().ToList();
        if (windows.Any(HasLoginControls)) return false;
        return windows.Any(w =>
        {
            try
            {
                if (string.Equals(w.AutomationId, "_mainApplicationWindow", StringComparison.OrdinalIgnoreCase)) return true;
                var name = w.Name ?? string.Empty;
                var cls = w.ClassName ?? string.Empty;
                return name.Contains("Management Client", StringComparison.OrdinalIgnoreCase)
                       && cls.StartsWith("WindowsForms", StringComparison.Ordinal)
                       && w.BoundingRectangle.Width > 800;
            }
            catch { return false; }
        });
    }

    private static bool HasLoginControls(Window w)
    {
        try
        {
            return w.FindFirstDescendant(cf => cf.ByAutomationId("_btnConnect")) != null
                   || w.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton")) != null;
        }
        catch { return false; }
    }

    private static bool IsNegativeButton(string name) =>
        name.Contains("don't", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("do not", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    public static Window WaitForSmartClientMain(ClientSession session, int timeoutSeconds = 90)
        => session.WaitForWindow(w => w.AutomationId == "_mainApplicationWindow", timeoutSeconds, "Smart Client main window");

    public static Window WaitForAdminMain(ClientSession session, int timeoutSeconds = 120)
    {
        var window = session.WaitForWindow(
            w =>
            {
                var cls = w.ClassName;
                var aid = w.AutomationId;
                var login = aid == "ConnectionDialog" ||
                            w.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton")) != null;
                return !login && cls != null && cls.StartsWith("WindowsForms", StringComparison.Ordinal) &&
                       w.BoundingRectangle.Width > 400 && w.Name != null && w.Name.Contains("Management Client", StringComparison.OrdinalIgnoreCase);
            },
            timeoutSeconds, "Management Client main window");

        // MC restores its window position from the last interactive session - it can come back
        // hanging off the monitor, where UIA still READS every tree row but mouse clicks land on
        // nothing (collapse strikes become silent no-ops). Normalize before any geometry work.
        try
        {
            if (window.Patterns.Window.IsSupported)
                window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
            Thread.Sleep(600);
        }
        catch { }
        return window;
    }

    private static void SetValueOrType(AutomationElement element, string value)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                element.Patterns.Value.Pattern.SetValue(value);
                return;
            }
        }
        catch
        {
            // fall through to keyboard input
        }
        Keyboard.Type(value);
    }

    /// <summary>
    /// Keyboard input (both typed keys and Ctrl+V) is routed by Windows to the FOREGROUND
    /// window, while UIA focus is a different fact - a plain Focus() can report success while
    /// another app still owns the foreground, so the password lands nowhere (empty box, disabled
    /// Connect, or a stale corrupted value). Verify BOTH that _passwordBox holds UIA focus and
    /// that its window is the foreground window before entering text.
    /// </summary>
    private static void FocusAndType(ClientSession session, Window window, AutomationElement element, string text)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try { window.Focus(); } catch { }
            try { element.Focus(); } catch { }
            Thread.Sleep(150);
            try
            {
                // NativeWindowHandle is unsupported on these dialog windows; route checks by
                // process instead: keystrokes go to the foreground window's process, and UIA
                // focus + AutomationId must name _passwordBox within the SAME process (MC and
                // SC show identically-id'd login dialogs simultaneously).
                var pid = session.Process.Id;
                var fgHwnd = GetForegroundWindow();
                GetWindowThreadProcessId(fgHwnd, out var fgPid);
                var focused = session.Automation.FocusedElement();
                int focusedPid = -1;
                try { focusedPid = focused == null ? -1 : focused.Properties.ProcessId.Value; } catch { }
                var focusOk = focused != null
                              && string.Equals(focused.AutomationId, element.AutomationId, StringComparison.OrdinalIgnoreCase)
                              && focusedPid == pid;
                var fgOk = fgPid == pid;
                if (focusOk && fgOk)
                {
                    EnterText(text);
                    return;
                }
                LogEntry($"focus attempt {attempt}: focusOk={focusOk} (focusedAid='{focused?.AutomationId}' focusedPid={focusedPid}) fgOk={fgOk} (fgPid={fgPid} want={pid})");
            }
            catch (Exception ex)
            {
                LogEntry($"focus attempt {attempt} threw {ex.GetType().Name}: {ex.Message}");
            }
            Thread.Sleep(250);
        }

        // Focus never verified: mouse-click the box (click forces foreground AND focus together),
        // then enter the text anyway - the Connect-disabled check upstream catches a miss.
        element.Click();
        Thread.Sleep(150);
        EnterText(text);
        LogEntry("entry via click-fallback (focus unverified)");
    }

    private static bool WaitUntilEnabled(AutomationElement element, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (element.IsEnabled) return true; } catch { }
            Thread.Sleep(250);
        }
        try { return element.IsEnabled; } catch { return false; }
    }

    /// <summary>Flat UIA census of the login dialog: what the client itself thinks each control
    /// (especially Connect) looks like after entry - the arbiter when Connect stays disabled.</summary>
    private static void DumpLoginTree(Window window, string fileName)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var el in window.FindAllDescendants())
            {
                try
                {
                    var val = string.Empty;
                    try
                    {
                        if (el.Patterns.Value.IsSupported)
                            val = $" value='{el.Patterns.Value.Pattern.Value}'";
                    }
                    catch { }
                    sb.AppendLine($"{el.ControlType} aid='{el.AutomationId}' name='{el.Name}' enabled={el.IsEnabled}{val}");
                }
                catch { }
            }
            Artifacts.WriteText(fileName, sb.ToString());
        }
        catch (Exception ex)
        {
            LogEntry($"login tree dump threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogEntry(string line)
    {
        try
        {
            File.AppendAllText(Artifacts.PathOf("login-entry-log.txt"),
                DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Enter text as clipboard paste (Ctrl+V) when possible: the VMS password carries characters
    /// ($ &amp; * @) whose shifted virtual-key mapping is layout-dependent, and Keyboard.Type had
    /// been sending silently CORRUPTED passwords. CF_UNICODETEXT pastes exact UTF-16 regardless
    /// of keyboard layout; Keyboard.Type remains the fallback if the clipboard is unavailable.
    /// </summary>
    private static void EnterText(string text)
    {
        if (TryClipboardSetText(text))
        {
            LogEntry("entry-via=paste (Ctrl+V)");
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
            return;
        }
        LogEntry("entry-via=typed-keys (clipboard unavailable - password may corrupt)");
        Keyboard.Type(text);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr newOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);

    private const uint CF_UNICODETEXT = 13;
    private const uint GHND = 0x42; // GMEM_MOVEABLE | GMEM_ZEROINIT

    private static bool TryClipboardSetText(string text)
    {
        var handle = IntPtr.Zero;
        try
        {
            var bytes = (uint)((text.Length + 1) * 2);
            handle = GlobalAlloc(GHND, (UIntPtr)bytes);
            if (handle == IntPtr.Zero) return false;
            var block = GlobalLock(handle);
            if (block == IntPtr.Zero) return false;
            Marshal.Copy(text.ToCharArray(), 0, block, text.Length); // +2 zero bytes = UTF-16 NUL terminator
            GlobalUnlock(handle);

            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();
                // On success the CLIPBOARD owns the handle - do not GlobalFree it.
                if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero) return false;
                handle = IntPtr.Zero;
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try { GlobalUnlock(handle); } catch { }
                try { GlobalFree(handle); } catch { } // clipboard never took ownership
            }
        }
    }
}
