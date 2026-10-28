using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Locators for the Smart Client main window. Workspace tabs live in the strip
/// (HeaderItemControl, name on a nested _textTextBlock); workspaces that don't fit
/// (and plugin workspaces not pinned to the strip) are reachable through the
/// header overflow menu (_headerItemOverflowMenu → _textBlock entries).
/// </summary>
public static class SmartClientUi
{
    public static void Maximize(Window mainWindow)
    {
        if (mainWindow.Patterns.Window.IsSupported)
        {
            mainWindow.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        }
    }

    public static List<string> VisibleTabNames(Window mainWindow) =>
        mainWindow.FindAllDescendants(cf => cf.ByAutomationId("_textTextBlock"))
            .Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

    /// <summary>Opens the header overflow menu and returns the name of every entry it lists.</summary>
    public static List<string> OpenOverflow(Window mainWindow, ClientSession session)
    {
        mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("_headerItemOverflowMenu"))?.Click();
        Thread.Sleep(2000);
        var fresh = session.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow") ?? mainWindow;
        return fresh.FindAllDescendants(cf => cf.ByAutomationId("_textBlock"))
            .Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
    }

    public static AutomationElement FindOverflowEntry(Window mainWindow, string name) =>
        mainWindow.FindAllDescendants(cf => cf.ByAutomationId("_textBlock"))
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ensures the named workspace is the selected one: click its strip label if visible,
    /// otherwise pick it from the overflow menu (re-opening it if a stale open menu closed).
    /// Returns the fresh main window after selection.
    /// </summary>
    public static Window SelectWorkspace(Window mainWindow, ClientSession session, string workspaceName, int timeoutSeconds = 40)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        List<string> seen = new();
        while (DateTime.UtcNow < deadline)
        {
            mainWindow = session.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow") ?? mainWindow;

            var label = mainWindow.FindAllDescendants(cf => cf.ByAutomationId("_textTextBlock"))
                .FirstOrDefault(t => string.Equals(t.Name, workspaceName, StringComparison.OrdinalIgnoreCase));
            if (label != null)
            {
                label.Click();
                Thread.Sleep(2000);
                return session.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            }

            seen = OpenOverflow(mainWindow, session);
            mainWindow = session.Windows().FirstOrDefault(w => w.AutomationId == "_mainApplicationWindow") ?? mainWindow;
            var entry = FindOverflowEntry(mainWindow, workspaceName);
            if (entry != null)
            {
                entry.Click();
                Thread.Sleep(3000);
                return session.Windows().First(w => w.AutomationId == "_mainApplicationWindow");
            }
            Thread.Sleep(1000);
        }
        throw new TimeoutException($"Workspace '{workspaceName}' is neither a strip tab nor an overflow entry. Overflow saw: {string.Join(", ", seen)}");
    }

    public static AutomationElement FindMipViewItem(Window mainWindow) =>
        mainWindow.FindFirstDescendant(cf => cf.ByClassName("WebContentUserControl"));

    /// <summary>The side panel region (icon strip + open panel) on the left of the main window.</summary>
    public static AutomationElement FindSidePanels(Window mainWindow) =>
        mainWindow.FindFirstDescendant(cf => cf.ByClassName("SidePanelsView"));

    /// <summary>True when any side panel is expanded (the closed strip is narrow).</summary>
    public static bool SidePanelsOpen(Window mainWindow)
    {
        var panels = FindSidePanels(mainWindow);
        return panels != null && panels.BoundingRectangle.Width > 120;
    }

    /// <summary>Closes any open side panels via the « (_closeLeftPane) header button.</summary>
    public static void CloseSidePanels(Window mainWindow, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline && SidePanelsOpen(mainWindow))
        {
            var close = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("_closeLeftPane"));
            if (close == null) return;
            try { close.Patterns.Invoke.Pattern.Invoke(); } catch { close.Click(); }
            Thread.Sleep(1200);
        }
    }

    /// <summary>Clicks the Setup button that toggles Smart Client setup mode.</summary>
    public static void ClickSetup(Window mainWindow)
    {
        var button = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("WorkspaceToolbarItemSetup"))
            ?? throw new InvalidOperationException("Setup button (WorkspaceToolbarItemSetup) not found.");
        try { button.Patterns.Invoke.Pattern.Invoke(); } catch { button.Click(); }
    }

    /// <summary>Invokes a control by automation id (used for the side panel toggle buttons).</summary>
    public static void InvokeById(Window mainWindow, string automationId, int waitMilliseconds = 1000)
    {
        var button = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
            ?? throw new InvalidOperationException($"Control '{automationId}' not found.");
        try { button.Patterns.Invoke.Pattern.Invoke(); } catch { button.Click(); }
        Thread.Sleep(waitMilliseconds);
    }

    /// <summary>Invokes a control by automation id when it exists; returns whether it was invoked.</summary>
    public static bool TryInvokeById(Window mainWindow, string automationId, int waitMilliseconds = 1000)
    {
        var button = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (button == null) return false;
        try { button.Patterns.Invoke.Pattern.Invoke(); } catch { button.Click(); }
        Thread.Sleep(waitMilliseconds);
        return true;
    }

    /// <summary>
    /// Types a URL into the plugin address bar like a user: click to focus, select all,
    /// type, press Enter. Real keystrokes so the control's KeyDown handler fires.
    /// </summary>
    public static void NavigateAddress(AutomationElement addressBar, string url)
    {
        addressBar.Click();
        Thread.Sleep(400);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
        FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL);
        FlaUI.Core.Input.Keyboard.Type(url);
        Thread.Sleep(300);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
    }

    public static AutomationElement WaitForMipViewItem(Window mainWindow, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var item = FindMipViewItem(mainWindow);
            if (item != null) return item;
            Thread.Sleep(1000);
        }
        throw new TimeoutException("The plugin's WebContentUserControl never appeared in the Smart Client view.");
    }
}
