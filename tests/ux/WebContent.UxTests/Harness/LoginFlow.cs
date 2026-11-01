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
    public static Window WaitForLoginWindow(ClientSession session, int timeoutSeconds = 60)
        => session.WaitForWindow(
            w => w.FindFirstDescendant(cf => cf.ByAutomationId("_btnConnect")) != null ||
                 w.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton")) != null,
            timeoutSeconds, "login dialog");

    public static void Login(ClientSession session, Window loginWindow, UxConfig config)
    {
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

        // The login control disables Connect while it considers the input incomplete - a
        // dropped password shows up as this, not as an error. Re-enter once before giving up.
        if (!WaitUntilEnabled(connect, 3))
        {
            LogEntry($"connect disabled; re-entering password (loginWindow aid='{loginWindow.AutomationId}')");
            Artifacts.Window(loginWindow, "login-filled-disabled");
            FocusAndType(session, loginWindow, passwordBox, config.VmsPassword);
            if (!WaitUntilEnabled(connect, 3))
                throw new InvalidOperationException(
                    "Connect stayed disabled after two password entries - the text never reached _passwordBox " +
                    "(see login-entry-log.txt artifact).");
        }
        connect.Patterns.Invoke.Pattern.Invoke();
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

    /// <summary>True when the client's own main window is up (Smart Client or Management Client).</summary>
    private static bool HasMainSessionWindow(ClientSession session)
    {
        foreach (var w in session.Windows().ToList())
        {
            try
            {
                if (w.AutomationId == "_mainApplicationWindow") return true;
                var name = w.Name ?? string.Empty;
                var cls = w.ClassName ?? string.Empty;
                if (name.Contains("Management Client", StringComparison.OrdinalIgnoreCase)
                    && cls.StartsWith("WindowsForms", StringComparison.Ordinal)
                    && w.BoundingRectangle.Width > 400)
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static bool IsNegativeButton(string name) =>
        name.Contains("don't", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("do not", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    public static Window WaitForSmartClientMain(ClientSession session, int timeoutSeconds = 90)
        => session.WaitForWindow(w => w.AutomationId == "_mainApplicationWindow", timeoutSeconds, "Smart Client main window");

    public static Window WaitForAdminMain(ClientSession session, int timeoutSeconds = 120)
        => session.WaitForWindow(
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
                var hwnd = window.Properties.NativeWindowHandle.Value;
                var foreground = GetForegroundWindow().ToInt64();
                var focused = session.Automation.FocusedElement();
                var focusOk = focused != null
                              && string.Equals(focused.AutomationId, element.AutomationId, StringComparison.OrdinalIgnoreCase)
                              && focused.Properties.NativeWindowHandle.Value == hwnd;
                var fgOk = foreground == (hwnd & 0xFFFFFFFFL);
                if (focusOk && fgOk)
                {
                    EnterText(text);
                    return;
                }
                LogEntry($"focus attempt {attempt}: focusOk={focusOk} fgOk={fgOk} fg=0x{foreground:X} hwnd=0x{hwnd & 0xFFFFFFFFL:X}");
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
