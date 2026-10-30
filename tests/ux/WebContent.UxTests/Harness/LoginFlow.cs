using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

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
        passwordBox.Focus();
        Keyboard.Type(config.VmsPassword);

        var connect = loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_btnConnect"))
                      ?? loginWindow.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton"));
        if (connect == null) throw new InvalidOperationException("Login dialog has no connect button.");
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

            Thread.Sleep(250);
        }
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
}
