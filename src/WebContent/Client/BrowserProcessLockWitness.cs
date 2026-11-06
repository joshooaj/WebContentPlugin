using System;
using System.Collections.Generic;
using System.Management;

namespace WebContent.Client
{
    /// <summary>
    /// The live-browser half of the #94 sweep's folder-lock witness: does any running
    /// <c>msedgewebview2.exe</c> name this profile folder in its command line? This is the
    /// witness the SC-8/SC-11 live rows already trust - the WebView2 RUNTIME process carries
    /// <c>--user-data-dir="&lt;requested UDF&gt;\EBWebView"</c>, so a substring match on the
    /// plugin-requested folder path covers both spellings. The process list is read ONCE per
    /// witness instance (the first stale-looking folder pays the WMI query; the rest are free).
    ///
    /// Plugin-only on purpose: it needs System.Management, which the linked unit-test sources
    /// never pull in. Tests inject their own lock seam instead.
    /// </summary>
    internal sealed class BrowserProcessLockWitness
    {
        private List<string> _commandLines;

        /// <summary>Why the command-line read failed (null once it succeeded). The wiring logs it once.</summary>
        internal string ProbeError { get; private set; }

        internal bool NamesFolder(string profileFolderPath)
        {
            if (string.IsNullOrWhiteSpace(profileFolderPath))
            {
                return false;
            }

            if (_commandLines == null)
            {
                _commandLines = ReadWebviewCommandLines();
            }

            foreach (var commandLine in _commandLines)
            {
                if (commandLine.IndexOf(profileFolderPath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private List<string> ReadWebviewCommandLines()
        {
            var lines = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'"))
                using (var results = searcher.Get())
                {
                    foreach (var result in results)
                    {
                        var line = result["CommandLine"] as string;
                        if (!string.IsNullOrEmpty(line))
                        {
                            lines.Add(line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // WMI unavailable (service disabled, access denied): the sweep still has the
                // lockfile probe and the never-force delete failure; journal which witness died.
                ProbeError = ex.Message;
            }

            return lines;
        }
    }
}
