using System;
using System.Text;
using System.Threading;
using FlaUI.Core.AutomationElements;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #58 (SC-5 start-URL row): set the Demo Workspace's home (start) address through the
/// Management Client pane and return the previous value so the probe can restore it. Same
/// discipline as ProxyTargetProvisioning - edit via paste/Value fallback, verified readback,
/// the pre-Save Name guard, and a Save that must actually stick. Assumes the workspace item
/// exists (call after ProxyTargetProvisioning.Ensure, which provisions/recreates it).
/// </summary>
public static class WorkspaceHomeAddress
{
    /// <summary>Sets the home address; returns the previous value (null when already equal).</summary>
    public static string Ensure(UxConfig config, string homeAddress, StringBuilder log)
    {
        ProcessKiller.KillClients("VideoOS.Administration");
        using var mc = ClientSession.Launch(config.AdminClientExe);
        var mcLogin = LoginFlow.WaitForLoginWindow(mc);
        LoginFlow.Login(mc, mcLogin, config);
        LoginFlow.AcceptCertificateTrust(mc);
        var mcMain = LoginFlow.WaitForAdminMain(mc);
        ManagementClientUi.CloseStrayLoginDialogs(mc);
        ManagementClientUi.WaitForTreeReady(mcMain);

        ManagementClientUi.CollapseTreeToRoot(mcMain, timeoutSeconds: 60, allowExpanded: new[] { config.PluginNodeName });
        ManagementClientUi.EnsureExpanded(mcMain, config.PluginNodeName, ManagementClientUi.WorkspacesNodeName);
        ManagementClientUi.EnsureExpanded(mcMain, ManagementClientUi.WorkspacesNodeName, config.McWorkspaceName);
        ManagementClientUi.SelectTreeItem(mcMain, config.McWorkspaceName);

        var homeBox = ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 15);
        var current = ReadValue(homeBox);
        log.AppendLine($"workspace home address before: '{current}'");
        if (string.Equals(current?.Trim(), homeAddress, StringComparison.OrdinalIgnoreCase))
        {
            log.AppendLine($"home already '{homeAddress}'; no Management Client edit needed");
            Retire(mc, mcMain);
            return null;
        }

        var pasted = ManagementClientUi.PasteIntoControl(mcMain, "txtHomeAddress", homeAddress);
        var edited = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 5));
        if (!string.Equals(edited?.Trim(), homeAddress, StringComparison.OrdinalIgnoreCase))
        {
            log.AppendLine($"paste route {(pasted ? "" : "(clipboard unavailable) ")}did not land (readback '{edited}'); ValuePattern fallback");
            ManagementClientUi.SetValue(mcMain, "txtHomeAddress", homeAddress);
            edited = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 5));
        }

        if (!ManagementClientUi.WaitForSaveEnabled(mcMain, 6))
        {
            log.AppendLine("Save still disabled after edit; keyboard-nudging the home box");
            ManagementClientUi.NudgeControlWithKeystroke(mcMain, "txtHomeAddress");
            log.AppendLine($"Save enabled after nudge: {ManagementClientUi.WaitForSaveEnabled(mcMain, 6)}");
        }

        var paneName = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtName", 5));
        if (!string.Equals(paneName, config.McWorkspaceName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to Save: the Name field reads '{paneName}', expected '{config.McWorkspaceName}'.");

        if (!ManagementClientUi.Save(mc, mcMain, 20))
            throw new InvalidOperationException($"Save never completed after setting the home address to '{homeAddress}'.");

        Thread.Sleep(1500);
        var after = ReadValue(ManagementClientUi.ExpectControl(mcMain, "txtHomeAddress", 15));
        if (!string.Equals(after?.Trim(), homeAddress, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"After Save, the home address reads '{after}' — '{homeAddress}' did not stick.");

        log.AppendLine($"home address set to '{homeAddress}' (verified readback)");
        Retire(mc, mcMain);
        return current;
    }

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
