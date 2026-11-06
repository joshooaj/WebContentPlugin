using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// #94 wiring guards: the sweep's decision core and IO seam are unit-tested directly, but
    /// the STARTUP HOOK lives in plugin Init inside a host no unit harness boots. These rows pin
    /// the acceptance shape of that hook - scheduled off the UI thread on a one-shot timer, every
    /// failure path journalled and never rethrown, the production lock witness composed from
    /// BOTH probes, and the two new files linked into the test assembly (the AGENTS.md both-
    /// Compile-lists rule, whose silent failure mode is the plugin or the tests missing a class).
    /// </summary>
    public class ProfileSweepStartupGuardTests
    {
        private static string ReadPluginSource(string name)
        {
            return File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", name));
        }

        private static string Collapse(string source)
        {
            return Regex.Replace(source, @"\s+", " ");
        }

        [Fact]
        public void SmartClient_Init_Schedules_The_Sweep_But_Runs_Nothing_Synchronously()
        {
            var collapsed = Collapse(ReadPluginSource("WebContentPluginDefinition.cs"));

            var smartClientCase = Regex.Match(
                collapsed,
                @"case EnvironmentType\.SmartClient: (?<body>.*?) case EnvironmentType\.Standalone",
                RegexOptions.Singleline);
            Assert.True(smartClientCase.Success, "the Smart Client Init branch was not found.");
            Assert.Contains("ScheduleProxiedProfileSweep();", smartClientCase.Groups["body"].Value);

            // The scheduling itself is a one-shot threadpool timer - no disk walk on the Init
            // (UI) thread, and nothing repeats it per Init call.
            Assert.Contains(
                "new Timer(ProfileSweepCallback, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan)",
                collapsed);
        }

        [Fact]
        public void The_Sweep_Callback_Catches_Everything_And_Never_Breaks_A_View()
        {
            var collapsed = Collapse(ReadPluginSource("WebContentPluginDefinition.cs"));

            var callback = Regex.Match(
                collapsed,
                @"private void ProfileSweepCallback\(object state\) \{ (?<body>.*?) private void RegisterWorkspaceRecoveryReceivers",
                RegexOptions.Singleline);
            Assert.True(callback.Success, "ProfileSweepCallback was not found before RegisterWorkspaceRecoveryReceivers.");
            var body = callback.Groups["body"].Value;

            // Any throw is journalled through the plugin logger, whose own failure is swallowed
            // too; the callback never rethrows onto the threadpool (crashing the host).
            Assert.Contains("catch (Exception ex)", body);
            Assert.Contains("EnvironmentManager.Instance.Log( true, nameof(ProfileSweep), \"Startup proxied-profile sweep failed", body);
            Assert.DoesNotContain("throw", body, StringComparison.Ordinal);

            // The sweep root is this Windows user's plugin data root, never a machine-wide path.
            Assert.Contains(
                "Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), BrowserProfileKey.DataFolderRootName",
                body);

            // Production composes BOTH lock witnesses: the lockfile probe and the live
            // msedgewebview2 command-line probe over the same folder.
            Assert.Contains("ProfileSweep.BrowserLockFileHeld(folder) || processWitness.NamesFolder(folder)", body);

            // The one-shot timer is torn down in finally, and Close() stops a not-yet-fired one.
            Assert.Contains("finally { var timer = _profileSweepTimer;", body);
            Assert.Contains("StopWorkspaceRetryTimer(); StopProxiedProfileSweepTimer();", collapsed);
        }

        [Fact]
        public void Both_Sweep_Classes_Are_Linked_Into_The_Test_Assembly_Too()
        {
            var testProject = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent.Tests", "WebContent.Tests.csproj"));

            // AGENTS.md both-Compile-lists rule: the pure core AND the IO seam cross, the
            // WMI-backed witness deliberately does not (System.Management stays plugin-only).
            Assert.Contains(@"Client\ProfileSweepCore.cs", testProject);
            Assert.Contains(@"Client\ProfileSweep.cs", testProject);
            Assert.DoesNotContain("BrowserProcessLockWitness.cs", testProject);
        }

        [Fact]
        public void The_Production_Witness_Is_Not_Linked_And_System_Management_Stay_In_The_Plugin()
        {
            var pluginProject = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "WebContent.csproj"));
            var witness = Collapse(ReadPluginSource(Path.Combine("Client", "BrowserProcessLockWitness.cs")));
            var sweep = Collapse(ReadPluginSource(Path.Combine("Client", "ProfileSweep.cs")));

            // The linked ProfileSweep seam compiles without System.Management; only the
            // plugin-only witness pulls it (csproj reference above).
            Assert.Contains("<Reference Include=\"System.Management\" />", pluginProject);
            Assert.Contains("using System.Management;", witness);
            Assert.DoesNotContain("System.Management", sweep);

            // The witness reads the runtime's own command lines once per sweep - the same
            // Win32_Process witness the SC-8/SC-11 live rows trust.
            Assert.Contains("SELECT CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'", witness);
            Assert.Contains("if (_commandLines == null)", witness);
        }
    }
}
