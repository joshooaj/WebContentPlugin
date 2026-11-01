using System.Text;
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
            Thread.Sleep(250);
        }
        throw new TimeoutException($"Tree item '{name}' never appeared.");
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
            Thread.Sleep(50);
        }
        throw new TimeoutException($"Tree item containing '{fragment}' never appeared.");
    }

    public static void SelectTreeItem(Window mainWindow, string name)
    {
        SelectNode(mainWindow, name);
        Thread.Sleep(50);
    }

    /// <summary>
    /// Closes the "ItemName.set ... Not valid to set dirty state at the line!!!" error dialog MC
    /// raises when its tree rename flow misbehaves. The dialog is modal and blocks every later
    /// UIA interaction, so it must be dismissed before continuing.
    /// </summary>
    public static void DismissItemNameSetErrors(ClientSession session)
    {
        foreach (var dialog in session.Windows().ToList())
        {
            var title = dialog.Name ?? string.Empty;
            if (!title.Contains("ItemName.set", StringComparison.OrdinalIgnoreCase)
                && !title.Contains("dirty state", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var ok = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => string.Equals((b.Name ?? string.Empty).Trim(), "OK", StringComparison.OrdinalIgnoreCase));
                if (ok != null) ok.Patterns.Invoke.Pattern.Invoke();
                else dialog.Close();
                Thread.Sleep(800);
            }
            catch { }
        }
    }

    /// <summary>
    /// Cancels the WinForms tree's inline rename editor. While editing, the tree hosts the node
    /// name in a ControlType.Edit control (WindowsForms10.EDIT) positioned inside the tree column;
    /// the screenshot shows the node with a pencil badge and the label underlined. Pressing ESC on
    /// the focused tree cancels it, and a single click on the label restores clean selection.
    /// Call right before any capture that includes the tree.
    /// </summary>
    public static void ExitTreeEditMode(Window mainWindow, string name)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!TreeEditActive(mainWindow, name)) return;
            var item = FindTreeItem(mainWindow, name);
            try { if (item != null) item.Focus(); } catch { }
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Thread.Sleep(700);
            // Re-select via UIA only: a mouse click on the label of a selected node is exactly
            // what arms the rename editor in the first place.
            try { if (item != null && item.Patterns.SelectionItem.IsSupported) item.Patterns.SelectionItem.Pattern.Select(); } catch { }
            Thread.Sleep(700);
        }
        if (TreeEditActive(mainWindow, name))
            throw new InvalidOperationException($"Tree node '{name}' stayed in inline edit mode after repeated ESC.");
    }

    /// <summary>
    /// True when the tree column hosts an Edit control whose value equals the node name (inline
    /// rename). Detail-pane edits like txtName hold the same value but sit outside the tree column.
    /// </summary>
    private static bool TreeEditActive(Window mainWindow, string name)
    {
        var tree = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PlatformTree"));
        if (tree == null) return false;
        var tr = tree.BoundingRectangle;
        if (tr.Width <= 0) return false;
        foreach (var edit in mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
        {
            try
            {
                var r = edit.BoundingRectangle;
                if (r.Width <= 0 || r.X + r.Width < tr.X || r.X > tr.X + tr.Width) continue;
                if (edit.Patterns.Value.IsSupported &&
                    string.Equals(edit.Patterns.Value.Pattern.Value.Value, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
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
        Thread.Sleep(150);
        try
        {
            if (item.Patterns.SelectionItem.IsSupported) item.Patterns.SelectionItem.Pattern.Select();
        }
        catch { }
        Thread.Sleep(200);
        ClickLabel(mainWindow, item);
        // MC's tree has LabelEdit active: clicking the label of an already-selected node arms
        // the inline rename editor. ESC on the freshly focused tree cancels that arming; without
        // this, the captured node renders in edit mode and rename commits can even raise
        // "ItemName.set ...: Not valid to set dirty state at the line!!!" error dialogs.
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(150);
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
        Thread.Sleep(1000);

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
        Thread.Sleep(1000);
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
    /// Ensures the named tree node is expanded. Navigation is enumerate-and-click: the realized
    /// rows are listed, the target's label is double-clicked, and the wheel scrolls when the node
    /// or its children sit below the fold (MC tree rows are only realized while visible).
    /// Expansion state is read from geometry — the MC tree exposes NO ExpandCollapse pattern, but
    /// a node is expanded exactly when the next realized row is indented deeper, the same trick
    /// CollapseTreeToRoot uses. The old keyboard walk (O(index) DOWN presses per attempt, re-walked
    /// on every retry, plus fixed 800–1500 ms sleeps) was the source of the 10+ second stalls.
    /// </summary>
    public static void EnsureExpanded(Window mainWindow, string name, params string[] expectedChildNames)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var strategy = 0;
        var scrollPass = 0;
        var ancestorsTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (DateTime.UtcNow < deadline)
        {
            if (expectedChildNames.Length > 0 && ChildrenAppeared(mainWindow, expectedChildNames)) return;

            var items = RealizedTreeItems(mainWindow);
            var node = items.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

            if (node == null)
            {
                // Target not realized: wheel-scroll the whole list looking for it, then open
                // collapsed level-1 ancestors one at a time until the subtree materializes.
                if (scrollPass < MaxTreeScrollPasses)
                {
                    WheelTree(mainWindow, +4);
                    scrollPass++;
                    continue;
                }
                WheelTree(mainWindow, -30);
                scrollPass = 0;
                if (!ExpandOneCollapsedLevel1(mainWindow, ancestorsTouched)) break;
                continue;
            }

            var expanded = IsExpandedIn(items, node);
            if (expanded && expectedChildNames.Length == 0) return;

            if (!expanded)
            {
                if (items.IndexOf(node) + 1 >= items.Count && scrollPass < MaxTreeScrollPasses)
                {
                    // Last realized row: its children may simply sit unrealized below the fold.
                    WheelTree(mainWindow, +4);
                    scrollPass++;
                    continue;
                }
                ToggleNode(node, strategy++ % 3);
                Thread.Sleep(250);
                continue;
            }

            // Expanded but the expected children never showed up in any scan: below the fold.
            if (scrollPass < MaxTreeScrollPasses)
            {
                WheelTree(mainWindow, +4);
                scrollPass++;
                continue;
            }
            ToggleNode(node, strategy++ % 3);
            Thread.Sleep(250);
        }

        if (expectedChildNames.Length > 0 && !ChildrenAppeared(mainWindow, expectedChildNames))
            throw new InvalidOperationException($"Could not expand '{name}': none of [{string.Join(", ", expectedChildNames)}] appeared.");
        if (expectedChildNames.Length == 0)
            throw new InvalidOperationException($"Could not expand '{name}': geometry never showed a deeper-indented row under it.");
    }

    /// <summary>Tree rows the WinForms tree currently realizes, in visual (top-to-bottom) order.</summary>
    private static List<AutomationElement> RealizedTreeItems(Window mainWindow)
    {
        var treeNav = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("TreeNavigation"));
        var items = (treeNav ?? (AutomationElement)mainWindow)
            .FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .Where(t => { try { return t.BoundingRectangle.Height > 0; } catch { return false; } })
            .ToList();
        items.Sort((x, y) => x.BoundingRectangle.Y.CompareTo(y.BoundingRectangle.Y));
        return items;
    }

    /// <summary>True exactly when the next realized row is indented deeper than the node.</summary>
    private static bool IsExpandedIn(List<AutomationElement> items, AutomationElement node)
    {
        var idx = items.IndexOf(node);
        if (idx < 0 || idx + 1 >= items.Count) return false;
        var a = items[idx].BoundingRectangle;
        var b = items[idx + 1].BoundingRectangle;
        if (a.Width <= 0 || b.Width <= 0) return false;
        return b.X > a.X + 5;
    }

    private const int MaxTreeScrollPasses = 12;

    /// <summary>
    /// Double-clicks the first realized, collapsed level-1 row (Home and already-tried rows are
    /// skipped) so the subtree that should contain the target becomes realized. One ancestor per
    /// call keeps the search verifiable; the touched-set stops the next round re-collapsing it.
    /// </summary>
    private static bool ExpandOneCollapsedLevel1(Window mainWindow, HashSet<string> touched)
    {
        var items = RealizedTreeItems(mainWindow);
        var home = items.FirstOrDefault(t => (t.Name ?? string.Empty).StartsWith("Home", StringComparison.OrdinalIgnoreCase));
        if (home == null) return false;
        if (items.Count == 1)
        {
            ToggleNode(home, 0); // MC persists tree state: the whole tree can start collapsed at Home.
            Thread.Sleep(300);
            return true;
        }
        var homeIdx = items.IndexOf(home);
        if (homeIdx + 1 >= items.Count) return false;
        var level1X = items[homeIdx + 1].BoundingRectangle.X;
        for (var i = homeIdx + 1; i < items.Count; i++)
        {
            var node = items[i];
            if (Math.Abs(node.BoundingRectangle.X - level1X) > 1) continue;
            if (i + 1 < items.Count && items[i + 1].BoundingRectangle.X > node.BoundingRectangle.X + 5) continue;
            if (!touched.Add(node.Name ?? string.Empty)) continue;
            ToggleNode(node, 1);
            Thread.Sleep(300);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Toggles expand/collapse of a node. Strategy 0 uses LegacyIAccessible.DoDefaultAction
    /// (works for most nodes), strategy 1 does a real double-click on the label (proven for nodes
    /// where DoDefaultAction is a silent no-op), strategy 2 selects the node first, then double-clicks.
    /// EnsureExpanded verifies the toggle by geometry and escalates strategies, so no keyboard
    /// fallback is needed here.
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
    /// Returns the tree to the canonical clean state: Home expanded, every level-1 child of Home
    /// collapsed (optionally allowing some nodes to stay open, e.g. the plugin node the caller will
    /// use next). In one snapshot of the realized rows a level-1 node is expanded exactly when the
    /// row directly after it is indented deeper (larger BoundingRectangle.X); all level-1 rows
    /// share one indent X. Double-clicking an expanded level-1 node's label collapses it, and the
    /// TreeView re-realizes the rows beneath — which pulls previously off-screen level-1 nodes into
    /// the classification on later rounds. Nodes whose double-click does not take are retried via
    /// the keyboard route (click Home, walk DOWN, press LEFT).
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
            var treeNav = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("TreeNavigation"));
            var items = (treeNav ?? (AutomationElement)mainWindow)
                .FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).ToList();
            var home = items.FirstOrDefault(t => (t.Name ?? string.Empty).StartsWith("Home", StringComparison.OrdinalIgnoreCase));
            if (home == null) { Artifacts.WriteText("mc-collapse-log.txt", log.AppendLine("Home not found").ToString()); return; }

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

            var homeIdx = items.IndexOf(home);
            if (homeIdx + 1 >= items.Count) { log.AppendLine("Home is the last realized row"); break; }

            // Level-1 rows share the X of the first realized row below Home. A level-1 node is
            // expanded exactly when the next realized row is indented deeper under it (larger X).
            var level1X = items[homeIdx + 1].BoundingRectangle.X;
            var expanded = new List<(int Index, AutomationElement Node)>();
            for (var i = homeIdx + 1; i + 1 < items.Count; i++)
            {
                var node = items[i];
                if (allow.Contains(node.Name ?? string.Empty)) continue;
                var a = node.BoundingRectangle;
                var b = items[i + 1].BoundingRectangle;
                if (a.Width <= 0 || b.Width <= 0) continue;
                if (Math.Abs(a.X - level1X) > 1) continue;
                if (b.X <= a.X + 5) continue;
                bool offscreen;
                try { offscreen = node.IsOffscreen; } catch { offscreen = false; }
                if (offscreen) continue;
                expanded.Add((i, node));
            }

            if (expanded.Count == 0)
            {
                log.AppendLine($"tree collapsed to root (realized items={items.Count}, homeIdx={homeIdx}, level1X={level1X})");
                Artifacts.WriteText("mc-collapse-log.txt", log.ToString());
                return;
            }

            foreach (var pair in expanded)
            {
                var name = pair.Node.Name ?? string.Empty;
                strikes[name] = strikes.GetValueOrDefault(name) + 1;
                log.AppendLine($"collapse level-1 '{name}' idx={pair.Index} strike={strikes[name]}");
                var pt = strikes[name] >= MaxStrikesPerNode ? null : ClickablePoint(pair.Node);
                if (pt.HasValue)
                {
                    FlaUI.Core.Input.Mouse.DoubleClick(pt.Value);
                    Thread.Sleep(400);
                }
                else
                {
                    // Double-click silently not taking on this node: the keyboard route proven for
                    // plugin-injected nodes — click Home (focus + select index 0), walk DOWN to the
                    // node's visible index, press LEFT to collapse.
                    KeyboardWalkTo(mainWindow, home, pair.Index);
                    FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.LEFT);
                    Thread.Sleep(800);
                }
            }
        }
        Artifacts.WriteText("mc-collapse-log.txt", log.ToString());
        throw new TimeoutException("Could not collapse the Management Client tree to the Home root.");
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

    /// <summary>
    /// Polls until the navigation tree exists and its first TreeItem (the Home root) is realized.
    /// MC realizes the tree some time AFTER the main window appears, so this replaces fixed
    /// startup sleeps: it proceeds as soon as CollapseTreeToRoot can actually do work.
    /// </summary>
    public static bool WaitForTreeReady(Window mainWindow, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("TreeNavigation")) != null
                    && mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)).Length > 0)
                    return true;
            }
            catch { }
            Thread.Sleep(300);
        }
        return false;
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
        Thread.Sleep(150);
        for (var i = 0; i < Math.Abs(clicks); i++)
        {
            InputUtil.Wheel(cx, cy, clicks > 0 ? -120 : 120);
            Thread.Sleep(40);
        }
        Thread.Sleep(250);
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

    /// <summary>
    /// Replace the full contents of a detail-pane Edit through the keyboard route the login
    /// harness proved reliable: focus (VERIFIED), select-all, CF_UNICODETEXT clipboard paste.
    /// The verification is not decoration - an ungated run pasted the proxy-target list into
    /// whatever field held focus (txtName), and the next Save renamed the workspace item to
    /// 'http://httpforever.com' (MIP.log 19:12:31). Returns false when focus could not be
    /// proven or the clipboard was unavailable; it NEVER types into an unproven control.
    /// </summary>
    public static bool PasteIntoControl(Window mainWindow, string automationId, string value)
    {
        var box = ExpectControl(mainWindow, automationId);
        if (!FocusVerified(mainWindow, box, automationId)) return false;
        if (!LoginFlow.TryClipboardSetText(value)) return false;
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                                                      FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
        System.Threading.Thread.Sleep(150);
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                                                      FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_V);
        System.Threading.Thread.Sleep(300);
        return true;
    }

    /// <summary>
    /// Focus an element and prove it took: UIA FocusedElement must carry the expected
    /// AutomationId before any keystroke is sent. Retries the focus call once.
    /// </summary>
    public static bool FocusVerified(Window mainWindow, AutomationElement element, string expectedAutomationId)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { element.Focus(); } catch { }
            System.Threading.Thread.Sleep(300);
            try
            {
                var focused = mainWindow.Automation.FocusedElement();
                if (focused != null && string.Equals(focused.AutomationId, expectedAutomationId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Dirty the detail pane through the checkbox's Checked/Unchecked handlers: two Toggle
    /// pattern flips leave the state as it was but fire the page's change events twice.
    /// Programmatic TextBox writes proved not to raise the shell's dirty chain in a freshly
    /// logged-in MC, while Toggle-driven changes do.
    /// </summary>
    public static bool DirtyPaneViaCheckbox(Window mainWindow, string automationId)
    {
        try
        {
            var box = ExpectControl(mainWindow, automationId, 10);
            if (!box.Patterns.Toggle.IsSupported) return false;
            var toggle = box.Patterns.Toggle.Pattern;
            var wasOn = toggle.ToggleState == FlaUI.Core.Definitions.ToggleState.On;
            toggle.Toggle();
            System.Threading.Thread.Sleep(300);
            toggle.Toggle();
            System.Threading.Thread.Sleep(300);
            return toggle.ToggleState == (wasOn ? FlaUI.Core.Definitions.ToggleState.On : FlaUI.Core.Definitions.ToggleState.Off);
        }
        catch { return false; }
    }

    /// <summary>True once the shell exposes an enabled, invokable "Save".</summary>
    public static bool WaitForSaveEnabled(Window mainWindow, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { if (FindSaveButton(mainWindow, -1) != null) return true; }
            catch { }
            Thread.Sleep(400);
        }
        return false;
    }

    /// <summary>
    /// Focus the Edit (verified) and press END, SPACE, BACK: a real keyboard round-trip through
    /// WPF input that fires TextChanged even when a programmatic write left the shell's dirty
    /// state cold. The box contents end up unchanged; nothing is typed without the focus gate.
    /// </summary>
    public static void NudgeControlWithKeystroke(Window mainWindow, string automationId)
    {
        var box = ExpectControl(mainWindow, automationId, 10);
        if (!FocusVerified(mainWindow, box, automationId)) return;
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.END);
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.SPACE);
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.BACK);
        System.Threading.Thread.Sleep(300);
    }

    /// <summary>
    /// Name of the single workspace item under the workspaces node when the expected name is
    /// gone - an item renamed by a stray paste still shows up in the tree under its bad name.
    /// Null when the node has no deeper-indented child at all.
    /// </summary>
    public static string OrphanWorkspaceName(Window mainWindow, string workspacesNodeName)
    {
        var items = RealizedTreeItems(mainWindow);
        var node = items.FirstOrDefault(t => string.Equals(t.Name, workspacesNodeName, StringComparison.OrdinalIgnoreCase));
        if (node == null) return null;
        var idx = items.IndexOf(node);
        for (var i = idx + 1; i < items.Count; i++)
        {
            try
            {
                if (items[i].BoundingRectangle.X > node.BoundingRectangle.X) return items[i].Name;
                break;
            }
            catch { break; }
        }
        return null;
    }

    /// <summary>Every realized tree row (name, indent X, Y) - so a "no children appeared"
    /// failure says what the tree ACTUALLY shows.</summary>
    public static void DumpWorkspaceChildren(Window mainWindow, StringBuilder log)
    {
        try
        {
            var items = RealizedTreeItems(mainWindow);
            var idx = items.FindIndex(t => string.Equals(t.Name, WorkspacesNodeName, StringComparison.OrdinalIgnoreCase));
            log.AppendLine($"tree rows={items.Count}, workspaces-node idx={(idx < 0 ? "ABSENT" : idx.ToString())}");
            foreach (var t in items)
            {
                var r = t.BoundingRectangle;
                log.AppendLine($"  '{t.Name}' x={r.X} y={r.Y}");
            }
        }
        catch (Exception ex) { log.AppendLine("tree dump threw " + ex.GetType().Name); }
    }

    /// <summary>
    /// Create a new item of the kind via the shell's 'Add New ...' menu entry (the tree
    /// right-click opens no usable context menu on this shell - the menu bar census proved
    /// 'Add New ...' is the real affordance). Every window/dialog that shows up afterwards is
    /// logged so the follow-up dialog's shape is recorded, not guessed.
    /// </summary>
    public static bool NewItemUnderNode(Window mainWindow, ClientSession session, string nodeName, StringBuilder log)
    {
        var addNew = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
            .FirstOrDefault(mi => (mi.Name ?? string.Empty).IndexOf("add new", StringComparison.OrdinalIgnoreCase) >= 0);
        if (addNew == null)
        {
            log.AppendLine("no 'Add New ...' menu-bar entry found");
            return false;
        }
        try { addNew.Patterns.Invoke.Pattern.Invoke(); }
        catch (Exception ex) { log.AppendLine("Add New invoke threw " + ex.GetType().Name); try { addNew.Click(); } catch { } }
        Thread.Sleep(2500);

        // What did it open? Log windows, their edits/buttons, and any menu items left over.
        try
        {
            foreach (var w in session.Windows())
            {
                string Safex(Func<string> f) { try { return f() ?? ""; } catch { return "<threw>"; } }
                log.AppendLine($"after AddNew window aid='{Safex(() => w.AutomationId)}' name='{Safex(() => w.Name)}' class='{Safex(() => w.ClassName)}'");
                try
                {
                    foreach (var b in w.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Take(10))
                        log.AppendLine($"   edit aid='{Safex(() => b.AutomationId)}' name='{Safex(() => b.Name)}'");
                    foreach (var b in w.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Take(15))
                        log.AppendLine($"   button aid='{Safex(() => b.AutomationId)}' name='{Safex(() => b.Name)}'");
                    foreach (var b in w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Take(15))
                        log.AppendLine($"   text name='{Safex(() => b.Name)}'");
                }
                catch { }
            }
        }
        catch (Exception ex) { log.AppendLine("AddNew aftermath dump threw " + ex.GetType().Name); }

        // If the shell opened a kind-picker, select our kind before accepting the dialog.
        try
        {
            foreach (var w in session.Windows())
            {
                foreach (var kind in w.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                            .Concat(w.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
                            .Where(e => (e.Name ?? string.Empty).IndexOf("Web Content", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    log.AppendLine($"AddNew picker: choosing '{kind.Name}'");
                    try { kind.Patterns.Invoke.Pattern.Invoke(); } catch { try { kind.Click(); } catch { } }
                    Thread.Sleep(1500);
                    break;
                }
            }
        }
        catch { }
        return true;
    }

    /// <summary>
    /// Re-type the pane's Name field (focus-verified keyboard only): select-all, then plain
    /// ASCII typing. Throws when focus cannot be proven - the field is never typed blind.
    /// </summary>
    public static void RenameWorkspacePane(Window mainWindow, string newName)
    {
        var box = ExpectControl(mainWindow, "txtName", 15);
        if (!FocusVerified(mainWindow, box, "txtName"))
            throw new InvalidOperationException("Cannot verify focus on txtName; refusing to type the workspace rename.");
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                                                      FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
        System.Threading.Thread.Sleep(150);
        FlaUI.Core.Input.Keyboard.Type(newName);
        System.Threading.Thread.Sleep(300);
    }

    /// <summary>
    /// Drives a checkbox in the detail pane (WPF hosted control: AutomationId is the x:Name)
    /// to the requested state using the UIA Toggle pattern.
    /// </summary>
    public static void EnsureCheckBox(Window mainWindow, string automationId, bool on)
    {
        var box = ExpectControl(mainWindow, automationId, 30);
        var desired = on ? FlaUI.Core.Definitions.ToggleState.On : FlaUI.Core.Definitions.ToggleState.Off;
        for (var attempt = 0; attempt < 5 && box.Patterns.Toggle.Pattern.ToggleState.Value != desired; attempt++)
        {
            try { box.Patterns.Toggle.Pattern.Toggle(); } catch { box.Click(); }
            Thread.Sleep(500);
        }
        if (box.Patterns.Toggle.Pattern.ToggleState.Value != desired)
            throw new InvalidOperationException($"Checkbox '{automationId}' never reached state {(on ? "On" : "Off")}.");
    }

    /// <summary>
    /// Deletes the named workspace item through its tree context menu when it exists.
    /// Returns false when the item was not present at all.
    /// </summary>
    public static bool DeleteWorkspaceIfPresent(ClientSession session, Window mainWindow, string name)
    {
        var item = FindTreeItem(mainWindow, name);
        if (item == null) return false;
        OpenContextMenu(session, mainWindow, item);
        if (!(ClickMenuItem(session, "Delete") || ClickMenuItem(session, "Remove")))
            throw new InvalidOperationException($"No Delete entry in the context menu of '{name}'.");
        AcceptConfirmation(session);
        Save(mainWindow);
        Thread.Sleep(1500);
        return true;
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

    /// <summary>
    /// Session-aware Save: clicking Save on the proxy-target page raises a shell confirmation
    /// modal ("Save changes?"). UIA Invoke BLOCKS for the whole modal lifetime — a harness
    /// thread inside Invoke cannot clear the modal it is waiting on, which is exactly how the
    /// #56 row hung until a human clicked Yes. So the click runs on a side thread while this
    /// thread sweeps modals; a modal that is already up before the click is swept first.
    /// </summary>
    public static bool Save(ClientSession session, Window mainWindow, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            AcceptPendingModal(session, 1);
            AutomationElement button = null;
            try
            {
                button = FindSaveButton(mainWindow, attempt);
            }
            catch (Exception ex)
            {
                LogSaveAttempt(attempt, "locator threw " + ex.GetType().Name + ": " + ex.Message);
            }
            if (button != null)
            {
                try
                {
                    var invoke = System.Threading.Tasks.Task.Run(() => button.Patterns.Invoke.Pattern.Invoke());
                    if (!invoke.Wait(3000))
                    {
                        // Still blocked: a modal owns the click. Accept it, then the click returns.
                        AcceptPendingModal(session, 10);
                        invoke.Wait(TimeSpan.FromSeconds(20));
                    }
                    else
                    {
                        AcceptPendingModal(session, 4);
                    }
                }
                catch (AggregateException agg) when (IsStaleButton(agg))
                {
                    // MC re-renders the pane after an edit and disables Save for a moment;
                    // IsEnabled raced the Invoke. Re-find the button and retry inside the deadline.
                    Thread.Sleep(500);
                    continue;
                }

                Thread.Sleep(2500);
                AcceptPendingModal(session, 2);
                return true;
            }
            attempt++;
            Thread.Sleep(500);
        }
        DumpSaveCandidates(mainWindow);
        return false;
    }

    /// <summary>
    /// The shell's enabled "Save" affordance. Name match is any control type: the MC toolbar
    /// renders Save as a button, but a toolbar element surfacing through the WinForms/WPF
    /// bridge can report as Text or Custom, and insisting on ControlType.Button hid a live,
    /// enabled Save from the harness. Each poll records what every name-matching candidate
    /// looked like, because "never became enabled" without the toolbar's actual state was a
    /// guess-and-recheck loop across several runs.
    /// </summary>
    private static AutomationElement FindSaveButton(Window mainWindow, int attempt)
    {
        var candidates = mainWindow.FindAllDescendants(cf => cf.ByName("Save"));
        var described = new List<string>();
        AutomationElement chosen = null;
        foreach (var b in candidates)
        {
            var ct = TryGet(() => b.ControlType.ToString());
            var enabled = TryGet(() => b.IsEnabled.ToString());
            var invoke = TryGet(() => b.Patterns.Invoke.IsSupported.ToString());
            described.Add($"ct={ct} enabled={enabled} invoke={invoke}");
            if (enabled != "True" || invoke != "True") continue;
            // Prefer the VISIBLE toolbar affordance; an application-menu 'Save' MenuItem
            // (zero rect) enumerates first but is the riskier click target.
            var visible = TryGet(() => b.BoundingRectangle.Width.ToString()) != "0";
            var chosenVisible = chosen != null && TryGet(() => chosen.BoundingRectangle.Width.ToString()) != "0";
            if (chosen == null || (visible && !chosenVisible)) chosen = b;
        }
        if (attempt >= 0 && attempt < 40)
        {
            LogSaveAttempt(attempt, described.Count == 0 ? "no ByName('Save') candidates" : string.Join(" | ", described));
        }
        return chosen;
    }

    private static string TryGet(Func<object> read)
    {
        try { return Convert.ToString(read()); }
        catch (Exception ex) { return "<threw " + ex.GetType().Name + ">"; }
    }

    private static void LogSaveAttempt(int attempt, string line)
    {
        try
        {
            File.AppendAllText(Artifacts.PathOf("mc-save-attempts.txt"),
                $"{DateTime.Now:HH:mm:ss} attempt={attempt} {line}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Failure evidence for "Save never became enabled": every element in the shell whose name
    /// mentions save/discard/revert, with control type and enabled state - so the next run
    /// says WHAT the toolbar actually exposes instead of guessing.
    /// </summary>
    private static void DumpSaveCandidates(Window mainWindow)
    {
        try
        {
            var all = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Custom)
                .Or(cf.ByControlType(ControlType.Button))
                .Or(cf.ByControlType(ControlType.Text))
                .Or(cf.ByControlType(ControlType.MenuItem))
                .Or(cf.ByControlType(ControlType.Hyperlink)));
            var hits = all.Where(e =>
            {
                var n = e.Name ?? string.Empty;
                return n.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("discard", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("revert", StringComparison.OrdinalIgnoreCase) >= 0;
            });
            var sb = new StringBuilder();
            sb.AppendLine($"save-census {DateTime.Now:HH:mm:ss} candidates={hits.Count()}");
            foreach (var e in hits)
            {
                sb.AppendLine($"  ct={TryGet(() => e.ControlType.ToString())} aid={TryGet(() => e.AutomationId)} name={TryGet(() => e.Name)} enabled={TryGet(() => e.IsEnabled)} invoke={TryGet(() => e.Patterns.Invoke.IsSupported)}");
            }
            File.AppendAllText(Artifacts.PathOf("mc-save-census.txt"), sb + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>True when the aggregate carries only "element not enabled" UIA failures (0x80040200).</summary>
    private static bool IsStaleButton(AggregateException aggregate)
    {
        foreach (var inner in aggregate.Flatten().InnerExceptions)
        {
            var code = (inner as System.Runtime.InteropServices.COMException)?.ErrorCode;
            var stale = inner.GetType().Name == "ElementNotEnabledException" || code == unchecked((int)0x80040200);
            if (!stale)
            {
                return false;
            }
        }

        return aggregate.InnerExceptions.Count > 0;
    }

    /// <summary>
    /// Clicks the affirmative button of any pending MC modal dialog (the "Save changes?"
    /// shell confirmation, a #32770 MessageBox, or a similar notice) that is not the main
    /// window. Returns true when a modal was accepted. Button preference: Yes, Save, OK.
    /// </summary>
    public static bool AcceptPendingModal(ClientSession session, int timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var window in session.Windows())
            {
                try
                {
                    // The save-changes confirmation carries the SAME title as the main window
                    // ("Milestone XProtect Management Client <version>"), so titles cannot tell
                    // them apart; structure can: the main window has the application AutomationId,
                    // and a confirmation is any other window pairing an affirmative button with a
                    // negative one (Yes/No/Cancel) or wearing the dialog window class.
                    if (string.Equals(window.AutomationId ?? string.Empty, "_mainApplicationWindow", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var cls = window.ClassName ?? string.Empty;
                    var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).ToList();
                    if (buttons.Count == 0) continue;

                    var hasNegative = buttons.Any(b =>
                        string.Equals((b.Name ?? string.Empty).Trim(), "No", StringComparison.OrdinalIgnoreCase)
                        || string.Equals((b.Name ?? string.Empty).Trim(), "Cancel", StringComparison.OrdinalIgnoreCase));
                    if (!cls.Contains("#32770") && !hasNegative) continue;

                    foreach (var wanted in new[] { "Yes", "Save", "OK" })
                    {
                        var hit = buttons.FirstOrDefault(b =>
                            string.Equals((b.Name ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase));
                        if (hit != null)
                        {
                            hit.Patterns.Invoke.Pattern.Invoke();
                            return true;
                        }
                    }
                }
                catch { }
            }
            Thread.Sleep(400);
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
