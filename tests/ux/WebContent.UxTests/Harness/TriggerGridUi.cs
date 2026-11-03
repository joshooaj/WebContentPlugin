using System.Text;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #78: shared Management Client phases for driving and reading the Proxy tab's
/// "Rule triggers" grid (proxyTriggersGrid). Same shape as HeaderGridUi: every per-row control
/// carries ONE shared AutomationId (proxyTriggerRowUrl, proxyTriggerRowMethod,
/// proxyTriggerRowStatus, proxyTriggerRowEvent, proxyTriggerRowProblem, proxyTriggerRowRemove),
/// so every lookup is scoped to the grid element first and rows are addressed by document order.
/// Virtualization is off in the grid's XAML, so the ordering cannot drift.
///
/// The Method cell is a non-editable WPF ComboBox, not a TextBox: writes go through
/// ExpandCollapse + SelectionItem.Select on the matching item, escalating to focus-gated arrow
/// keys (a closed WPF combo changes SelectedItem with Up/Down), and the readback is the combo's
/// UIA Name, which WPF keeps equal to the selected item's text. The read-only Event column is a
/// TextBlock read by Name like the Problem cell.
///
/// Same discipline as the rest of the harness: every edit verifies by observable readback, and
/// every "it saved" claim goes through two independent channels — the plugin's own "Stored proxy
/// triggers ..." audit line in the MC MIP.log, and the grid re-read from a RELOADED pane,
/// order-sensitively (the Event Server evaluates rows top-down; first match wins).
/// </summary>
public static class TriggerGridUi
{
    public const string GridId = "proxyTriggersGrid";
    public const string UrlCellId = "proxyTriggerRowUrl";
    public const string MethodCellId = "proxyTriggerRowMethod";
    public const string StatusCellId = "proxyTriggerRowStatus";
    public const string EventCellId = "proxyTriggerRowEvent";
    public const string ProblemCellId = "proxyTriggerRowProblem";
    public const string RemoveCellId = "proxyTriggerRowRemove";
    public const string AddButtonId = "btnProxyTriggerAdd";

    /// <summary>The dropdown's verb set in XAML order — mirrors ProxyTriggerEditor.ProxyTriggerMethods
    /// (index 0 is the any-method "*"). The arrow-key escalation counts steps on this list.</summary>
    private static readonly string[] MethodOrder = { "*", "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "PATCH" };

    /// <summary>Where the grid driver's step notes go (same convention as AllowlistGridUi.Trace).</summary>
    public static Action<string> Trace { get; set; } = _ => { };

    /// <summary>One trigger row as UIA reads it. Describe renders the stored-row shape the plugin's
    /// audit line uses: "url=&lt;url&gt; method=&lt;verb|any&gt; status=&lt;filter|any&gt;".</summary>
    public sealed class RowReading
    {
        public int Index;
        public string Url = string.Empty;
        public string Method = string.Empty;
        public string Status = string.Empty;
        public string Event = string.Empty;
        public string Problem = string.Empty;

        public string Describe() =>
            $"url={Url} method={(Method == "*" ? "any" : Method)} status={(Status.Length == 0 ? "any" : Status)}";
    }

    /// <summary>The wanted-row shape callers pass in: (url, method, status); method "*" = any.</summary>
    public sealed record Row(string Url, string Method, string Status)
    {
        public string Describe() =>
            $"url={Url} method={(Method == "*" ? "any" : Method)} status={(string.IsNullOrEmpty(Status) ? "any" : Status)}";
    }

    private static AutomationElement GridFor(Window mcMain) =>
        ManagementClientUi.ExpectControl(mcMain, GridId, 15);

    /// <summary>The grid-scoped cell funnel. Same COMException retry as HeaderGridUi: a mid-edit
    /// UIA tree walk can transiently throw while the DataGrid re-realizes underneath.</summary>
    private static List<AutomationElement> CellElements(AutomationElement grid, string cellId)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return grid.FindAllDescendants(cf => cf.ByAutomationId(cellId)).ToList();
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
            {
                Thread.Sleep(500 * (attempt + 1));
            }
        }
    }

    public static int RowCount(Window mcMain) => CellElements(GridFor(mcMain), UrlCellId).Count;

    public static List<RowReading> ReadRows(Window mcMain)
    {
        var grid = GridFor(mcMain);
        var urls = CellElements(grid, UrlCellId);
        var methods = CellElements(grid, MethodCellId);
        var statuses = CellElements(grid, StatusCellId);
        var events = CellElements(grid, EventCellId);
        var problems = CellElements(grid, ProblemCellId);

        var rows = new List<RowReading>();
        for (var i = 0; i < urls.Count; i++)
        {
            rows.Add(new RowReading
            {
                Index = i,
                Url = ReadValue(urls[i]),
                Method = i < methods.Count ? ReadComboSelection(methods[i]) : string.Empty,
                Status = i < statuses.Count ? ReadValue(statuses[i]) : string.Empty,
                Event = i < events.Count ? ReadText(events[i]) : string.Empty,
                Problem = i < problems.Count ? ReadText(problems[i]) : string.Empty,
            });
        }

        return rows;
    }

    public static string ReadProblem(Window mcMain, int index)
    {
        var problems = CellElements(GridFor(mcMain), ProblemCellId);
        if (index < 0 || index >= problems.Count)
            throw new InvalidOperationException($"no Problem cell at row {index} of '{GridId}' ({problems.Count} realized).");
        return ReadText(problems[index]);
    }

    /// <summary>Press the grid's Add button and wait for a new realized row.</summary>
    public static void AddRow(Window mcMain)
    {
        var before = RowCount(mcMain);
        var button = ManagementClientUi.ExpectControl(mcMain, AddButtonId, 10);
        for (var attempt = 0; attempt < 5 && RowCount(mcMain) == before; attempt++)
        {
            try { button.Patterns.Invoke.Pattern.Invoke(); }
            catch { button.Click(); }
            Thread.Sleep(600);
        }

        if (RowCount(mcMain) != before + 1)
            throw new InvalidOperationException($"'{AddButtonId}' did not append a row to '{GridId}' ({before} -> {RowCount(mcMain)}).");
    }

    /// <summary>Remove one row by index, waiting for the row count to drop.</summary>
    public static void RemoveRowAt(Window mcMain, int index)
    {
        var before = RowCount(mcMain);
        if (index < 0 || index >= before)
            throw new InvalidOperationException($"no row {index} in '{GridId}' to remove (rows={before}).");

        var grid = GridFor(mcMain);
        for (var attempt = 0; attempt < 5 && RowCount(mcMain) == before; attempt++)
        {
            var cells = CellElements(grid, RemoveCellId);
            if (index >= cells.Count) { Thread.Sleep(400); continue; }
            var button = cells[index];
            try { button.Patterns.Invoke.Pattern.Invoke(); }
            catch { button.Click(); }
            Thread.Sleep(600);
        }

        if (RowCount(mcMain) != before - 1)
            throw new InvalidOperationException($"remove on row {index} of '{GridId}' never dropped a row ({before} -> {RowCount(mcMain)}).");
    }

    public static void RemoveAllRows(Window mcMain)
    {
        for (var guard = 0; RowCount(mcMain) > 0 && guard < 50; guard++)
        {
            RemoveRowAt(mcMain, 0);
        }
    }

    /// <summary>Replace the whole grid with the wanted rows: remove every row, add one row per
    /// entry, fill and verify row by row (order preserved — the matcher evaluates top-down and
    /// raises at most the FIRST matching row's event).</summary>
    public static void SetRows(Window mcMain, IEnumerable<Row> rows)
    {
        RemoveAllRows(mcMain);
        foreach (var row in rows ?? Enumerable.Empty<Row>())
        {
            AddRow(mcMain);
            SetLastRow(mcMain, row);
        }
    }

    /// <summary>Fill the LAST row of the grid and verify it by readback, including the read-only
    /// Event column the row renders.</summary>
    public static void SetLastRow(Window mcMain, Row row)
    {
        var index = RowCount(mcMain) - 1;
        if (index < 0) throw new InvalidOperationException($"no row to set in '{GridId}'.");

        var grid = GridFor(mcMain);
        SetTextCell(mcMain, grid, UrlCellId, index, row.Url ?? string.Empty);
        SetComboCell(mcMain, grid, MethodCellId, index, string.IsNullOrEmpty(row.Method) ? "*" : row.Method);
        SetTextCell(mcMain, grid, StatusCellId, index, row.Status ?? string.Empty);

        var read = ReadRows(mcMain)[index];
        Trace($"'{GridId}' row {index}: '{row.Describe()}' reads url='{read.Url}' method='{read.Method}' status='{read.Status}' event='{read.Event}' problem='{read.Problem}'");
        if (!string.Equals(read.Url, row.Url ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(read.Method, string.IsNullOrEmpty(row.Method) ? "*" : row.Method, StringComparison.Ordinal)
            || !string.Equals(read.Status, row.Status ?? string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{GridId}' row {index} never read back as '{row.Describe()}' (reads '{read.Describe()}').");
        }
    }

    /// <summary>
    /// The trigger-save discipline, mirroring HeaderGridUi.SavePoliciesAndVerify: dirty-chain
    /// guards, the pre-Save Name check, Save with modal sweep, then the stored list read back
    /// from TWO independent channels — the plugin's own "Stored proxy triggers for workspace
    /// '...' : N row(s) [...]" audit line in the MC MIP.log, and the grid re-read from a
    /// RELOADED pane, ORDER-SENSITIVELY (configuration order is part of the policy: the first
    /// matching row wins, here and on the Event Server).
    /// </summary>
    public static void SaveTriggersAndVerify(
        ClientSession mc,
        Window mcMain,
        string workspacesNodeName,
        string workspaceName,
        IEnumerable<Row> expected,
        StringBuilder log)
    {
        Trace = message => log.AppendLine(message);
        var rows = (expected ?? Enumerable.Empty<Row>()).ToList();

        if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
        {
            log.AppendLine("Save still disabled after the trigger-grid edit; flipping the routing checkbox twice to dirty the pane");
            ManagementClientUi.DirtyPaneViaCheckbox(mcMain, AllowlistGridUi.ProxyEnabledId);
            if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
            {
                log.AppendLine("Save still disabled after the checkbox flip; keyboard-nudging the first trigger-grid url cell");
                try { ManagementClientUi.NudgeControlWithKeystroke(mcMain, UrlCellId); }
                catch (Exception ex) { log.AppendLine($"nudge skipped: {ex.GetType().Name}"); }
                log.AppendLine($"Save enabled after nudge: {ManagementClientUi.WaitForSaveEnabled(mcMain, 6)}");
            }
        }

        var paneName = AllowlistGridUi.ReadPaneName(mcMain);
        if (!string.Equals(paneName, workspaceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to Save: the Name field reads '{paneName}', expected '{workspaceName}'.");

        AllowlistGridUi.OpenProxyTab(mcMain);

        // The audit line this save writes, keyed on this call's exact row count AND exact row
        // renderings, so an older save of a different list cannot satisfy the delta.
        var auditMarker = $"Stored proxy triggers for workspace '{workspaceName}'";
        var tokens = AuditTokens(rows);
        var auditBefore = McLog.CountMatches(auditMarker, tokens);

        if (!ManagementClientUi.Save(mc, mcMain, 20))
            throw new InvalidOperationException(
                $"Save never became enabled for triggers [{string.Join("; ", rows.Select(r => r.Describe()))}].");

        var auditLine = McLog.WaitForNewLine(auditBefore, 20, auditMarker, tokens);
        if (auditLine == null)
            throw new InvalidOperationException(
                $"the save wrote no 'Stored proxy triggers' audit line carrying [{string.Join(" | ", tokens)}] " +
                $"({auditBefore} -> {McLog.CountMatches(auditMarker, tokens)}). " +
                "The save either never ran or stored something other than the grid showed.");
        log.AppendLine($"stored triggers audit line: {auditLine}");

        Thread.Sleep(1500);
        AllowlistGridUi.ReloadPane(mcMain, workspacesNodeName, workspaceName);
        AllowlistGridUi.OpenProxyTab(mcMain);
        var stored = ReadRows(mcMain);
        if (!SameRows(rows, stored))
        {
            Artifacts.Window(mcMain, "mc-triggers-reload-mismatch");
            throw new InvalidOperationException(
                $"After Save + reload the trigger grid reads '{string.Join(" | ", stored.Select(r => r.Describe()))}' — expected '{string.Join(" | ", rows.Select(r => r.Describe()))}'.");
        }
        log.AppendLine(
            $"saved triggers [{string.Join("; ", rows.Select(r => r.Describe()))}] (verified from the audit line AND the reloaded grid, order-sensitive)");
    }

    /// <summary>The audit line fragments for one wanted list: the row count AND the exact
    /// rendering in brackets. ProxyTriggerEditor.DescribeStoredTriggers renders each row as
    /// "url=... method=...|any status=...|any", joined by "; " — mirrored exactly here.</summary>
    private static string[] AuditTokens(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        return new[]
        {
            $"{list.Count} row(s)",
            $"[{string.Join("; ", list.Select(r => r.Describe()))}]",
        };
    }

    /// <summary>Order-SENSITIVE row comparison: configuration order is part of the policy.</summary>
    public static bool SameRows(IEnumerable<Row> expected, IEnumerable<RowReading> actual)
    {
        var e = (expected ?? Enumerable.Empty<Row>()).Select(r => r.Describe()).ToList();
        var a = (actual ?? Enumerable.Empty<RowReading>()).Select(r => r.Describe()).ToList();
        return e.SequenceEqual(a, StringComparer.Ordinal);
    }

    // ---- cell primitives: read/verify, escalate the route on failure (same ladder as the
    //      header grids, but every lookup is scoped to the trigger grid) -----------------------

    private static void SetTextCell(Window mcMain, AutomationElement grid, string cellId, int index, string value)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var cell = CellElement(grid, cellId, index);
            try
            {
                if (cell.Patterns.Value.IsSupported)
                    cell.Patterns.Value.Pattern.SetValue(value);
            }
            catch { }

            Thread.Sleep(400);
            if (string.Equals(ReadValue(CellElement(grid, cellId, index)), value, StringComparison.Ordinal))
                return;

            // Keyboard route, focus-gated: the id is shared by every row of this grid, so the
            // focus gate only proves SOMETHING with this id holds focus — the row readback below
            // is what proves the write landed in THIS row.
            cell = CellElement(grid, cellId, index);
            if (ManagementClientUi.FocusVerified(mcMain, cell, cellId) && LoginFlow.TryClipboardSetText(value))
            {
                FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                                                             FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
                Thread.Sleep(150);
                FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                                                             FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_V);
                Thread.Sleep(400);
                if (string.Equals(ReadValue(CellElement(grid, cellId, index)), value, StringComparison.Ordinal))
                    return;
            }

            Thread.Sleep(400);
        }

        throw new InvalidOperationException(
            $"'{cellId}' row {index} never read back as '{value}' (reads '{ReadValue(CellElement(grid, cellId, index))}').");
    }

    /// <summary>Drive the Method dropdown to the wanted verb. Pattern route first (expand, then
    /// SelectionItem.Select on the item whose Name is the verb); escalation is focus-gated arrow
    /// keys — a closed WPF combo moves SelectedItem with Up/Down, and the dropdown's order is
    /// fixed, so counting steps from the readback lands on the verb.</summary>
    private static void SetComboCell(Window mcMain, AutomationElement grid, string cellId, int index, string verb)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var cell = CellElement(grid, cellId, index);
            try
            {
                var expandable = cell.Patterns.ExpandCollapse.IsSupported;
                if (expandable)
                    cell.Patterns.ExpandCollapse.Pattern.Expand();
                Thread.Sleep(400);
                var items = cell.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).ToList();
                var scope = "combo-scoped";
                if (items.Count == 0)
                {
                    // WPF dropdowns live in a popup that can attach OUTSIDE the combo's subtree -
                    // fall back to a window-wide search, restricted to the known verb set so no
                    // other list on the pane can be mistaken for this dropdown.
                    items = mcMain.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                        .Where(i => MethodOrder.Contains((i.Name ?? string.Empty).Trim()))
                        .ToList();
                    scope = "window-wide";
                }

                var match = items.FirstOrDefault(i => string.Equals((i.Name ?? string.Empty).Trim(), verb, StringComparison.Ordinal));
                Trace($"combo row {index} '{verb}': expand={expandable} items={items.Count} ({scope}) match={(match == null ? "none" : $"'{match.Name}' selectionItem={match.Patterns.SelectionItem.IsSupported}")}");
                if (match != null && match.Patterns.SelectionItem.IsSupported)
                    match.Patterns.SelectionItem.Pattern.Select();
                Thread.Sleep(300);
                if (cell.Patterns.ExpandCollapse.IsSupported)
                    cell.Patterns.ExpandCollapse.Pattern.Collapse();
            }
            catch (Exception ex)
            {
                Trace($"combo row {index} '{verb}' pattern route threw {ex.GetType().Name}: {ex.Message}");
            }

            Thread.Sleep(400);
            if (string.Equals(ReadComboSelection(CellElement(grid, cellId, index)), verb, StringComparison.Ordinal))
                return;

            // Keyboard route, focus-gated.
            cell = CellElement(grid, cellId, index);
            var current = Array.IndexOf(MethodOrder, ReadComboSelection(cell));
            var target = Array.IndexOf(MethodOrder, verb);
            var focusOk = ManagementClientUi.FocusVerified(mcMain, cell, cellId);
            Trace($"combo row {index} '{verb}': keyboard route focus={focusOk} current={current} target={target}");
            if (focusOk && current >= 0 && target >= 0)
            {
                var key = target >= current
                    ? FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN
                    : FlaUI.Core.WindowsAPI.VirtualKeyShort.UP;
                for (var step = 0; step < Math.Abs(target - current); step++)
                {
                    FlaUI.Core.Input.Keyboard.Press(key);
                    Thread.Sleep(150);
                }

                Thread.Sleep(400);
                if (string.Equals(ReadComboSelection(CellElement(grid, cellId, index)), verb, StringComparison.Ordinal))
                    return;
            }

            Thread.Sleep(400);
        }

        throw new InvalidOperationException(
            $"'{cellId}' row {index} never read back method '{verb}' (reads '{ReadComboSelection(CellElement(grid, cellId, index))}').");
    }

    private static AutomationElement CellElement(AutomationElement grid, string cellId, int index)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var cells = CellElements(grid, cellId);
            if (index >= 0 && index < cells.Count)
            {
                return cells[index];
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"grid cell '{cellId}' row {index} never realized ({cells.Count} realized in that column).");
            }

            Thread.Sleep(400);
        }
    }

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value ?? string.Empty : element.Name ?? string.Empty; }
        catch { return element.Name ?? string.Empty; }
    }

    /// <summary>Read the combo's selection. A WPF ComboBox peer exposes its dropdown items as
    /// children only once the dropdown has been opened at least time (a freshly loaded closed
    /// combo shows ZERO item peers), and the popup stays in the subtree after a collapse, so naive
    /// Text scans hit the popup's first row ("*") instead of the selection. The selection is read
    /// from the item peers' IsSelected flags; a combo whose peers are not realized yet is expanded
    /// once to realize them, read there, and collapsed again.</summary>
    private static string ReadComboSelection(AutomationElement element)
    {
        try
        {
            var direct = (element.Name ?? string.Empty).Trim();
            if (MethodOrder.Contains(direct))
                return direct;

            var selected = FirstSelected(element.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).ToList());
            if (selected != null)
                return (selected.Name ?? string.Empty).Trim();

            var probe = new List<string>();
            try
            {
                if (element.Patterns.ExpandCollapse.IsSupported)
                {
                    element.Patterns.ExpandCollapse.Pattern.Expand();
                    Thread.Sleep(400);
                    var realized = element.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).ToList();
                    foreach (var i in realized)
                    {
                        try { probe.Add($"'{i.Name}'={i.Patterns.SelectionItem.IsSupported}:{i.Patterns.SelectionItem.Pattern.IsSelected}"); }
                        catch (Exception ex) { probe.Add($"'{i.Name}'=threw:{ex.GetType().Name}"); }
                    }

                    selected = FirstSelected(realized);
                    element.Patterns.ExpandCollapse.Pattern.Collapse();
                }
            }
            catch { }

            if (selected == null)
                Trace($"combo readback: realized=[{string.Join(" ", probe)}] selected=none");
            if (selected != null)
                return (selected.Name ?? string.Empty).Trim();
            if (probe.Count == 0)
            {
                // No item peers even after expanding: fall back to the selection-box Text child.
                foreach (var text in element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                {
                    var name = (text.Name ?? string.Empty).Trim();
                    if (MethodOrder.Contains(name))
                        return name;
                }
            }

            return direct;
        }
        catch { return string.Empty; }
    }

    private static AutomationElement FirstSelected(List<AutomationElement> items) =>
        items.FirstOrDefault(i =>
        {
            try { return i.Patterns.SelectionItem.IsSupported && i.Patterns.SelectionItem.Pattern.IsSelected; }
            catch { return false; }
        });

    private static string ReadText(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}
