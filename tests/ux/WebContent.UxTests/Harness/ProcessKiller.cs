using System.Management;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Kills the desktop clients and the WebView2 browser processes that belong to the
/// plugin's profiles. The browser processes are matched by their --user-data-dir so
/// other WebView2 apps on the workstation are never touched.
/// </summary>
public static class ProcessKiller
{
    public static void KillClients(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    if (!p.CloseMainWindow()) p.Kill();
                }
                catch { }
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               processNames.Any(n => System.Diagnostics.Process.GetProcessesByName(n).Length > 0))
        {
            Thread.Sleep(500);
        }

        foreach (var name in processNames)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try { p.Kill(); } catch { }
            }
        }
    }

    public static void KillPluginWebView2Browsers(string dataRootMarker)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'");
        foreach (ManagementObject proc in searcher.Get())
        {
            var commandLine = proc["CommandLine"] as string ?? string.Empty;
            if (!commandLine.Contains(dataRootMarker, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                System.Diagnostics.Process.GetProcessById(Convert.ToInt32(proc["ProcessId"])).Kill();
            }
            catch { }
        }

        Thread.Sleep(1500);
    }

    public static bool IsRunning(string processName) =>
        System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;
}
