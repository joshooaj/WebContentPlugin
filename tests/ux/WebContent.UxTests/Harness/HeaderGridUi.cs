using System.Text;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #86: shared Management Client phases for driving and reading the Proxy tab's two
/// "Advanced: headers" grids — the request-leg grid (dgProxyRequestHeaders) and the response-leg
/// grid (dgProxyResponseHeaders). Like the allowlist grid above them, every per-row control
/// carries the SAME AutomationId (proxyHeaderRowName, proxyHeaderRowValue, proxyHeaderRowStrip,
/// proxyHeaderRowProblem, proxyHeaderRowRemove) — but this time the id appears in TWO grids on
/// the same tab, so a window-wide FindAllByAutomationId would interleave the two policies' rows.
/// Everything here is therefore scoped to the grid element first (its explicit AutomationId,
/// proxyRequestHeadersGrid / proxyResponseHeadersGrid), and within one grid rows are addressed by
/// document order like the allowlist harness does. Virtualization is off in both grids' XAML, so
/// every row stays realized and the ordering cannot drift.
///
/// Same discipline as the rest of the harness: every edit verifies by observable readback,
/// escalating the route (Value pattern first, then focus-gated clipboard paste) on failure, and
/// every "it saved" claim goes through two independent channels — the plugin's own
/// "Stored proxy header policy ..." audit line in the MC MIP.log, and a readback from a
/// RELOADED pane.
/// </summary>
public static class HeaderGridUi
{
    public const string RequestGridId = "proxyRequestHeadersGrid";
    public const string ResponseGridId = "proxyResponseHeadersGrid";
    public const string NameCellId = "proxyHeaderRowName";
    public const string ValueCellId = "proxyHeaderRowValue";
    public const string StripCellId = "proxyHeaderRowStrip";
    public const string ProblemCellId = "proxyHeaderRowProblem";
    public const string RemoveCellId = "proxyHeaderRowRemove";
    public const string AddRequestButtonId = "btnProxyHeaderAddRequest";
    public const string AddResponseButtonId = "btnProxyHeaderAddResponse";

    /// <summary>Where the grid driver's step notes go (same convention as AllowlistGridUi.Trace).</summary>
    public static Action<string> Trace { get; set; } = _ => { };

    /// <summary>One header-policy row as UIA reads it. Describe renders the stored-rule shape the
    /// plugin's audit line uses: "Name=Value" for add/override rows, "-Name" for strip rows.</summary>
    public sealed class RowReading
    {
        public int Index;
        public string Name = string.Empty;
        public string Value = string.Empty;
        public bool Strip;
        public string Problem = string.Empty;

        public string Describe() => Strip ? "-" + Name : Name + "=" + Value;
    }

    /// <summary>The wanted-row shape callers pass in: (name, value, strip).</summary>
    public sealed record Row(string Name, string Value, bool Strip)
    {
        public string Describe() => Strip ? "-" + Name : Name + "=" + Value;
    }

    private static AutomationElement GridFor(Window mcMain, string gridId) =>
        ManagementClientUi.ExpectControl(mcMain, gridId, 15);

    private static string AddButtonFor(string gridId) =>
        gridId == ResponseGridId ? AddResponseButtonId : AddRequestButtonId;

    /// <summary>The grid-scoped cell funnel. Same COMException retry as AllowlistGridUi.CellElements:
    /// a mid-edit UIA tree walk can transiently throw while the DataGrid re-realizes underneath.</summary>
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

    public static int RowCount(Window mcMain, string gridId) =>
        CellElements(GridFor(mcMain, gridId), NameCellId).Count;

    public static List<RowReading> ReadRows(Window mcMain, string gridId)
    {
        var grid = GridFor(mcMain, gridId);
        var names = CellElements(grid, NameCellId);
        var values = CellElements(grid, ValueCellId);
        var strips = CellElements(grid, StripCellId);
        var problems = CellElements(grid, ProblemCellId);

        var rows = new List<RowReading>();
        for (var i = 0; i < names.Count; i++)
        {
            rows.Add(new RowReading
            {
                Index = i,
                Name = ReadValue(names[i]),
                Value = i < values.Count ? ReadValue(values[i]) : string.Empty,
                Strip = i < strips.Count && ReadToggleState(strips[i]),
                Problem = i < problems.Count ? ReadText(problems[i]) : string.Empty,
            });
        }

        return rows;
    }

    public static string ReadProblem(Window mcMain, string gridId, int index)
    {
        var problems = CellElements(GridFor(mcMain, gridId), ProblemCellId);
        if (index < 0 || index >= problems.Count)
            throw new InvalidOperationException($"no Problem cell at row {index} of '{gridId}' ({problems.Count} realized).");
        return ReadText(problems[index]);
    }

    /// <summary>Press the grid's Add button and wait for a new realized row.</summary>
    public static void AddRow(Window mcMain, string gridId)
    {
        var before = RowCount(mcMain, gridId);
        var button = ManagementClientUi.ExpectControl(mcMain, AddButtonFor(gridId), 10);
        for (var attempt = 0; attempt < 5 && RowCount(mcMain, gridId) == before; attempt++)
        {
            try { button.Patterns.Invoke.Pattern.Invoke(); }
            catch { button.Click(); }
            Thread.Sleep(600);
        }

        if (RowCount(mcMain, gridId) != before + 1)
            throw new InvalidOperationException($"'{AddButtonFor(gridId)}' did not append a row to '{gridId}' ({before} -> {RowCount(mcMain, gridId)}).");
    }

    /// <summary>Remove one row by index, waiting for the row count to drop.</summary>
    public static void RemoveRowAt(Window mcMain, string gridId, int index)
    {
        var before = RowCount(mcMain, gridId);
        if (index < 0 || index >= before)
            throw new InvalidOperationException($"no row {index} in '{gridId}' to remove (rows={before}).");

        var grid = GridFor(mcMain, gridId);
        for (var attempt = 0; attempt < 5 && RowCount(mcMain, gridId) == before; attempt++)
        {
            var cells = CellElements(grid, RemoveCellId);
            if (index >= cells.Count) { Thread.Sleep(400); continue; }
            var button = cells[index];
            try { button.Patterns.Invoke.Pattern.Invoke(); }
            catch { button.Click(); }
            Thread.Sleep(600);
        }

        if (RowCount(mcMain, gridId) != before - 1)
            throw new InvalidOperationException($"remove on row {index} of '{gridId}' never dropped a row ({before} -> {RowCount(mcMain, gridId)}).");
    }

    public static void RemoveAllRows(Window mcMain, string gridId)
    {
        for (var guard = 0; RowCount(mcMain, gridId) > 0 && guard < 50; guard++)
        {
            RemoveRowAt(mcMain, gridId, 0);
        }
    }

    /// <summary>Replace the whole grid with the wanted rows: remove every row, add one row per
    /// entry, fill and verify row by row (order preserved — the plugin stores configuration
    /// order, and for one header name the LAST rule wins).</summary>
    public static void SetRows(Window mcMain, string gridId, IEnumerable<Row> rows)
    {
        RemoveAllRows(mcMain, gridId);
        foreach (var row in rows ?? Enumerable.Empty<Row>())
        {
            AddRow(mcMain, gridId);
            SetLastRow(mcMain, gridId, row);
        }
    }

    /// <summary>Fill the LAST row of the grid and verify it by readback.</summary>
    public static void SetLastRow(Window mcMain, string gridId, Row row)
    {
        var index = RowCount(mcMain, gridId) - 1;
        if (index < 0) throw new InvalidOperationException($"no row to set in '{gridId}'.");

        var grid = GridFor(mcMain, gridId);
        SetTextCell(mcMain, grid, NameCellId, index, row.Name);
        SetTextCell(mcMain, grid, ValueCellId, index, row.Strip ? string.Empty : row.Value ?? string.Empty);
        SetToggleCell(mcMain, grid, StripCellId, index, row.Strip);

        var read = ReadRows(mcMain, gridId)[index];
        Trace($"'{gridId}' row {index}: '{row.Name}' (strip={row.Strip}) reads Name='{read.Name}' Value='{read.Value}' Strip={read.Strip} Problem='{read.Problem}'");
        if (!string.Equals(read.Name, row.Name, StringComparison.Ordinal)
            || !string.Equals(read.Strip ? string.Empty : read.Value, row.Strip ? string.Empty : row.Value ?? string.Empty, StringComparison.Ordinal)
            || read.Strip != row.Strip)
        {
            throw new InvalidOperationException(
                $"'{gridId}' row {index} never read back as '{row.Describe()}' (reads '{read.Describe()}').");
        }
    }

    /// <summary>
    /// The header-policy save discipline, mirroring AllowlistGridUi.SaveAndVerify: dirty-chain
    /// guards, the pre-Save Name check, Save with modal sweep, then the stored policy read back
    /// from TWO independent channels — the plugin's own "Stored proxy header policy for workspace
    /// '...' : request N rule(s) [...]; response M rule(s) [...]" audit line in the MC MIP.log,
    /// and both grids re-read from a RELOADED pane, ORDER-SENSITIVELY (configuration order is
    /// part of the policy: last rule for one name wins, here and on the Event Server).
    /// </summary>
    public static void SavePoliciesAndVerify(
        ClientSession mc,
        Window mcMain,
        string workspacesNodeName,
        string workspaceName,
        IEnumerable<Row> expectedRequest,
        IEnumerable<Row> expectedResponse,
        StringBuilder log)
    {
        Trace = message => log.AppendLine(message);
        var request = (expectedRequest ?? Enumerable.Empty<Row>()).ToList();
        var response = (expectedResponse ?? Enumerable.Empty<Row>()).ToList();

        if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
        {
            log.AppendLine("Save still disabled after the header-grid edit; flipping the routing checkbox twice to dirty the pane");
            ManagementClientUi.DirtyPaneViaCheckbox(mcMain, AllowlistGridUi.ProxyEnabledId);
            if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
            {
                log.AppendLine("Save still disabled after the checkbox flip; keyboard-nudging the first request-grid name cell");
                try { ManagementClientUi.NudgeControlWithKeystroke(mcMain, NameCellId); }
                catch (Exception ex) { log.AppendLine($"nudge skipped: {ex.GetType().Name}"); }
                log.AppendLine($"Save enabled after nudge: {ManagementClientUi.WaitForSaveEnabled(mcMain, 6)}");
            }
        }

        var paneName = AllowlistGridUi.ReadPaneName(mcMain);
        if (!string.Equals(paneName, workspaceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to Save: the Name field reads '{paneName}', expected '{workspaceName}'.");

        AllowlistGridUi.OpenProxyTab(mcMain);

        // The audit line this save writes, keyed on this call's exact leg counts AND exact rule
        // renderings, so an older save of a different policy cannot satisfy the delta.
        var auditMarker = $"Stored proxy header policy for workspace '{workspaceName}'";
        var tokens = AuditTokens(request, response);
        var auditBefore = McLog.CountMatches(auditMarker, tokens);

        if (!ManagementClientUi.Save(mc, mcMain, 20))
            throw new InvalidOperationException(
                $"Save never became enabled for policy request=[{string.Join("; ", request.Select(r => r.Describe()))}] response=[{string.Join("; ", response.Select(r => r.Describe()))}].");

        var auditLine = McLog.WaitForNewLine(auditBefore, 20, auditMarker, tokens);
        if (auditLine == null)
            throw new InvalidOperationException(
                $"the save wrote no 'Stored proxy header policy' audit line carrying [{string.Join(" | ", tokens)}] " +
                $"({auditBefore} -> {McLog.CountMatches(auditMarker, tokens)}). " +
                "The save either never ran or stored something other than the grids showed.");
        log.AppendLine($"stored header policy audit line: {auditLine}");

        Thread.Sleep(1500);
        AllowlistGridUi.ReloadPane(mcMain, workspacesNodeName, workspaceName);
        AllowlistGridUi.OpenProxyTab(mcMain);
        var storedRequest = ReadRows(mcMain, RequestGridId);
        var storedResponse = ReadRows(mcMain, ResponseGridId);
        if (!SameRows(request, storedRequest))
            throw new InvalidOperationException(
                $"After Save + reload the request grid reads '{string.Join(" | ", storedRequest.Select(r => r.Describe()))}' — expected '{string.Join(" | ", request.Select(r => r.Describe()))}'.");
        if (!SameRows(response, storedResponse))
            throw new InvalidOperationException(
                $"After Save + reload the response grid reads '{string.Join(" | ", storedResponse.Select(r => r.Describe()))}' — expected '{string.Join(" | ", response.Select(r => r.Describe()))}'.");
        log.AppendLine(
            $"saved header policy request=[{string.Join("; ", request.Select(r => r.Describe()))}] response=[{string.Join("; ", response.Select(r => r.Describe()))}] (verified from the audit line AND reloaded panes, order-sensitive)");
    }

    /// <summary>The audit line fragments for one wanted policy: each leg's count AND its exact
    /// rendering in brackets. ProxyHeaderEditor.DescribeStoredRules renders add rows as
    /// "Name=Value" and strip rows as "-Name", joined by "; " — mirrored exactly here.</summary>
    private static string[] AuditTokens(IEnumerable<Row> request, IEnumerable<Row> response)
    {
        var requestRules = request.ToList();
        var responseRules = response.ToList();
        var reqDesc = string.Join("; ", requestRules.Select(r => r.Describe()));
        var rspDesc = string.Join("; ", responseRules.Select(r => r.Describe()));
        return new[]
        {
            $"request {requestRules.Count} rule(s)",
            $"[{reqDesc}]",
            $"response {responseRules.Count} rule(s)",
            $"[{rspDesc}]",
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
    //      allowlist grid, but every lookup is scoped to this caller's grid) ------------------

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

            // Keyboard route, focus-gated: the id is shared by BOTH grids' rows, so the focus
            // gate only proves SOMETHING with this id holds focus — the row readback below is
            // what proves the write landed in THIS grid's row.
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

    private static void SetToggleCell(Window mcMain, AutomationElement grid, string cellId, int index, bool on)
    {
        var desired = on ? ToggleState.On : ToggleState.Off;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var cell = CellElement(grid, cellId, index);
            if (!cell.Patterns.Toggle.IsSupported) { cell.Click(); Thread.Sleep(500); continue; }
            if (cell.Patterns.Toggle.Pattern.ToggleState.Value == desired) return;
            try { cell.Patterns.Toggle.Pattern.Toggle(); }
            catch { cell.Click(); }
            Thread.Sleep(500);
        }

        var state = CellElement(grid, cellId, index).Patterns.Toggle.Pattern.ToggleState.Value;
        if (state != desired)
            throw new InvalidOperationException($"toggle cell '{cellId}' row {index} never reached {(on ? "On" : "Off")} (state {state}).");
    }

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value ?? string.Empty : element.Name ?? string.Empty; }
        catch { return element.Name ?? string.Empty; }
    }

    private static string ReadText(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool ReadToggleState(AutomationElement element)
    {
        try { return element.Patterns.Toggle.IsSupported && element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On; }
        catch { return false; }
    }
}
