using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Locators for the Management Client: the configuration tree, the plugin's
/// workspace nodes, the WinForms detail pane (control Name == AutomationId),
/// and the shell toolbar (Save/Undo).
/// </summary>
public static class ManagementClientUi
{
    public const string WorkspacesNodeName = "Web Content Workspaces";

    /// <summary>Expands the plugin's workspaces node.</summary>
    public static void ExpandWorkspacesNode(Window mainWindow) => EnsureExpanded(mainWindow, WorkspacesNodeName);

    public static AutomationElement FindTreeItem(Window mainWindow, string name) =>
        mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public static AutomationElement WaitForTreeItem(Window mainWindow, string name, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var item = FindTreeItem(mainWindow, name);
            if (item != null) return item;
            Thread.Sleep(1000);
        }
        throw new TimeoutException($"Tree item '{name}' never appeared.");
    }

    /// <summary>
    /// Finds a node under the currently selected tree node by walking DOWN with the keyboard.
    /// The WinForms tree only realizes visible rows, so a newly expanded node's children
    /// below the fold are invisible to UIA until the selection scrolls them into view.
    /// </summary>
    public static AutomationElement FindChildByWalkingDown(Window mainWindow, string name, int maxSteps = 40)
    {
        for (var step = 0; step < maxSteps; step++)
        {
            var item = FindTreeItem(mainWindow, name);
            if (item != null) return item;
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
            Thread.Sleep(350);
        }
        return FindTreeItem(mainWindow, name);
    }

    /// <summary>Finds a tree node whose name contains the fragment (the Home root carries the version suffix).</summary>
    public static AutomationElement WaitForTreeItemContaining(Window mainWindow, string fragment, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var item = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(t => (t.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (item != null) return item;
            Thread.Sleep(1000);
        }
        throw new TimeoutException($"Tree item containing '{fragment}' never appeared.");
    }

    public static void SelectTreeItem(Window mainWindow, string name)
    {
        SelectNode(mainWindow, name);
        Thread.Sleep(1000);
    }

    /// <summary>
    /// Selects a tree node by name. SelectionItem.Select scrolls an off-screen node into view
    /// (proven to work on nodes below the tree fold); a real mouse click on the label afterwards
    /// gives the WinForms TreeView keyboard focus so subsequent LEFT/RIGHT/keys land on the tree.
    /// </summary>
    public static void SelectNode(Window mainWindow, string name)
    {
        var item = FindTreeItem(mainWindow, name)
            ?? throw new InvalidOperationException($"Tree item '{name}' not found.");
        try { if (item.Patterns.ScrollItem.IsSupported) item.Patterns.ScrollItem.Pattern.ScrollIntoView(); } catch { }
        Thread.Sleep(300);
        try
        {
            if (item.Patterns.SelectionItem.IsSupported) item.Patterns.SelectionItem.Pattern.Select();
        }
        catch { }
        Thread.Sleep(400);
        ClickLabel(mainWindow, item);
        Thread.Sleep(800);
    }

    /// <summary>Closes any leftover login/connection dialog that would steal keyboard focus from the tree.</summary>
    public static void CloseStrayLoginDialogs(ClientSession session)
    {
        for (var round = 0; round < 3; round++)
        {
            var stray = session.Windows().FirstOrDefault(w =>
            {
                try
                {
                    return w.FindFirstDescendant(cf => cf.ByAutomationId("_connectButton")) != null
                           && !((w.Name ?? string.Empty).Contains("Management Client", StringComparison.OrdinalIgnoreCase)
                                && (w.ClassName ?? string.Empty).StartsWith("WindowsForms", StringComparison.Ordinal));
                }
                catch { return false; }
            });
            if (stray == null) return;
            var close = stray.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => string.Equals((b.Name ?? string.Empty).Trim(), "Close", StringComparison.OrdinalIgnoreCase));
            try { if (close != null) close.Patterns.Invoke.Pattern.Invoke(); else stray.Close(); } catch { }
            Thread.Sleep(1500);
        }
    }

    /// <summary>
    /// Opens the node's context menu (right-click when clickable, otherwise select + Apps key)
    /// and returns every menu entry the shell exposes.
    /// </summary>
    public static List<string> OpenContextMenu(ClientSession session, Window mainWindow, AutomationElement treeItem)
    {
        try
        {
            if (treeItem.Patterns.ScrollItem.IsSupported)
            {
                treeItem.Patterns.ScrollItem.Pattern.ScrollIntoView();
                Thread.Sleep(400);
            }
        }
        catch { }
        treeItem.Focus();
        try { treeItem.Patterns.SelectionItem.Pattern.Select(); } catch { }
        Thread.Sleep(500);
        try
        {
            treeItem.RightClick();
        }
        catch
        {
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.APPS);
        }
        Thread.Sleep(1500);

        var names = new List<string>();
        foreach (var window in session.Windows())
        {
            try
            {
                names.AddRange(window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .Select(m => m.Name ?? string.Empty));
            }
            catch { }
        }
        names.RemoveAll(string.IsNullOrEmpty);
        return names;
    }

    public static List<string> RightClickAndDumpMenu(Window mainWindow, AutomationElement treeItem)
    {
        treeItem.RightClick();
        Thread.Sleep(1500);
        var items = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
            .Select(m => m.Name ?? string.Empty).Where(n => n.Length > 0).ToList();
        return items;
    }

    public static bool ClickMenuItem(ClientSession session, string nameContains)
    {
        foreach (var window in session.Windows())
        {
            List<AutomationElement> items;
            try
            {
                items = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)).ToList();
            }
            catch { continue; }
            var item = items.FirstOrDefault(m => (m.Name ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase));
            if (item == null) continue;
            try { item.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(1500); return true; } catch { }
            try
            {
                var r = item.BoundingRectangle;
                if (r.Width > 0)
                {
                    FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(r.X + r.Width / 2, r.Y + r.Height / 2));
                    Thread.Sleep(1500);
                    return true;
                }
            }
            catch { }
            var index = items.FindIndex(m => (m.Name ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i <= index; i++) FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            Thread.Sleep(1500);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ensures the named tree node is expanded. MC tree items expose NO ExpandCollapse pattern,
    /// but LegacyIAccessible.DoDefaultAction() (and a real double-click on the label) toggle the
    /// node, and collapsing un-realizes the subtree in UIA — so expansion state is verified by
    /// the expected children being realized, toggling the node whenever they are missing.
    /// </summary>
    public static void EnsureExpanded(Window mainWindow, string name, params string[] expectedChildNames)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (ChildrenAppeared(mainWindow, expectedChildNames)) return;
            var items = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).ToList();
            var node = items.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (node == null)
            {
                // Possibly the whole tree (or Home) is collapsed: open Home once and retry.
                var home = items.FirstOrDefault(t => (t.Name ?? string.Empty).StartsWith("Home", StringComparison.OrdinalIgnoreCase));
                if (home != null && items.Count <= 2)
                {
                    ClickLabel(mainWindow, home);
                    Thread.Sleep(800);
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
                    Thread.Sleep(1500);
                    continue;
                }
                throw new InvalidOperationException($"Tree node '{name}' not found (attempt {attempt}).");
            }

            if (attempt < 3)
            {
                // Keyboard route: walk DOWN to the node's visible index, press RIGHT. This is what
                // plugin-injected nodes honour (DoDefaultAction / double-click just open a page).
                var home = items.FirstOrDefault(t => (t.Name ?? string.Empty).StartsWith("Home", StringComparison.OrdinalIgnoreCase));
                if (home != null)
                {
                    var idx = items.IndexOf(node);
                    KeyboardWalkTo(mainWindow, home, idx);
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
                    Thread.Sleep(1200);
                    continue;
                }
            }
            ToggleNode(node, Math.Min(attempt, 2));
            Thread.Sleep(1200);
        }
        if (expectedChildNames.Length > 0)
            throw new InvalidOperationException($"Could not expand '{name}': none of [{string.Join(", ", expectedChildNames)}] appeared.");
    }

    /// <summary>
    /// Toggles expand/collapse of a node. Strategy 0 uses LegacyIAccessible.DoDefaultAction
    /// (works for most nodes), strategy 1 does a real double-click on the label (proven for nodes
    /// where DoDefaultAction is a silent no-op), strategy 2 selects the node first, then double-clicks.
    /// </summary>
    private static void ToggleNode(AutomationElement node, int strategy = 0)
    {
        if (strategy == 0)
        {
            try
            {
                if (node.Patterns.LegacyIAccessible.IsSupported)
                {
                    node.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                    return;
                }
            }
            catch { }
        }
        if (strategy >= 2)
        {
            try { if (node.Patterns.SelectionItem.IsSupported) node.Patterns.SelectionItem.Pattern.Select(); } catch { }
            Thread.Sleep(400);
        }
        var pt = ClickablePoint(node);
        if (pt.HasValue) FlaUI.Core.Input.Mouse.DoubleClick(pt.Value);
    }

    private static bool ChildrenAppeared(Window mainWindow, string[] expectedChildNames) =>
        expectedChildNames.Length == 0 ||
        expectedChildNames.All(c => FindTreeItem(mainWindow, c) != null);

    /// <summary>Clicks the node label with the real mouse (no UIA clickable-point dance).</summary>
    private static void ClickLabel(Window mainWindow, AutomationElement node)
    {
        var pt = ClickablePoint(node);
        if (pt.HasValue) FlaUI.Core.Input.Mouse.Click(pt.Value);
    }

    private static System.Drawing.Point? ClickablePoint(AutomationElement node)
    {
        try
        {
            var r = node.BoundingRectangle;
            if (r.Width > 0 && r.Height > 0)
            {
                // Click the middle of the label text: the left edge of these rects sits on the
                // glyph/checkbox column, and a click there toggles expand/collapse.
                var x = r.X + Math.Min(r.Width - 5, Math.Max(25, r.Width / 2));
                return new System.Drawing.Point(x, r.Y + r.Height / 2);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Returns the tree to the canonical clean state: Home expanded, every child of Home collapsed
    /// (optionally allowing some nodes to stay open, e.g. the plugin node the caller will use next).
    /// Repeatedly locates the first expanded non-root node in document order, then drives the
    /// WinForms TreeView itself: real-click Home (focus + selection at the top), walk DOWN with the
    /// keyboard to the node's visible index, press LEFT (collapse). Plugin-injected nodes ignore
    /// DoDefaultAction/double-click toggles, so the keyboard route is the reliable one.
    /// </summary>
    public static void CollapseTreeToRoot(Window mainWindow, int timeoutSeconds = 150, string[] allowExpanded = null)
    {
        var log = new System.Text.StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var strikes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allow = new HashSet<string>(allowExpanded ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        while (DateTime.UtcNow < deadline)
        {
            WheelTree(mainWindow, -12);
            var items = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).ToList();
            var home = items.FirstOrDefault(t => (t.Name ?? string.Empty).StartsWith("Home", StringComparison.OrdinalIgnoreCase));
            if (home == null) { Artifacts.WriteText("mc-collapse-log.txt", log.AppendLine("Home not realized").ToString()); return; }

            // MC persists tree collapse state between sessions: the whole tree can start collapsed.
            if (items.Count == 1)
            {
                log.AppendLine("Home starts collapsed; expanding via keyboard RIGHT");
                ClickLabel(mainWindow, home);
                Thread.Sleep(800);
                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RIGHT);
                Thread.Sleep(1500);
                continue;
            }

            var culprit = FindExpandedNonRoot(items, home.Name, allow, strikes);
            if (culprit == null)
            {
                var stubborn = ExpandedNonRootNames(items, home.Name, allow);
                if (stubborn.Count == 0 || strikes.Count > 0)
                {
                    if (stubborn.Count > 0) log.AppendLine("accepting stubborn-expanded: " + string.Join(", ", stubborn));
                    Artifacts.WriteText("mc-collapse-log.txt", log.ToString());
                    return;
                }
            }

            var name = culprit?.Name ?? string.Empty;
            strikes[name] = strikes.GetValueOrDefault(name) + 1;
            var idx = items.FindIndex(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            log.AppendLine($"collapse '{name}' idx={idx} strike={strikes[name]}");
            KeyboardWalkTo(mainWindow, home, idx);
            var walked = items[idx];
            if (_selectionObservable == true && !IsNodeSelected(walked))
            {
                log.AppendLine($"  walk failed (node not selected); skipping LEFT");
                continue;
            }
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.LEFT);
            Thread.Sleep(800);
        }
        Artifacts.WriteText("mc-collapse-log.txt", log.ToString());
        throw new TimeoutException("Could not collapse the Management Client tree to the Home root.");
    }

    /// <summary>First realized node that has an indented child right after it (skips Home, allowed names, strike-exhausted names).</summary>
    private static AutomationElement FindExpandedNonRoot(List<AutomationElement> items, string homeName,
        HashSet<string> allow, Dictionary<string, int> strikes)
    {
        for (var i = 0; i + 1 < items.Count; i++)
        {
            if (string.Equals(items[i].Name, homeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (allow.Contains(items[i].Name ?? string.Empty)) continue;
            if (strikes.GetValueOrDefault(items[i].Name ?? string.Empty) >= MaxStrikesPerNode) continue;
            var a = items[i].BoundingRectangle;
            var b = items[i + 1].BoundingRectangle;
            if (a.Width > 0 && b.Width > 0 && b.X > a.X + 5) return items[i];
        }
        return null;
    }

    /// <summary>
    /// Selects the node at the given visible index by clicking the Home root (which focuses the
    /// tree and selects index 0) and walking DOWN with the keyboard. Selection state on this tree
    /// may be unreadable (SelectionItem.IsSelected stays false); when it is readable the walk is
    /// verified per key press, otherwise the key presses are trusted.
    /// </summary>
    private static bool? _selectionObservable;

    private static void KeyboardWalkTo(Window mainWindow, AutomationElement home, int index)
    {
        for (var round = 0; round < 3; round++)
        {
            ClickLabel(mainWindow, home);
            Thread.Sleep(800);
            if (_selectionObservable == null)
                _selectionObservable = IsNodeSelected(home); // clicking Home must select it when readable

            var target = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .ElementAtOrDefault(index);
            if (target == null) return;

            for (var i = 0; i < index; i++)
            {
                if (_selectionObservable == true && IsNodeSelected(target)) { Thread.Sleep(250); return; }
                FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
                Thread.Sleep(250);
            }
            if (_selectionObservable != true) return; // trusted walk done
            if (IsNodeSelected(target)) { Thread.Sleep(200); return; }
            // Selection never reached the target: re-click Home and retry the whole walk.
        }
    }

    private const int MaxStrikesPerNode = 4;

    private static bool IsNodeSelected(AutomationElement el)
    {
        try
        {
            if (el.Patterns.SelectionItem.IsSupported)
                return el.Patterns.SelectionItem.Pattern.IsSelected.Value;
        }
        catch { }
        return false;
    }

    /// <summary>Names of realized nodes (excluding Home and allowed names) with an indented child directly after them.</summary>
    private static List<string> ExpandedNonRootNames(List<AutomationElement> items, string homeName, HashSet<string> allow)
    {
        var names = new List<string>();
        for (var i = 0; i + 1 < items.Count; i++)
        {
            if (homeName != null && string.Equals(items[i].Name, homeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (allow.Contains(items[i].Name ?? string.Empty)) continue;
            var a = items[i].BoundingRectangle;
            var b = items[i + 1].BoundingRectangle;
            if (a.Width > 0 && b.Width > 0 && b.X > a.X + 5) names.Add(items[i].Name ?? "?");
        }
        return names;
    }

    /// <summary>Scrolls the navigation tree with the mouse wheel (negative = up). MC trees ignore keyboard scrolling.</summary>
    public static void WheelTree(Window mainWindow, int clicks)
    {
        var tree = mainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        if (tree == null) return;
        var r = tree.BoundingRectangle;
        if (r.Width <= 0) return;
        var cx = (int)(r.X + r.Width / 2);
        var cy = (int)(r.Y + Math.Min(r.Height / 2, 300));
        FlaUI.Core.Input.Mouse.Position = new System.Drawing.Point(cx, cy);
        Thread.Sleep(300);
        for (var i = 0; i < Math.Abs(clicks); i++)
        {
            InputUtil.Wheel(cx, cy, clicks > 0 ? -120 : 120);
            Thread.Sleep(150);
        }
        Thread.Sleep(600);
    }

    /// <summary>
    /// Selects a role row in the Roles list (Table automationId 'GridRoles'). Rows are Custom
    /// elements with unreliable names; match by name/cell text when readable, otherwise fall
    /// back to the last row (roles sort alphabetically; 'Operator' was last in this deployment).
    /// </summary>
    public static AutomationElement WaitAndSelectListRow(Window mainWindow, string text, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var table = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("GridRoles"));
            if (table != null)
            {
                var rows = table.FindAllDescendants(cf =>
                        cf.ByControlType(ControlType.Custom).Or(cf.ByControlType(ControlType.DataItem)))
                    .Where(r => r.BoundingRectangle.Height > 8)
                    .OrderBy(r => r.BoundingRectangle.Y)
                    .ToList();
                if (rows.Count > 0)
                {
                    var row = rows.FirstOrDefault(r =>
                            string.Equals((r.Name ?? string.Empty).Trim(), text, StringComparison.OrdinalIgnoreCase)
                            || r.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                .Any(t => string.Equals((t.Name ?? string.Empty).Trim(), text, StringComparison.OrdinalIgnoreCase)))
                        ?? rows.Last();

                    try { if (row.Patterns.SelectionItem.IsSupported) row.Patterns.SelectionItem.Pattern.Select(); } catch { }
                    var r = row.BoundingRectangle;
                    FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(r.X + Math.Min(40, r.Width / 2), r.Y + r.Height / 2));
                    Thread.Sleep(1200);
                    return row;
                }
            }
            Thread.Sleep(1000);
        }
        throw new TimeoutException($"List row '{text}' never appeared in the Management Client detail pane (GridRoles table or its rows were not found).");
    }

    /// <summary>Selects the tab (page header) with the given name in the detail pane.</summary>
    public static AutomationElement SelectTab(Window mainWindow, string nameContains, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var tab = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
                .FirstOrDefault(t => (t.Name ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase));
            if (tab != null)
            {
                try { tab.Patterns.SelectionItem.Pattern.Select(); } catch { tab.Click(); }
                Thread.Sleep(1500);
                return tab;
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException($"Tab '{nameContains}' not found in the Management Client detail pane.");
    }

    public static bool ClickMenuItem(Window mainWindow, string nameContains)
    {
        var item = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(m => (m.Name ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        if (item == null) return false;
        try { item.Patterns.Invoke.Pattern.Invoke(); } catch { item.Click(); }
        Thread.Sleep(1500);
        return true;
    }

    public static AutomationElement ExpectControl(Window mainWindow, string automationId, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var el = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (el != null) return el;
            Thread.Sleep(500);
        }
        throw new TimeoutException($"Management Client detail pane control '{automationId}' never appeared.");
    }

    public static void SetValue(Window mainWindow, string automationId, string value)
    {
        var box = ExpectControl(mainWindow, automationId);
        box.Focus();
        if (box.Patterns.Value.IsSupported)
        {
            box.Patterns.Value.Pattern.SetValue(value);
        }
        else
        {
            FlaUI.Core.Input.Keyboard.Type(value);
        }
    }

    public static bool Save(Window mainWindow, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var button = mainWindow.FindAllDescendants(cf => cf.ByName("Save"))
                .FirstOrDefault(b => b.ControlType == ControlType.Button && b.IsEnabled);
            if (button != null)
            {
                button.Patterns.Invoke.Pattern.Invoke();
                Thread.Sleep(2500);
                return true;
            }
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>Handles the yes/no confirmation MessageBox WinForms pops for deletes.</summary>
    public static void AcceptConfirmation(ClientSession session, string buttonName = "Yes", int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var window in session.Windows())
            {
                try
                {
                    var cls = window.ClassName ?? string.Empty;
                    if (!cls.Contains("#32770") && !(window.Name ?? "").Contains("confirm", StringComparison.OrdinalIgnoreCase)
                        && !(window.Name ?? "").Contains("delete", StringComparison.OrdinalIgnoreCase)) continue;
                    var yes = window.FindAllDescendants(cf => cf.ByName(buttonName)).FirstOrDefault();
                    if (yes != null) { yes.Patterns.Invoke.Pattern.Invoke(); return; }
                }
                catch { }
            }
            Thread.Sleep(500);
        }
    }
}
