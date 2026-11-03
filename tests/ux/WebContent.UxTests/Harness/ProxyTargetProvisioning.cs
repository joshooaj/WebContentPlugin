using System;
using System.Linq;
using System.Text;

using FlaUI.Core.AutomationElements;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Shared Management Client phase for every proxied-target probe: make sure the workspace
/// allowlist carries exactly one row for the probe origin - the entry as given, replacing any
/// older row for the same origin so a previous run's flag state never leaks into this row's
/// verdict. Since #81 the allowlist is the structured grid on the Proxy tab (no free-text box),
/// so the whole edit/save/readback discipline is <see cref="AllowlistGridUi.SetEntriesAndSave"/>.
/// Runs before the Smart Client so the fetched configuration carries the new allowlist. Includes
/// the self-repair discovered the hard way: adopting a mis-named item, recreating a destroyed
/// one (Add New), the pre-Save Name guard, and a readback taken from a reloaded pane.
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

        // #81: the allowlist is a structured grid on the Proxy tab now - one entry per row, the
        // accept-untrusted flag a checkbox instead of a line token. Rows are read back in the
        // same probe-record shape the old text box wrote, so the entry strings here are unchanged.
        AllowlistGridUi.OpenProxyTab(mcMain);
        var current = AllowlistGridUi.ReadEntries(mcMain);
        log.AppendLine($"proxy targets before: '{string.Join(" | ", current)}'");

        if (current.Any(line => string.Equals(line, entry, StringComparison.OrdinalIgnoreCase)))
        {
            log.AppendLine($"entry '{entry}' already allowlisted; no Management Client edit needed");
            Retire(mc, mcMain);
            return;
        }

        // Same origin with a different flag state is a contradictory entry: exactly one row
        // decides the origin's TLS posture.
        var originToken = entry.Split(' ')[0];
        var lines = current
            .Where(line => !line.Split(' ')[0].Equals(originToken, StringComparison.OrdinalIgnoreCase))
            .ToList();
        lines.Add(entry);

        AllowlistGridUi.SetEntriesAndSave(mc, mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName, lines, log);
        log.AppendLine($"allowlisted '{entry}' and saved");
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
}
