using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #81: shared Management Client phase for driving and reading the structured Proxy-tab
/// allowlist grid (dgProxyTargets). The free-text txtProxyTargets box is gone, so every probe
/// that provisioned or read back an allowlist now goes through rows: scheme dropdown, host edit,
/// port edit, accept-untrusted checkbox, Problem cell, and a per-row Remove button.
///
/// The per-row controls all carry the SAME AutomationId (proxyRowHost, proxyRowPort, ...) - one
/// per grid row - so rows are addressed by DOCUMENT ORDER: the i-th match of any per-row id is
/// the same row across ids (document order is display order, corroborated by ascending
/// BoundingRectangle.Y). Virtualization is off in the grid's XAML (pinned by the shell guard)
/// so every row stays realized and the ordering can never drift.
///
/// Every edit verifies by observable change (read the row back), escalating the route on each
/// failed attempt - the harness rules learned in #52/#56/#74 still apply: never type into an
/// unfocused control, and never trust a pattern invoke that can silently no-op.
/// </summary>
public static class AllowlistGridUi
{
    public const string GridId = "dgProxyTargets";
    public const string SchemeCellId = "proxyRowScheme";
    public const string HostCellId = "proxyRowHost";
    public const string PortCellId = "proxyRowPort";
    public const string UntrustedCellId = "proxyRowUntrusted";
    public const string ProblemCellId = "proxyRowProblem";
    public const string RemoveCellId = "proxyRowRemove";
    public const string AddButtonId = "btnProxyTargetAdd";
    public const string WarningBannerId = "proxyWildcardWarning";

    /// <summary>The banner Border's inner TextBlock. A plain WPF Border has no UIA peer, so the
    /// Border itself is never in the tree - but a collapsed subtree removes its TextBlock too,
    /// which makes the TextBlock's presence/visibility the honest UIA probe for banner state.</summary>
    public const string WarningBannerTextId = "txtProxyWildcardWarning";
    public const string TargetsHintId = "lblProxyTargetsHint";
    public const string ProxyTabId = "tabProxy";
    public const string SettingsTabId = "tabSettings";
    public const string NameFieldId = "txtName";
    public const string ProxyEnabledId = "chkProxyEnabled";

    /// <summary>One row as UIA reads it. Entry renders in the pre-#81 probe-record shape
    /// ("scheme://host[:port] accept-untrusted") so existing assertions transfer.</summary>
    public sealed class RowReading
    {
        public int Index;
        public string Scheme = string.Empty;
        public string Host = string.Empty;
        public string Port = string.Empty;
        public bool Untrusted;
        public string Problem = string.Empty;

        public string Entry
        {
            get
            {
                var origin = $"{Scheme}://{Host}{(string.IsNullOrWhiteSpace(Port) ? string.Empty : ":" + Port.Trim())}";
                return Untrusted ? origin + " accept-untrusted" : origin;
            }
        }
    }

    public static void OpenProxyTab(Window mcMain)
    {
        SelectTab(mcMain, ProxyTabId);

        // The tab is only "open" once its content is realized.
        ManagementClientUi.ExpectControl(mcMain, GridId, 15);
    }

    /// <summary>Selects the Settings tab (Name / home address live there) and waits for its
    /// first control to realize. WPF unloads a non-selected tab's content, so every harness
    /// phase that reads or writes a control has to be on the tab that owns it.</summary>
    public static void OpenSettingsTab(Window mcMain)
    {
        SelectTab(mcMain, SettingsTabId);
        ManagementClientUi.ExpectControl(mcMain, NameFieldId, 15);
    }

    private static void SelectTab(Window mcMain, string tabId)
    {
        var tab = ManagementClientUi.ExpectControl(mcMain, tabId, 15);
        try
        {
            if (tab.Patterns.SelectionItem.IsSupported)
            {
                tab.Patterns.SelectionItem.Pattern.Select();
                Thread.Sleep(1200);
                return;
            }
        }
        catch { }

        tab.Click();
        Thread.Sleep(1200);
    }

    /// <summary>The Settings tab's Name field - the guard every Save phase re-checks before
    /// letting the shell write (one bad run saved a corrupted Name and renamed the item).</summary>
    public static string ReadPaneName(Window mcMain)
    {
        OpenSettingsTab(mcMain);
        return ReadValue(ManagementClientUi.ExpectControl(mcMain, NameFieldId, 10));
    }

    /// <summary>
    /// Re-selects the workspace item so the detail pane is rebuilt FROM THE STORED ITEM. Every
    /// "it saved" claim in these probes goes through this: a readback of the pane the harness
    /// just edited proves nothing, a readback after a reload proves the record round-tripped.
    /// </summary>
    public static void ReloadPane(Window mcMain, string workspacesNodeName, string itemName)
    {
        ManagementClientUi.SelectNode(mcMain, workspacesNodeName);
        Thread.Sleep(900);
        ManagementClientUi.SelectTreeItem(mcMain, itemName);
        Thread.Sleep(1800);
    }

    /// <summary>Where the grid driver's step notes go (which selection route ran, what the
    /// dropdown reported). Probes point this at their run log so the route history is on disk.</summary>
    public static Action<string> Trace { get; set; } = _ => { };

    /// <summary>
    /// The full allowlist save discipline, grid edition: routing toggle on, rows replaced with
    /// the wanted entries, dirty-chain guards, the pre-Save Name check, Save with modal sweep,
    /// then a readback from a RELOADED pane. Throws if any step cannot be verified by observable
    /// change. This is what every proxy probe's "make the workspace allowlist look like X" phase
    /// uses, so the grid's edit quirks are handled in exactly one place.
    /// </summary>
    public static void SetEntriesAndSave(ClientSession mc, Window mcMain, string workspacesNodeName, string workspaceName, IEnumerable<string> entries, StringBuilder log)
    {
        Trace = message => log.AppendLine(message);
        var wanted = (entries ?? Enumerable.Empty<string>()).ToList();

        OpenProxyTab(mcMain);
        ManagementClientUi.EnsureCheckBox(mcMain, ProxyEnabledId, true);
        SetEntries(mcMain, wanted);
        log.AppendLine($"grid now shows: {string.Join(" | ", ReadEntries(mcMain))}");

        SaveAndVerify(mc, mcMain, workspacesNodeName, workspaceName, wanted, log);
    }

    /// <summary>Save discipline for a grid the caller has already edited: dirty-chain guards,
    /// the pre-Save Name check, Save with modal sweep, then the stored allowlist read back from
    /// TWO independent channels - the plugin's own storage audit line in the MC MIP.log, and a
    /// RELOADED pane. Either channel alone can lie: the grid's dropdowns only echo their own
    /// selection through UIA, and the log line is the only view of what the save wrote.</summary>
    public static void SaveAndVerify(ClientSession mc, Window mcMain, string workspacesNodeName, string workspaceName, IEnumerable<string> expected, StringBuilder log)
    {
        Trace = message => log.AppendLine(message);
        var wanted = (expected ?? Enumerable.Empty<string>()).ToList();

        if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
        {
            log.AppendLine("Save still disabled after the grid edit; flipping the routing checkbox twice to dirty the pane");
            ManagementClientUi.DirtyPaneViaCheckbox(mcMain, ProxyEnabledId);
            if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
            {
                log.AppendLine("Save still disabled after the checkbox flip; keyboard-nudging a host cell");
                ManagementClientUi.NudgeControlWithKeystroke(mcMain, HostCellId);
                log.AppendLine($"Save enabled after nudge: {ManagementClientUi.WaitForSaveEnabled(mcMain, 6)}");
            }
        }

        var paneName = ReadPaneName(mcMain);
        if (!string.Equals(paneName, workspaceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to Save: the Name field reads '{paneName}', expected '{workspaceName}'.");

        OpenProxyTab(mcMain);

        // The audit line the save writes, keyed on this call's exact entry set, so an older save
        // of the same allowlist cannot satisfy the delta.
        var auditMarker = $"Stored proxy targets for workspace '{workspaceName}'";
        var origins = wanted.Select(WantedOrigin).ToArray();
        var auditBefore = McLog.CountMatches(auditMarker, origins);

        if (!ManagementClientUi.Save(mc, mcMain, 20))
            throw new InvalidOperationException($"Save never became enabled for allowlist '{string.Join(" | ", wanted)}'.");

        var auditLine = McLog.WaitForNewLine(auditBefore, 20, auditMarker, origins);
        if (auditLine == null)
            throw new InvalidOperationException(
                $"the save wrote no storage audit line naming every wanted entry ({auditBefore} -> " +
                $"{McLog.CountMatches(auditMarker, origins)} for '{string.Join(", ", origins)}'). " +
                "The save either never ran or stored something other than the grid showed.");
        log.AppendLine($"stored proxy targets (MC MIP.log): {AuditTail(auditLine)}");

        var stored = EntriesInAuditLine(auditLine);
        if (!SameOrigins(wanted, stored))
            throw new InvalidOperationException(
                $"the plugin stored '{string.Join(" | ", stored)}' - expected '{string.Join(" | ", wanted)}'.");

        // The flag is part of the stored record, and the audit line is the one place it is stated
        // independently of the checkbox's own UIA state.
        foreach (var entry in wanted.Where(e => e.IndexOf("accept-untrusted", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            var flagged = $"{WantedOrigin(entry)} accept-untrusted";
            if (!auditLine.Contains(flagged, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{entry}' was saved without its accept-untrusted flag: the audit line says '{AuditTail(auditLine)}'.");
        }

        Thread.Sleep(1500);
        ReloadPane(mcMain, workspacesNodeName, workspaceName);
        OpenProxyTab(mcMain);
        var after = ReadEntries(mcMain);
        if (!SameOrigins(wanted, after))
            throw new InvalidOperationException(
                $"After Save + reload the allowlist reads '{string.Join(" | ", after)}' — expected '{string.Join(" | ", wanted)}'.");
        log.AppendLine($"saved allowlist '{string.Join(" | ", wanted)}' (verified from the audit line AND a reloaded pane)");
    }

    /// <summary>The origin part of a probe entry ("scheme://host[:port]", flag token stripped).</summary>
    public static string WantedOrigin(string entry)
    {
        ParseEntry(entry, out var scheme, out var host, out var port, out _);
        return $"{scheme}://{host}" + (string.IsNullOrWhiteSpace(port) ? string.Empty : $":{port}");
    }

    /// <summary>The entry list out of the plugin's "Stored proxy targets ..." log line.</summary>
    private static List<string> EntriesInAuditLine(string line)
    {
        var tail = AuditTail(line);
        return tail.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(e => e.Trim())
                   .Where(e => e.Length > 0)
                   .ToList();
    }

    private static string AuditTail(string line)
    {
        var cut = line.IndexOf("routing ", StringComparison.Ordinal);
        if (cut < 0) return line;
        var rest = line.Substring(cut);
        var semi = rest.IndexOf(';');
        var tail = semi < 0 ? rest : rest.Substring(semi + 1).Trim();
        // The audit line is a sentence: it ends in '.' after the last entry, not part of any entry.
        return tail.EndsWith(".", StringComparison.Ordinal) ? tail.Substring(0, tail.Length - 1).Trim() : tail;
    }

    /// <summary>Order-insensitive origin comparison: the save deduplicates and canonicalizes, so
    /// the stored set is what matters, not the row order the harness happened to type.</summary>
    public static bool SameOrigins(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Origins(expected).OrderBy(o => o, StringComparer.Ordinal)
            .SequenceEqual(Origins(actual).OrderBy(o => o, StringComparer.Ordinal));

    private static string[] Origins(IEnumerable<string> entries) =>
        (entries ?? Enumerable.Empty<string>())
            .Select(e => (e ?? string.Empty).Trim())
            .Where(e => e.Length > 0)
            .Select(WantedOrigin)
            .ToArray();

    /// <summary>
    /// Clicks Save and captures the shell notice the plugin raises when a save is REFUSED (a
    /// WinForms MessageBox the plugin's own save path shows), dismissing it on the way out. The
    /// shell's own "Save changes?" confirmation is accepted first, because the plugin's refusal
    /// only runs after it. Returns the captured notice text, or null when no such notice appeared
    /// (i.e. the save went through). Invoke runs on a side thread: a UIA Invoke blocks for the
    /// whole lifetime of the modal it caused, which is how the #56 row hung.
    ///
    /// The notice is read through WIN32, not UIA. Live on #81: the refusal dialog was demonstrably
    /// up (the Save Invoke stayed blocked on it, and the MIP.log carried the matching rejection
    /// line), yet the app's UIA window list reported nothing but the main window - so a UIA-only
    /// capture cannot see a plugin-raised MessageBox at all. EnumWindows + GetWindowText + BM_CLICK
    /// sees it and answers it.
    /// </summary>
    public static string SaveAndCaptureNotice(ClientSession mc, Window mcMain, string noticeFragment, StringBuilder log, int timeoutSeconds = 30)
    {
        var saveButton = mcMain.FindAllDescendants(cf => cf.ByName("Save"))
            .FirstOrDefault(b => b.ControlType == ControlType.Button && b.IsEnabled);
        if (saveButton == null)
            throw new InvalidOperationException("no enabled Save button to press - the grid edit never dirtied the pane.");

        var mainHandle = IntPtr.Zero;
        try { mainHandle = mcMain.Properties.NativeWindowHandle.Value; } catch { }

        System.Threading.Tasks.Task invoke;
        try
        {
            invoke = System.Threading.Tasks.Task.Run(() => saveButton.Patterns.Invoke.Pattern.Invoke());
        }
        catch (Exception ex)
        {
            log.AppendLine($"Save invoke threw on the way in: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        var captured = (string)null;
        var fallback = (string)null;
        var seen = new HashSet<string>();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var pid = mc.Process.Id;

        while (DateTime.UtcNow < deadline && captured == null)
        {
            // ---- Win32 route: the plugin's own notice dialogs --------------------------------
            foreach (var hwnd in TopLevelWindows(pid))
            {
                try
                {
                    if (hwnd == mainHandle) continue;

                    var cls = WindowClass(hwnd);
                    var title = WindowTitle(hwnd);
                    var body = WindowBodyText(hwnd);
                    var text = $"{title} {body}";

                    // One diagnostic line per distinct window shape: which dialogs the shell and
                    // the plugin actually put on the desktop and what each of them says. Without
                    // this a missed notice is indistinguishable from a notice that never showed.
                    if (seen.Add($"w:{cls}|{title}"))
                        log.AppendLine($"window: class='{cls}' title='{Collapse(title)}' text='{Collapse(body)}'");

                    if (text.Contains(noticeFragment, StringComparison.OrdinalIgnoreCase))
                    {
                        captured = Collapse(text);
                        if (!ClickNativeButton(hwnd, "OK")) ClickNativeButton(hwnd, "Close");
                        break;
                    }

                    // A notice dialog whose wording could not be matched: keep it as the fallback
                    // answer to the assertion and dismiss it so the blocked UI thread can continue.
                    if (cls.Contains("#32770"))
                    {
                        if (!string.IsNullOrWhiteSpace(Collapse(text))) fallback = fallback ?? Collapse(text);
                        if (!ClickNativeButton(hwnd, "OK")) ClickNativeButton(hwnd, "Close");
                    }
                }
                catch { }
            }

            if (captured != null) break;

            // ---- UIA route: the shell's own confirmation, which UIA does report ---------------
            foreach (var window in mc.Windows())
            {
                try
                {
                    if (string.Equals(window.AutomationId ?? string.Empty, "_mainApplicationWindow", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).ToList();
                    if (buttons.Count == 0) continue;

                    var cls = SafeText(() => window.ClassName);
                    var title = SafeText(() => window.Name);
                    if (cls.Contains("#32770")) continue; // already handled by the Win32 route

                    if (seen.Add($"u:{cls}|{title}"))
                        log.AppendLine($"uia window: class='{cls}' title='{title}' buttons=[{string.Join(",", buttons.Select(b => SafeText(() => b.Name)))}]");

                    var text = NoticeText(window);
                    if (text.Contains(noticeFragment, StringComparison.OrdinalIgnoreCase))
                    {
                        captured = Collapse(text);
                        if (!ClickButton(buttons, "OK")) ClickButton(buttons, "Close");
                        break;
                    }

                    // The shell's save confirmation: answer it so the plugin's save path runs.
                    if (!ClickButton(buttons, "Yes"))
                    {
                        if (!ClickButton(buttons, "Save")) ClickButton(buttons, "OK");
                    }
                }
                catch { }
            }

            if (captured == null) Thread.Sleep(400);
        }

        if (seen.Count == 0)
            log.AppendLine($"no window other than the main one appeared within {timeoutSeconds}s of the Save press");

        try { invoke.Wait(TimeSpan.FromSeconds(20)); } catch { }
        ManagementClientUi.AcceptPendingModal(mc, 3);
        return captured ?? fallback;
    }

    /// <summary>Everything a notice window says: title plus every text-ish descendant, plus the
    /// plain Win32 window text of the dialog and its children. The Win32 route is not decoration:
    /// a MessageBox raised while the host's UI thread sits in its own modal loop can hand UIA an
    /// empty element tree, and the HWND text is the only thing still readable (learned live on
    /// #81, where the plugin's refusal dialog was on screen but UIA reported nothing).</summary>
    private static string NoticeText(Window window)
    {
        var parts = new List<string>();
        try { parts.Add(window.Name ?? string.Empty); } catch { }
        try
        {
            foreach (var element in window.FindAllDescendants(cf =>
                         cf.ByControlType(ControlType.Text)
                             .Or(cf.ByControlType(ControlType.Edit))
                             .Or(cf.ByControlType(ControlType.Document))))
            {
                try { parts.Add(element.Name ?? string.Empty); } catch { }
            }
        }
        catch { }

        try
        {
            var hwnd = IntPtr.Zero;
            try { hwnd = window.Properties.NativeWindowHandle.Value; } catch { }
            if (hwnd != IntPtr.Zero) parts.Add(WindowBodyText(hwnd));
        }
        catch { }

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    // ---- Win32 dialog plumbing: the only route that sees a plugin-raised MessageBox ----------

    private static List<IntPtr> TopLevelWindows(int processId)
    {
        var found = new List<IntPtr>();
        _ = EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string WindowClass(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        _ = GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(1024);
        _ = GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    /// <summary>The dialog's own text plus every child control's text - a #32770 notice keeps its
    /// message in a static child and its answers in Button children.</summary>
    private static string WindowBodyText(IntPtr hwnd)
    {
        var parts = new List<string>();
        _ = EnumChildWindows(hwnd, (child, _) =>
        {
            var text = WindowTitle(child);
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
            return true;
        }, IntPtr.Zero);
        return string.Join(" ", parts);
    }

    private static bool ClickNativeButton(IntPtr parent, string name)
    {
        var hit = IntPtr.Zero;
        _ = EnumChildWindows(parent, (child, _) =>
        {
            if (hit != IntPtr.Zero) return true;
            if (WindowClass(child) != "Button") return true;
            if (!string.Equals(WindowTitle(child), name, StringComparison.OrdinalIgnoreCase)) return true;
            hit = child;
            return false;
        }, IntPtr.Zero);

        if (hit == IntPtr.Zero) return false;
        _ = PostMessage(hit, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private const uint BM_CLICK = 0x00F5;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static string Collapse(string text) =>
        text == null ? string.Empty : System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static string SafeText(Func<string> read)
    {
        try { return (read() ?? string.Empty).Trim(); } catch { return string.Empty; }
    }

    private static bool ClickButton(System.Collections.Generic.IEnumerable<AutomationElement> buttons, string name)
    {
        var hit = buttons.FirstOrDefault(b => string.Equals((b.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return false;
        try { hit.Patterns.Invoke.Pattern.Invoke(); } catch { try { hit.Click(); } catch { } }
        return true;
    }

    public static bool SameEntries(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Norm(expected).SequenceEqual(Norm(actual));

    private static string[] Norm(IEnumerable<string> entries) =>
        (entries ?? Enumerable.Empty<string>()).Select(e => (e ?? string.Empty).Trim()).Where(e => e.Length > 0).ToArray();


    public static List<RowReading> ReadRows(Window mcMain)
    {
        var hosts = CellElements(mcMain, HostCellId);
        var schemes = CellElements(mcMain, SchemeCellId);
        var ports = CellElements(mcMain, PortCellId);
        var untrusted = CellElements(mcMain, UntrustedCellId);
        var problems = CellElements(mcMain, ProblemCellId);

        var rows = new List<RowReading>();
        for (var i = 0; i < hosts.Count; i++)
        {
            rows.Add(new RowReading
            {
                Index = i,
                Scheme = i < schemes.Count ? ReadComboValue(schemes[i]) : string.Empty,
                Host = ReadValue(hosts[i]),
                Port = i < ports.Count ? ReadValue(ports[i]) : string.Empty,
                Untrusted = i < untrusted.Count && ReadToggleState(untrusted[i]),
                Problem = i < problems.Count ? ReadText(problems[i]) : string.Empty,
            });
        }

        return rows;
    }

    public static List<string> ReadEntries(Window mcMain) => ReadRows(mcMain).Select(r => r.Entry).ToList();

    public static int RowCount(Window mcMain) => CellElements(mcMain, HostCellId).Count;

    /// <summary>Press the grid's "Add target" button and wait for a new realized row.</summary>
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
            throw new InvalidOperationException($"'{AddButtonId}' did not append a grid row ({before} -> {RowCount(mcMain)}).");
    }

    /// <summary>Fill the LAST grid row (the one AddRow just appended) cell by cell, verifying
    /// each cell by readback. scheme "http"/"https"; port may be null/empty for the default.</summary>
    public static void SetLastRow(Window mcMain, string scheme, string host, string port, bool acceptUntrusted)
    {
        var index = RowCount(mcMain) - 1;
        if (index < 0) throw new InvalidOperationException("no grid row to set.");

        SetSchemeCell(mcMain, index, scheme);
        SetTextCell(mcMain, HostCellId, index, host);
        SetTextCell(mcMain, PortCellId, index, port ?? string.Empty);
        SetToggleCell(mcMain, UntrustedCellId, index, acceptUntrusted);

        var row = ReadRows(mcMain)[index];
        Trace($"grid row {index} typed as {scheme}://{host}{(string.IsNullOrWhiteSpace(port) ? string.Empty : ":" + port)} and reads '{row.Entry}' (Problem: '{row.Problem}')");
        if (!string.Equals(row.Host, host, StringComparison.Ordinal) ||
            !string.Equals(row.Port?.Trim() ?? string.Empty, (port ?? string.Empty).Trim(), StringComparison.Ordinal) ||
            !string.Equals(row.Scheme, scheme, StringComparison.OrdinalIgnoreCase) ||
            row.Untrusted != acceptUntrusted)
        {
            throw new InvalidOperationException(
                $"grid row {index} never read back as {scheme}://{host}{(string.IsNullOrWhiteSpace(port) ? string.Empty : ":" + port)}{(acceptUntrusted ? " accept-untrusted" : string.Empty)} (reads '{row.Entry}').");
        }
    }

    /// <summary>Add one entry as a fresh row: button, then fill the new last row.</summary>
    public static void AddEntry(Window mcMain, string scheme, string host, string port, bool acceptUntrusted)
    {
        AddRow(mcMain);
        SetLastRow(mcMain, scheme, host, port, acceptUntrusted);
    }

    /// <summary>Remove one row by index, waiting for the row count to drop.</summary>
    public static void RemoveRowAt(Window mcMain, int index)
    {
        var before = RowCount(mcMain);
        if (index < 0 || index >= before)
            throw new InvalidOperationException($"no grid row {index} to remove (rows={before}).");

        for (var attempt = 0; attempt < 5 && RowCount(mcMain) == before; attempt++)
        {
            var button = CellElement(mcMain, RemoveCellId, index);
            try { button.Patterns.Invoke.Pattern.Invoke(); }
            catch { button.Click(); }
            Thread.Sleep(600);
        }

        if (RowCount(mcMain) != before - 1)
            throw new InvalidOperationException($"remove on grid row {index} never dropped a row ({before} -> {RowCount(mcMain)}).");
    }

    public static void RemoveAllRows(Window mcMain)
    {
        for (var guard = 0; RowCount(mcMain) > 0 && guard < 50; guard++)
        {
            RemoveRowAt(mcMain, 0);
        }
    }

    /// <summary>Replace the whole grid with the given entries (probe-record shape "scheme://host[:port]
    /// [accept-untrusted]"): remove every row, add one row per entry, verified row by row.</summary>
    public static void SetEntries(Window mcMain, IEnumerable<string> entries)
    {
        RemoveAllRows(mcMain);
        foreach (var entry in entries)
        {
            ParseEntry(entry, out var scheme, out var host, out var port, out var untrusted);
            AddEntry(mcMain, scheme, host, port, untrusted);
        }
    }

    public static void ParseEntry(string entry, out string scheme, out string host, out string port, out bool untrusted)
    {
        untrusted = false;
        var tokens = (entry ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var origin = tokens.FirstOrDefault(t => t.Contains("://")) ?? string.Empty;
        untrusted = tokens.Any(t => string.Equals(t, "accept-untrusted", StringComparison.OrdinalIgnoreCase));

        var rest = origin.Substring(origin.IndexOf("://", StringComparison.Ordinal) + 3);
        scheme = origin.Substring(0, origin.IndexOf("://", StringComparison.Ordinal)).ToLowerInvariant();
        var lastColon = rest.LastIndexOf(':');
        if (lastColon > 0 && rest.Substring(lastColon + 1).All(char.IsDigit))
        {
            host = rest.Substring(0, lastColon);
            port = rest.Substring(lastColon + 1);
        }
        else
        {
            host = rest;
            port = string.Empty;
        }
    }

    /// <summary>True while the full-host-wildcard warning banner is on screen.</summary>
    public static bool WarningBannerVisible(Window mcMain)
    {
        var banner = mcMain.FindFirstDescendant(cf => cf.ByAutomationId(WarningBannerTextId));
        if (banner == null) return false;
        try { return !banner.IsOffscreen; }
        catch { return false; }
    }

    // ---- cell primitives: read/verify, escalate the route on failure -------------------------

    private static void SetTextCell(Window mcMain, string automationId, int index, string value)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var cell = CellElement(mcMain, automationId, index);
            try
            {
                if (cell.Patterns.Value.IsSupported)
                    cell.Patterns.Value.Pattern.SetValue(value);
            }
            catch { }

            Thread.Sleep(400);
            if (string.Equals(ReadValue(CellElement(mcMain, automationId, index)), value, StringComparison.Ordinal))
                return;

            // Keyboard route, focus-gated like the harness rules demand: verified focus on THIS
            // cell (its id matches every row's, so the focus gate must also re-verify by the
            // cell handle), select-all, clipboard paste.
            cell = CellElement(mcMain, automationId, index);
            if (ManagementClientUi.FocusVerified(mcMain, cell, automationId) && LoginFlow.TryClipboardSetText(value))
            {
                FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                                                             FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
                Thread.Sleep(150);
                FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                                                             FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_V);
                Thread.Sleep(400);
                if (string.Equals(ReadValue(CellElement(mcMain, automationId, index)), value, StringComparison.Ordinal))
                    return;
            }

            Thread.Sleep(400);
        }

        throw new InvalidOperationException(
            $"grid cell '{automationId}' row {index} never read back as '{value}' (reads '{ReadValue(CellElement(mcMain, automationId, index))}').");
    }

    private static void SetSchemeCell(Window mcMain, int index, string scheme)
    {
        // Three routes, always run in the order an admin would take them. Whether the row model
        // actually followed is NOT decided here: the dropdown's own IsSelected echo has already
        // been caught claiming a scheme the row never stored (live on #81: every row driven
        // through the UIA selection routes displayed http and stored https). The authority is what
        // the save writes - the refusal dialog's row text and the "Stored proxy targets ..." audit
        // line in the MC MIP.log.
        var combo = ResolveCombo(CellElement(mcMain, SchemeCellId, index));
        SelectSchemeByClick(combo, scheme);
        SelectSchemeByKeyboard(mcMain, combo, index, scheme);
        if (!string.Equals(ReadComboValue(ResolveCombo(CellElement(mcMain, SchemeCellId, index))), scheme, StringComparison.OrdinalIgnoreCase))
            SelectSchemeByPattern(ResolveCombo(CellElement(mcMain, SchemeCellId, index)), scheme);

        var final = ReadComboValue(ResolveCombo(CellElement(mcMain, SchemeCellId, index)));
        if (!string.Equals(final, scheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"scheme combo row {index} never read back as '{scheme}' (reads '{final}').");
    }

    private static void SelectSchemeByClick(AutomationElement combo, string scheme)
    {
        var expanded = false;
        try { combo.Patterns.ExpandCollapse.Pattern.Expand(); expanded = true; }
        catch { try { combo.Click(); expanded = true; } catch { } }
        Thread.Sleep(450);

        var items = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
        Trace($"scheme row: click route (Expand threw: {!expanded}), popup items=[{string.Join(",", items.Select(i => SafeText(() => i.Name)))}]");

        var item = items.FirstOrDefault(i => string.Equals((i.Name ?? string.Empty).Trim(), scheme, StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            try
            {
                item.Click();
                Trace($"scheme row: clicked the '{scheme}' item at {item.BoundingRectangle}");
            }
            catch (Exception ex)
            {
                Trace($"scheme row: item click threw {ex.GetType().Name}, falling back to SelectionItem");
                try { item.Patterns.SelectionItem.Pattern.Select(); } catch { }
            }
        }

        Thread.Sleep(400);
        try { combo.Patterns.ExpandCollapse.Pattern.Collapse(); } catch { }
        Thread.Sleep(250);
    }

    /// <summary>Open the dropdown with a real mouse click and TYPE the scheme: the route a
    /// keyboard-driven admin takes, committing through the same input path the binding watches
    /// rather than through an accessibility pattern. Typing only starts once the dropdown is
    /// demonstrably open, so keystrokes can never land in another cell.</summary>
    private static void SelectSchemeByKeyboard(Window mcMain, AutomationElement combo, int index, string scheme)
    {
        try { combo.Click(); }
        catch { try { combo.Patterns.ExpandCollapse.Pattern.Expand(); } catch { } }
        Thread.Sleep(500);

        var open = IsComboOpen(combo);
        if (!open)
        {
            try { combo.Patterns.ExpandCollapse.Pattern.Expand(); Thread.Sleep(400); open = IsComboOpen(combo); }
            catch { }
        }

        if (!open)
        {
            Trace($"scheme row {index}: no dropdown open before typing - keyboard route skipped");
            return;
        }

        FlaUI.Core.Input.Keyboard.Type(scheme);
        Thread.Sleep(350);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        FlaUI.Core.Input.Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        Trace($"scheme row {index}: typed '{scheme}' + Enter into an open dropdown");
        Thread.Sleep(400);
        try { combo.Patterns.ExpandCollapse.Pattern.Collapse(); } catch { }
        Thread.Sleep(250);
    }

    private static bool IsComboOpen(AutomationElement combo)
    {
        try { return combo.Patterns.ExpandCollapse.IsSupported && combo.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value == ExpandCollapseState.Expanded; }
        catch { return false; }
    }

    private static void SelectSchemeByPattern(AutomationElement combo, string scheme)
    {
        var item = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(i => string.Equals((i.Name ?? string.Empty).Trim(), scheme, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            try
            {
                combo.Patterns.ExpandCollapse.Pattern.Expand();
                Thread.Sleep(400);
                item = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(i => string.Equals((i.Name ?? string.Empty).Trim(), scheme, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
        }

        try { item?.Patterns.SelectionItem.Pattern.Select(); } catch { }
        Thread.Sleep(400);
        try { combo.Patterns.ExpandCollapse.Pattern.Collapse(); } catch { }
        Thread.Sleep(250);
    }

    private static void SetToggleCell(Window mcMain, string automationId, int index, bool on)
    {
        var desired = on ? ToggleState.On : ToggleState.Off;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var cell = CellElement(mcMain, automationId, index);
            if (!cell.Patterns.Toggle.IsSupported) { cell.Click(); Thread.Sleep(500); continue; }
            if (cell.Patterns.Toggle.Pattern.ToggleState.Value == desired) return;
            try { cell.Patterns.Toggle.Pattern.Toggle(); }
            catch { cell.Click(); }
            Thread.Sleep(500);
        }

        var state = CellElement(mcMain, automationId, index).Patterns.Toggle.Pattern.ToggleState.Value;
        if (state != desired)
            throw new InvalidOperationException($"toggle cell '{automationId}' row {index} never reached {(on ? "On" : "Off")} (state {state}).");
    }

    // ---- UIA readers -------------------------------------------------------------------------

    /// <summary>The single grid-cell funnel. A raw FindAll can transiently throw a UIA
    /// COMException (E_UNEXPECTED 0x8000FFFF mid-row-remove, also seen as ELEMENTNOTAVAILABLE
    /// 0x80040201 on a stale element) while the WPF DataGrid re-virtualizes underneath the
    /// tree walk (#61 live run tripped exactly this inside RemoveAllRows). Bounded retry with
    /// backoff, then an honest re-throw - never a silent empty list that would let a
    /// remove/add loop proceed against the wrong row count.</summary>
    private static List<AutomationElement> CellElements(Window mcMain, string automationId)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return mcMain.FindAllDescendants(cf => cf.ByAutomationId(automationId)).ToList();
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
            {
                Thread.Sleep(500 * (attempt + 1));
            }
        }
    }

    /// <summary>Cell for row {index}, WAITING for that row's cell of this column to be realized:
    /// a row's host cell can exist while a sibling cell (scheme combo, remove button) is still
    /// virtualized away, and a typing step that indexes straight into the short list crashed with
    /// ArgumentOutOfRangeException on the #61 live run. Honest timeout once the wait expires.</summary>
    private static AutomationElement CellElement(Window mcMain, string automationId, int index)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var cells = CellElements(mcMain, automationId);
            if (index >= 0 && index < cells.Count)
            {
                return cells[index];
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"grid cell '{automationId}' row {index} never realized ({cells.Count} realized in that column).");
            }

            Thread.Sleep(400);
        }
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

    /// <summary>The ComboBox inside a grid cell (the column's AutomationId may sit on the cell).</summary>
    private static AutomationElement ResolveCombo(AutomationElement target)
    {
        try
        {
            if (target.ControlType != ControlType.ComboBox)
                return target.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)) ?? target;
        }
        catch { }

        return target;
    }

    private static string ReadComboValue(AutomationElement target)
    {
        // A WPF ComboBox only carries the Value pattern when it is editable, and a CLOSED combo
        // realizes no ListItem peers - so a closed, non-editable combo reads empty through either
        // single-shot route (learned live: every stored row's Scheme column came back blank).
        // The grid column's AutomationId can also land on the cell rather than the combo, so the
        // combo is resolved by control type first. Then escalate deliberately: Name, then any
        // realized item peer, then open the popup, read the selected peer, and close it again.
        var combo = ResolveCombo(target);

        try
        {
            if (combo.Patterns.Value.IsSupported)
            {
                var value = combo.Patterns.Value.Pattern.Value.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        catch { }

        try
        {
            if (!string.IsNullOrWhiteSpace(combo.Name)) return combo.Name.Trim();
        }
        catch { }

        var selected = SelectedItemName(combo);
        if (!string.IsNullOrWhiteSpace(selected)) return selected;

        try
        {
            combo.Patterns.ExpandCollapse.Pattern.Expand();
            Thread.Sleep(350);
            selected = SelectedItemName(combo);
            combo.Patterns.ExpandCollapse.Pattern.Collapse();
            Thread.Sleep(200);
            return selected ?? string.Empty;
        }
        catch
        {
            try { combo.Patterns.ExpandCollapse.Pattern.Collapse(); } catch { }
            return string.Empty;
        }
    }

    private static string SelectedItemName(AutomationElement combo)
    {
        try
        {
            var items = combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            var selected = items.FirstOrDefault(i =>
            {
                try { return i.Patterns.SelectionItem.IsSupported && i.Patterns.SelectionItem.Pattern.IsSelected.Value; }
                catch { return false; }
            });
            return (selected?.Name ?? string.Empty).Trim();
        }
        catch { return string.Empty; }
    }

    private static bool ReadToggleState(AutomationElement element)
    {
        try { return element.Patterns.Toggle.IsSupported && element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On; }
        catch { return false; }
    }
}
