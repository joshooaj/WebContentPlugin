using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WebContent.Client
{
    /// <summary>Counts and the once-per-deletion log lines of one <see cref="ProfileSweep.Run"/>.</summary>
    internal sealed class ProfileSweepReport
    {
        internal int ProxiedFoldersSeen { get; set; }

        internal int ProxiedFoldersReaped { get; set; }

        internal int ProxiedFoldersSkippedLocked { get; set; }

        internal int ClaimFilesReaped { get; set; }

        internal int ClaimTempFilesReaped { get; set; }

        /// <summary>Every non-quiet line (each deletion logged exactly once; skips and refusals too).</summary>
        internal List<string> LogLines { get; } = new List<string>();
    }

    /// <summary>
    /// The thin filesystem seam of the #94 startup sweep: it walks ONLY the plugin's own
    /// proxied shape classes under a per-user browser data root - <c>proxied-*</c> folders and
    /// their <c>.claim</c>/<c>.claim.tmp-*</c> siblings, at both the #92 boundary shape
    /// (<c>&lt;user&gt;\server-&lt;leg&gt;\proxied-*</c>) and the pre-#92 old shape
    /// (<c>&lt;user&gt;\proxied-*</c>, orphaned on purpose by #92's re-keying) - gathers age and
    /// lock facts, and applies the pure <see cref="ProfileSweepCore"/> decisions with real
    /// deletes. Non-proxied state (the user's <c>EBWebView</c> tree, component caches, anything
    /// not matching the shape) is never descended into or deleted.
    ///
    /// Every probe is an injectable seam (pid/port liveness shared with the #93 store, browser
    /// lock witness, clock, retention), so the disk tests run against a real temp directory
    /// without processes or sockets, and production wires the OS-backed probes. Deletes are
    /// best-effort: a refusal (in-use file, permissions) is journalled, never retried, never
    /// fatal - the sweep may run again next startup.
    /// </summary>
    internal sealed class ProfileSweep
    {
        /// <summary>How long a proxied profile must be untouched before the sweep may reap it.</summary>
        internal static readonly TimeSpan DefaultFolderRetention = TimeSpan.FromDays(30);

        /// <summary>How long <c>.claim.tmp-*</c> litter must sit before it cannot be a mid-flight create.</summary>
        internal static readonly TimeSpan DefaultClaimTempLitterRetention = TimeSpan.FromHours(24);

        /// <summary>Bounded-scan cap: hitting it means "could not finish", which keeps the folder.</summary>
        internal const int MaxActivityScanEntries = 20000;

        /// <summary>The WebView2 runtime's own subfolder name under any plugin-requested UDF.</summary>
        internal const string BrowserWebViewSubfolderName = "EBWebView";

        private static readonly string[] LockFileProbes =
        {
            // Chromium's profile lock, relative to the plugin-REQUESTED UDF: the runtime roots
            // its tree under <requested>\EBWebView, so the lockfile sits there (or at the
            // requested root for runtimes that keep it flat). Both are cheap stat probes.
            Path.Combine(BrowserWebViewSubfolderName, "lockfile"),
            "lockfile",
        };

        private readonly Func<int, bool> _ownerProcessAlive;
        private readonly Func<int, bool> _loopbackPortConnectable;
        private readonly Func<string, bool> _browserLockHeld;
        private readonly int _selfPid;

        /// <param name="ownerProcessAlive">pid liveness seam; defaults to the #93 production probe.</param>
        /// <param name="loopbackPortConnectable">port liveness seam; defaults to the #93 production probe.</param>
        /// <param name="browserLockHeld">folder lock witness; defaults to the lockfile-exclusive-open
        /// probe. Production wiring composes in the msedgewebview2 command-line witness
        /// (<see cref="BrowserProcessLockWitness"/>), which no unit test needs.</param>
        /// <param name="selfPid">pid whose own claims are never reaped; defaults to this process.</param>
        internal ProfileSweep(
            Func<int, bool> ownerProcessAlive = null,
            Func<int, bool> loopbackPortConnectable = null,
            Func<string, bool> browserLockHeld = null,
            int? selfPid = null)
        {
            _ownerProcessAlive = ownerProcessAlive ?? ListenerClaimStore.ProcessExists;
            _loopbackPortConnectable = loopbackPortConnectable ?? ListenerClaimStore.LoopbackPortConnectable;
            _browserLockHeld = browserLockHeld ?? BrowserLockFileHeld;
            _selfPid = selfPid ?? System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        /// <summary>Production-shaped run: real now, both default retentions.</summary>
        internal ProfileSweepReport Run(string dataRoot)
        {
            return Run(dataRoot, DateTimeOffset.UtcNow, DefaultFolderRetention, DefaultClaimTempLitterRetention);
        }

        internal ProfileSweepReport Run(string dataRoot, DateTimeOffset nowUtc, TimeSpan folderRetention, TimeSpan claimTempLitterRetention)
        {
            var report = new ProfileSweepReport();
            if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot))
            {
                return report;
            }

            // The root itself doubles as the UDF fallback (identity unresolvable), so its EBWebView
            // tree is explicitly never descended. Depth is shape-bounded: root -> [user key] ->
            // [server-<leg>] -> proxied leaf; nothing below a proxied leaf is enumerated, and no
            // directory that matches no shape class ever is.
            SweepClaimShapes(dataRoot, report, nowUtc, claimTempLitterRetention);

            foreach (var top in SafeEnumerateDirectories(dataRoot))
            {
                var topName = Path.GetFileName(top);
                if (ProfileSweepCore.IsProxiedFolderName(topName))
                {
                    SweepProxiedFolder(top, report, nowUtc, folderRetention);
                }
                else if (ProfileSweepCore.IsServerFolderName(topName))
                {
                    SweepServerDir(top, report, nowUtc, folderRetention, claimTempLitterRetention);
                }
                else if (!string.Equals(topName, BrowserWebViewSubfolderName, StringComparison.OrdinalIgnoreCase))
                {
                    // A per-XProtect-user key dir: it holds the non-proxied UDF (EBWebView -
                    // skipped, never touched) plus this user's proxied folders, both the old
                    // (pre-#92) and the boundary shape.
                    SweepClaimShapes(top, report, nowUtc, claimTempLitterRetention);
                    foreach (var child in SafeEnumerateDirectories(top))
                    {
                        var name = Path.GetFileName(child);
                        if (ProfileSweepCore.IsProxiedFolderName(name))
                        {
                            SweepProxiedFolder(child, report, nowUtc, folderRetention);
                        }
                        else if (ProfileSweepCore.IsServerFolderName(name))
                        {
                            SweepServerDir(child, report, nowUtc, folderRetention, claimTempLitterRetention);
                        }
                    }
                }
            }

            return report;
        }

        /// <summary>One boundary dir: its proxied leaves, then its claim slot + litter beside them.</summary>
        private void SweepServerDir(string dir, ProfileSweepReport report, DateTimeOffset nowUtc, TimeSpan folderRetention, TimeSpan litterRetention)
        {
            foreach (var child in SafeEnumerateDirectories(dir))
            {
                if (ProfileSweepCore.IsProxiedFolderName(Path.GetFileName(child)))
                {
                    SweepProxiedFolder(child, report, nowUtc, folderRetention);
                }
            }

            // After the folder pass, so a folder this sweep just reaped makes its claim read
            // "sibling profile folder is gone" instead of racing liveness probes for it.
            SweepClaimShapes(dir, report, nowUtc, litterRetention);
        }

        private void SweepProxiedFolder(string path, ProfileSweepReport report, DateTimeOffset nowUtc, TimeSpan folderRetention)
        {
            report.ProxiedFoldersSeen++;

            var scan = ScanActivity(path, (nowUtc - folderRetention).UtcDateTime);
            if (scan.Recent)
            {
                return; // inside retention: quiet keep, no lock probe spent
            }

            if (scan.Truncated)
            {
                report.LogLines.Add("Proxied browser profile '" + path + "': the activity scan hit the " +
                                    MaxActivityScanEntries.ToString(CultureInfo.InvariantCulture) +
                                    "-entry cap; kept this sweep.");
                return;
            }

            var move = ProfileSweepCore.DecideFolder(
                scan.LastActivityUtc.HasValue ? new DateTimeOffset(scan.LastActivityUtc.Value, TimeSpan.Zero) : (DateTimeOffset?)null,
                _browserLockHeld(path),
                nowUtc,
                folderRetention);

            switch (move.Step)
            {
                case ProxiedFolderStep.SkipLocked:
                    report.ProxiedFoldersSkippedLocked++;
                    report.LogLines.Add("Skipped locked proxied browser profile '" + path + "' (" + move.Audit + ").");
                    return;

                case ProxiedFolderStep.Reap:
                    try
                    {
                        Directory.Delete(path, true);
                        report.ProxiedFoldersReaped++;
                        report.LogLines.Add("Swept orphaned proxied browser profile '" + path + "' (" + move.Audit + ").");
                    }
                    catch (IOException ex)
                    {
                        // The final never-force arbiter: even with the lock probes quiet, a live
                        // handle refuses the delete; journal and move on.
                        report.LogLines.Add("Proxied browser profile '" + path + "' met the retention rule but refused deletion (left in place): " + ex.Message);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        report.LogLines.Add("Proxied browser profile '" + path + "' met the retention rule but deletion is denied (left in place): " + ex.Message);
                    }

                    return;

                default: // Keep with an audit (activity undetermined)
                    if (move.Audit != null)
                    {
                        report.LogLines.Add("Proxied browser profile '" + path + "': kept - " + move.Audit + ".");
                    }

                    return;
            }
        }

        private void SweepClaimShapes(string dir, ProfileSweepReport report, DateTimeOffset nowUtc, TimeSpan litterRetention)
        {
            foreach (var file in SafeEnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (ProfileSweepCore.IsListenerClaimTempName(name))
                {
                    SweepClaimTemp(file, nowUtc, litterRetention, report);
                }
                else if (ProfileSweepCore.IsListenerClaimName(name))
                {
                    SweepClaimFile(file, name, report);
                }
            }
        }

        private void SweepClaimFile(string path, string name, ProfileSweepReport report)
        {
            var read = ReadClaimContent(path);
            var stem = name.Substring(0, name.Length - BrowserProfileKey.ListenerClaimExtension.Length);
            var expectedBrowserId = ProfileSweepCore.TryParseProxiedLeafBrowserId(stem);
            var siblingFolderExists = Directory.Exists(Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, stem));

            var move = ProfileSweepCore.DecideClaimFile(
                read, expectedBrowserId, _selfPid, siblingFolderExists, _ownerProcessAlive, _loopbackPortConnectable);

            if (move.Step == ClaimFileStep.Keep)
            {
                if (move.Audit != null)
                {
                    report.LogLines.Add("Listener claim '" + path + "': kept - " + move.Audit + ".");
                }

                return;
            }

            try
            {
                File.Delete(path);
                report.ClaimFilesReaped++;
                report.LogLines.Add("Swept listener claim '" + path + "' - " + move.Audit + ".");
            }
            catch (IOException ex)
            {
                report.LogLines.Add("Listener claim '" + path + "' would be swept but refused deletion (left in place): " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                report.LogLines.Add("Listener claim '" + path + "' would be swept but deletion is denied (left in place): " + ex.Message);
            }
        }

        private void SweepClaimTemp(string path, DateTimeOffset nowUtc, TimeSpan litterRetention, ProfileSweepReport report)
        {
            DateTime writeUtc;
            try
            {
                writeUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            var move = ProfileSweepCore.DecideClaimTemp(new DateTimeOffset(writeUtc, TimeSpan.Zero), nowUtc, litterRetention);
            if (move.Step == ClaimFileStep.Keep)
            {
                return;
            }

            try
            {
                File.Delete(path);
                report.ClaimTempFilesReaped++;
                report.LogLines.Add("Swept listener-claim temp litter '" + path + "' - " + move.Audit + ".");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Mirrors the #93 store's read: missing is NotFound, an IO failure is an Untrusted with
        /// the reason (never trusted, never a crash).
        /// </summary>
        private static ListenerClaimRead ReadClaimContent(string path)
        {
            if (!File.Exists(path))
            {
                return ListenerClaimRead.NotFound();
            }

            try
            {
                return ListenerClaim.Read(File.ReadAllText(path));
            }
            catch (IOException ex)
            {
                return ListenerClaimRead.Untrusted("the file could not be read: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ListenerClaimRead.Untrusted("reading is denied: " + ex.Message);
            }
        }

        /// <summary>
        /// Bounded, early-exit activity scan of one proxied folder: it walks the folder's own
        /// timestamps plus its whole subtree, stopping at the first entry at/after the cutoff
        /// (a folder in retention costs almost nothing; only already-orphan-looking folders get
        /// walked to the end). IO failures on a subtree do not conclude recency; an exhausted
        /// entry cap reports Truncated so the caller keeps the folder.
        /// </summary>
        private static ActivityScan ScanActivity(string root, DateTime cutoffUtc)
        {
            var scan = new ActivityScan();
            long entries = 0;

            var dirs = new List<string> { root };
            var index = 0;
            while (index < dirs.Count)
            {
                var dir = dirs[index++];

                DateTime dirWrite;
                try
                {
                    dirWrite = Directory.GetLastWriteTimeUtc(dir);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                if (dirWrite >= cutoffUtc)
                {
                    scan.Recent = true;
                    return scan;
                }

                if (!scan.LastActivityUtc.HasValue || dirWrite > scan.LastActivityUtc.Value)
                {
                    scan.LastActivityUtc = dirWrite;
                }

                string[] children;
                try
                {
                    children = Directory.GetFileSystemEntries(dir);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (++entries > MaxActivityScanEntries)
                    {
                        scan.Truncated = true;
                        return scan;
                    }

                    DateTime childWrite;
                    try
                    {
                        childWrite = Directory.Exists(child)
                            ? Directory.GetLastWriteTimeUtc(child)
                            : File.GetLastWriteTimeUtc(child);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (childWrite >= cutoffUtc)
                    {
                        scan.Recent = true;
                        return scan;
                    }

                    if (childWrite > scan.LastActivityUtc)
                    {
                        scan.LastActivityUtc = childWrite;
                    }

                    if (Directory.Exists(child))
                    {
                        dirs.Add(child);
                    }
                }
            }

            return scan;
        }

        private sealed class ActivityScan
        {
            internal bool Recent;

            internal bool Truncated;

            internal DateTime? LastActivityUtc;
        }

        /// <summary>
        /// Production lock probe (one half of the wiring's witness): Chromium's profile
        /// <c>lockfile</c> exists and cannot be opened exclusively WHILE held means a live
        /// browser owns this UDF. A lockfile that opens cleanly is a crashed browser's stale
        /// leftover (Chromium deletes it on clean exit); no lockfile at all is quiet.
        /// </summary>
        internal static bool BrowserLockFileHeld(string profileFolder)
        {
            foreach (var probe in LockFileProbes)
            {
                var candidate = Path.Combine(profileFolder, probe);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    using (var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        // Opened exclusively: nobody holds it.
                    }
                }
                catch (IOException)
                {
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    // Cannot prove it free: treat as held.
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string dir)
        {
            try
            {
                return Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string dir)
        {
            try
            {
                return Directory.GetFiles(dir);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new string[0];
            }
        }
    }
}
