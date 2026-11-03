using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #67 (SC-8): reads the live command lines of the WebView2 browser processes the
/// plugin hosts, so a probe can prove what routing each browser was LAUNCHED with - the
/// --proxy-server argument is per browser process, and "which proxy does this browser obey"
/// is decided there, not in any plugin-side log. Processes are scoped by --user-data-dir
/// (same marker ProcessKiller uses) so other WebView2 apps on the workstation never appear.
/// </summary>
public static class BrowserProcessArgs
{
    /// <summary>Full command line of every msedgewebview2.exe referencing the given data-root marker.</summary>
    public static List<string> CommandLines(string dataRootMarker)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'");
        var found = new List<string>();
        foreach (ManagementObject proc in searcher.Get())
        {
            var commandLine = proc["CommandLine"] as string ?? string.Empty;
            if (commandLine.Contains(dataRootMarker ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                found.Add(commandLine);
        }

        return found;
    }

    /// <summary>One compact line per process: user-data-dir, and every proxy-shaping switch
    /// actually present (--proxy-server, --proxy-bypass-list, --no-proxy-server). Absent
    /// switches are simply not named, so "no proxy switch" is visible as silence.</summary>
    public static string Summarize(string commandLine)
    {
        var userDataDir = MatchSwitch(commandLine, "--user-data-dir");
        var proxyServer = MatchSwitch(commandLine, "--proxy-server");
        var bypass = MatchSwitch(commandLine, "--proxy-bypass-list");
        var flags = new List<string>();
        if (commandLine.Contains("--no-proxy-server", StringComparison.OrdinalIgnoreCase)) flags.Add("--no-proxy-server");
        if (commandLine.Contains(" --browser", StringComparison.OrdinalIgnoreCase) ||
            commandLine.EndsWith("--browser", StringComparison.OrdinalIgnoreCase)) flags.Add("--browser");
        return $"user-data-dir='{userDataDir}' proxy-server='{proxyServer}' proxy-bypass-list='{bypass}' flags=[{string.Join(",", flags)}]";
    }

    private static string MatchSwitch(string commandLine, string switchName)
    {
        var match = Regex.Match(commandLine ?? string.Empty,
            Regex.Escape(switchName) + "=(\"(?<q>[^\"]*)\"|(?<b>[^\\s]*))");
        if (!match.Success) return string.Empty;
        return match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["b"].Value;
    }
}
