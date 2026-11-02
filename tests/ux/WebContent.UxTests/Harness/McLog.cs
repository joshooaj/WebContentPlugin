namespace WebContent.UxTests.Harness;

/// <summary>
/// Reads the Management Client's MIP.log (plus its rolling archives) as a verification channel.
/// The log is the one view of plugin behaviour that does not depend on how a WPF control happens
/// to surface through accessibility, so it is what settles arguments about what the shell really
/// did - the proxy allowlist's stored shape (#81) being the case that made it necessary.
/// </summary>
public static class McLog
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Milestone", "XProtect Management Client", "Logs");

    /// <summary>Every log line that contains ALL of the given fragments (case-sensitive: these are
    /// the plugin's own marker phrases and the exact entry strings it prints).</summary>
    public static List<string> MatchingLines(string mustContain, params string[] andAlso)
    {
        var found = new List<string>();
        foreach (var file in LogFiles())
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.Contains(mustContain, StringComparison.Ordinal)) continue;
                    if (andAlso.Any(f => !line.Contains(f, StringComparison.Ordinal))) continue;
                    found.Add(line);
                }
            }
            catch { /* locked mid-write or unreadable: try the next file */ }
        }

        return found;
    }

    public static int CountMatches(string mustContain, params string[] andAlso) =>
        MatchingLines(mustContain, andAlso).Count;

    /// <summary>Waits until the log carries more matching lines than <paramref name="before"/> and
    /// returns the newest of them, or null when the delta never arrived.</summary>
    public static string WaitForNewLine(int before, int timeoutSeconds, string mustContain, params string[] andAlso)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var lines = MatchingLines(mustContain, andAlso);
            if (lines.Count > before) return lines[lines.Count - 1];
            Thread.Sleep(1000);
        }

        return null;
    }

    private static IEnumerable<string> LogFiles()
    {
        var candidates = new List<string>();
        try
        {
            var top = Path.Combine(Root, "MIP.log");
            if (File.Exists(top)) candidates.Add(top);

            var archive = Path.Combine(Root, "Archive");
            if (Directory.Exists(archive)) candidates.AddRange(Directory.EnumerateFiles(archive, "MIP*.log"));
        }
        catch { /* no MC log on this machine */ }

        return candidates;
    }
}
