using System;
using System.Linq;
using System.Text;
using System.Threading;
using FlaUI.Core.AutomationElements;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Shared Management Client phase for every proxied-target probe: make sure the workspace
/// proxy target list carries exactly one line for the probe origin - the entry as given,
/// replacing any older line for the same origin so a previous run's flag state never leaks
/// into this row's verdict. Runs before the Smart Client so the fetched configuration
/// carries the new allowlist. Includes the self-repair discovered the hard way: adopting a
/// mis-named item, recreating a destroyed one (Add New), focus-gated keyboard edits, the
/// pre-Save Name guard, and verified readback.
/// </summary>
public static class ProxyTargetProvisioning
{
    public static void Ensure(UxConfig config, string entry, StringBuilder log)
    {
        ProcessKiller.KillClients("VideoOS.Administration");
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        ManagementClientUi.CloseStrayLoginDialogs(mc);
        ManagementClientUi.WaitForTreeReady(mcMain);

        // Canonical starting state (fast double-click collapse, no keyboard), then walk the
        // known chain top-down (enumerate rows + click + wheel scroll): each step realizes the
        // rows the next one needs, so no blind walking or long sleeps are involved.
        ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
        ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);

        // A previous bad Save once renamed the item (txtName corrupted by an ungated paste -
        // MIP.log 'ItemName.get ... http://httpforever.com'), and later evidence showed the
        // item gone entirely. Adopt a mis-named lone child, or recreate via the shell New
        // affordance, before touching anything else.
        var workspaceItem = config.McWorkspaceName;
        var paneReady = false;
        try
        {
            ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
        }
        catch (InvalidOperationException)
        {
            ManagementClientUi.DumpWorkspaceChildren(mcMain, log);
            // The last toggle strategy may have left the node collapsed: one more expansion
            // pass with no child-name assertion, then look for the lone child.
            try { ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, new string[0]); }
            catch (InvalidOperationException) { }
            ManagementClientUi.DumpWorkspaceChildren(mcMain, log);
            var orphan = ManagementClientUi.OrphanWorkspaceName(mcMain, ManagementClientUi.WorkspacesNodeName);
            if (orphan != null)
            {
                log.AppendLine($"workspace item is named '{orphan}', not '{config.McWorkspaceName}' - adopting and renaming");
                workspaceItem = orphan;
            }
            else
            {
                log.AppendLine("workspaces node has NO children - recreating the item via the shell New menu");
                if (!ManagementClientUi.NewItemUnderNode(mcMain, mc, ManagementClientUi.WorkspacesNodeName, log))
                    throw new InvalidOperationException(
                        $"Workspace '{config.McWorkspaceName}' is gone from the config and the shell New menu was unavailable.");
                ManagementClientUi.AcceptPendingModal(mc, 5); // a name prompt, if the shell shows one
                paneReady = true; // the New flow leaves the pane showing the fresh item
                log.AppendLine("created new workspace item; filling the pane");
            }
        }
        if (!paneReady) ManagementClientUi.SelectTreeItem(mcMain, workspaceItem);

        var nameBox = ManagementClientUi.ExpectControl(mcMain, "txtName", 15);
        var paneName = ReadValue(nameBox);
        if (!string.Equals(paneName, config.McWorkspaceName, StringComparison.Ordinal))
        {
            log.AppendLine($"pane Name field reads '{paneName}'; retyping '{config.McWorkspaceName}'");
            ManagementClientUi.RenameWorkspacePane(mcMain, config.McWorkspaceName);
        }

        var targetsBox = ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 30);
        var current = ReadValue(targetsBox);
        log.AppendLine($"proxy targets before: '{current}'");

        var lines = (current ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Any(line => string.Equals(line, entry, StringComparison.OrdinalIgnoreCase)))
        {
            log.AppendLine($"entry '{entry}' already allowlisted; no Management Client edit needed");
            Retire(mc, mcMain);
            return;
        }

        // Same origin with a different flag state is a contradictory entry: exactly one line
        // decides the origin's TLS posture.
        var originToken = entry.Split(' ')[0];
        lines.RemoveAll(line => line.Split(' ')[0].Equals(originToken, StringComparison.OrdinalIgnoreCase));

        ManagementClientUi.EnsureCheckBox(mcMain, "chkProxyEnabled", true);
        lines.Add(entry);
        var joined = string.Join("\n", lines);

        var pasted = ManagementClientUi.PasteIntoControl(mcMain, "txtProxyTargets", joined);
        var edited = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 5));
        if (!SameLines(joined, edited))
        {
            log.AppendLine($"paste route {(pasted ? "" : "(clipboard unavailable) ")}did not land (readback '{edited}'); ValuePattern fallback");
            ManagementClientUi.SetValue(mcMain, "txtProxyTargets", joined);
            edited = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 5));
        }
        log.AppendLine($"proxy targets right after edit: '{edited}'");

        if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
        {
            log.AppendLine("Save still disabled after edit; flipping chkProxyEnabled twice to dirty the pane");
            ManagementClientUi.DirtyPaneViaCheckbox(mcMain, "chkProxyEnabled");
            if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
            {
                log.AppendLine("Save still disabled after checkbox flip; keyboard-nudging the targets box");
                ManagementClientUi.NudgeControlWithKeystroke(mcMain, "txtProxyTargets");
                log.AppendLine($"Save enabled after nudge: {ManagementClientUi.WaitForSaveEnabled(mcMain, 6)}");
            }
        }

        // Guard before the Save: the shell writes EVERY pane field, and one bad run already
        // saved a corrupted Name ('ItemName.get ... http://httpforever.com'). Never re-save
        // a pane whose Name field does not read the workspace name.
        var paneNameNow = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtName", 5));
        if (!string.Equals(paneNameNow, config.McWorkspaceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to Save: the Name field reads '{paneNameNow}', expected '{config.McWorkspaceName}'.");

        if (!ManagementClientUi.Save(mc, mcMain, 20))
        {
            throw new InvalidOperationException($"Save never became enabled after adding '{entry}' to the proxy targets.");
        }

        // Read the pane back: a save the shell silently discarded (or a confirmation whose
        // No/Cancel path was taken) must fail here, not strand the Smart Client phase with a
        // stale allowlist.
        Thread.Sleep(1500);
        var after = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtProxyTargets", 15));
        if (after == null || !after.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.Trim().Equals(entry, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"After Save, the proxy targets read back '{after}' — '{entry}' did not stick.");
        }

        log.AppendLine($"allowlisted '{entry}' and saved (verified readback)");
        Retire(mc, mcMain);
    }

    /// <summary>
    /// Closes Management Client after its phase: an MC left open can hold a pending
    /// "Save changes?" modal that steals focus and blocks UIA for the Smart Client phase
    /// (the #56 hang). Close politely, sweep any confirmation it raises, then guarantee exit.
    /// </summary>
    private static void Retire(ClientSession mc, Window mcMain)
    {
        try { mcMain.Close(); } catch { }
        ManagementClientUi.AcceptPendingModal(mc, 5);
        ProcessKiller.KillClients("VideoOS.Administration");
    }

    private static string ReadValue(AutomationElement element)
    {
        try { return element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.Value.Value : element.Name; }
        catch { return element.Name; }
    }

    private static bool SameLines(string expected, string actual)
    {
        string[] Norm(string s) => (s ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        return Norm(expected).SequenceEqual(Norm(actual));
    }
}
