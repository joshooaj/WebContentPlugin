using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System.Diagnostics;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Starts an XProtect desktop client with WebView2 remote debugging enabled through
/// WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS (the plugin never sets AdditionalBrowserArguments,
/// so the env var wins) and attaches FlaUI to it.
/// </summary>
public sealed class ClientSession : IDisposable
{
    public Process Process { get; }
    public Application Application { get; }
    public UIA3Automation Automation { get; }

    public ClientSession(Process process, Application application, UIA3Automation automation)
    {
        Process = process;
        Application = application;
        Automation = automation;
    }

    public static ClientSession Launch(string exePath, int startupSeconds = 30)
    {
        var procName = Path.GetFileNameWithoutExtension(exePath);
        var automation = new UIA3Automation();
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var startedAt = DateTime.Now;
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                };
                psi.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--remote-debugging-port=0";

                Process startedProcess = null;
                try
                {
                    startedProcess = Process.Start(psi);
                }
                catch
                {
                    // Some XProtect client launchers are single-instance stubs; wait for the
                    // real GUI process below instead of treating the stub as fatal.
                }

                Thread.Sleep(500);
                if (startedProcess != null)
                {
                    try
                    {
                        return new ClientSession(startedProcess, Application.Attach(startedProcess), automation);
                    }
                    catch
                    {
                        // fall through to process-name discovery
                    }
                }

                var deadline = DateTime.UtcNow.AddSeconds(startupSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    var candidates = Process.GetProcessesByName(procName)
                        .Where(p =>
                        {
                            try
                            {
                                return !p.HasExited && p.StartTime >= startedAt;
                            }
                            catch
                            {
                                return false;
                            }
                        })
                        .OrderByDescending(p =>
                        {
                            try { return p.Id; } catch { return 0; }
                        })
                        .ToList();

                    if (candidates.Count == 0 && attempt > 0)
                    {
                        candidates = Process.GetProcessesByName(procName)
                            .Where(p =>
                            {
                                try { return !p.HasExited; } catch { return false; }
                            })
                            .OrderByDescending(p =>
                            {
                                try { return p.Id; } catch { return 0; }
                            })
                            .ToList();
                    }

                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            return new ClientSession(candidate, Application.Attach(candidate), automation);
                        }
                        catch
                        {
                        }
                    }

                    Thread.Sleep(500);
                }

                ProcessKiller.KillClients(procName);
            }

            throw new InvalidOperationException($"Failed to attach to {procName} after launching {exePath}.");
        }
        catch
        {
            automation.Dispose();
            throw;
        }
    }

    public Window[] Windows()
    {
        try
        {
            return Application.GetAllTopLevelWindows(Automation);
        }
        catch
        {
            return Array.Empty<Window>();
        }
    }

    /// <summary>Polls until the predicate returns a window, or throws after the timeout.</summary>
    public Window WaitForWindow(Func<Window, bool> predicate, int timeoutSeconds, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            // Windows can die while being inspected; a COM failure on one window must not kill the poll.
            var window = Windows().FirstOrDefault(w => { try { return predicate(w); } catch { return false; } });
            if (window != null) return window;
            Thread.Sleep(500);
        }
        throw new TimeoutException($"Timed out waiting for {description} (process {Process.ProcessName}, pid {Process.Id}).");
    }

    public Window FindWindow(Func<Window, bool> predicate) =>
        Windows().FirstOrDefault(w => { try { return predicate(w); } catch { return false; } });

    public void Dispose()
    {
        Automation.Dispose();
        Application.Dispose();
    }
}
