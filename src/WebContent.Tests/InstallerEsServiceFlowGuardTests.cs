using System;
using System.IO;
using System.Text.RegularExpressions;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// #82: source pins for the MSI Event Server service flow. The compiled-MSI shape
    /// (sequences, CA types, ControlEvent/Control rows) is gated by build.ps1's structural
    /// guard at installer-build time; these rows pin the AUTHORING that guard cannot see:
    /// the three-branch condition text, the single detection property shared by UI and
    /// custom actions, the fork wiring that keeps WixUI_Minimal semantics on machines
    /// without an Event Server, the SC-aware dialog copy, and the CA project's place in
    /// the build (built before the wixproj, never harvested into the drop).
    /// </summary>
    public class InstallerEsServiceFlowGuardTests
    {
        private static string SetupWxs()
        {
            return File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "Setup", "setup.wxs"));
        }

        private static string SetupUiWxs()
        {
            return File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "Setup", "setupui.wxs"));
        }

        private static string Wxl()
        {
            return File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "Setup", "setup.en-us.wxl"));
        }

        private static string Collapsed(string text)
        {
            return Regex.Replace(text, @"\s+", " ");
        }

        // ---------------------------------------------------------------- detection + branches

        [Fact]
        public void EsDetectionIsTheServiceControlManagerKey()
        {
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<RegistrySearch Id=\"EventServerServiceImagePath\" Root=\"HKLM\" Key=\"SYSTEM\\CurrentControlSet\\Services\\MilestoneEventServerService\"",
                wxs);
        }

        [Fact]
        public void PluginFilesDetectionCoversTheCopyItemDeployRoute()
        {
            // The stop branch must also fire when the drop was placed by the dev Copy-Item
            // flow (a FileSearch on the fixed drop path), not only for MSI-registered upgrades.
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<DirectorySearch Id=\"PluginDropDir\" Path=\"C:\\Program Files\\VideoOS\\MIPPlugins\\CascadiaWebContentPlugin\"> <FileSearch Id=\"PluginDropWebContentDll\" Name=\"WebContent.dll\" />",
                wxs);
        }

        [Fact]
        public void StopConditionCarriesAllThreeBranchGuards()
        {
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<Custom Action=\"StopEventServerService\" After=\"Wix4CloseApplications_$(sys.BUILDARCHSHORT)\" Condition=\"NOT REMOVE AND EVENTSERVERSERVICEPRESENT AND (WIX_UPGRADE_DETECTED OR WEBCONTENTPLUGINPRESENT)\" />",
                wxs);
        }

        [Fact]
        public void RestartConditionCarriesTheChoiceAndThePresenceGate()
        {
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<Custom Action=\"RestartEventServerService\" After=\"InstallFiles\" Condition=\"NOT REMOVE AND EVENTSERVERSERVICEPRESENT AND RESTART_EVENT_SERVER=&quot;1&quot;\" />",
                wxs);
        }

        [Fact]
        public void RestartCheckboxIsCheckedByDefaultAndPublic()
        {
            // Public name: the UI checkbox value must flow into the execute sequence, and
            // scripted installs get RESTART_EVENT_SERVER=0 as the documented decline.
            var wxs = Collapsed(SetupWxs());
            Assert.Contains("<Property Id=\"RESTART_EVENT_SERVER\" Value=\"1\" />", wxs);
        }

        [Fact]
        public void BothServiceActionsRunFromTheSameStreamedDeferredExe()
        {
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<CustomAction Id=\"StopEventServerService\" BinaryRef=\"WebContentServiceCtl.exe\" ExeCommand=\"stop MilestoneEventServerService\" Execute=\"deferred\" Return=\"check\" />",
                wxs);
            Assert.Contains(
                "<CustomAction Id=\"RestartEventServerService\" BinaryRef=\"WebContentServiceCtl.exe\" ExeCommand=\"restart MilestoneEventServerService\" Execute=\"deferred\" Return=\"check\" />",
                wxs);
        }

        [Fact]
        public void DeclinedRestartIsStatedOnTheFinishPage()
        {
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<SetProperty Id=\"WIXUI_EXITDIALOGOPTIONALTEXT\"",
                wxs);
            Assert.Contains("NOT RESTART_EVENT_SERVER", wxs);
            Assert.Contains("!(loc.WebContentEsExitText)", wxs);
        }

        // ---------------------------------------------------------------- UI fork

        [Fact]
        public void WelcomePageBranchesToTheChoicePageOnlyWithEs()
        {
            var wxs = Collapsed(SetupUiWxs());
            Assert.Contains(
                "<Publish Event=\"NewDialog\" Value=\"EventServerOptionsDlg\" Condition=\"EVENTSERVERSERVICEPRESENT AND OutOfDiskSpace &lt;&gt; 1\" />",
                wxs);
            Assert.Contains(
                "<Publish Event=\"EndDialog\" Value=\"Return\" Condition=\"NOT EVENTSERVERSERVICEPRESENT AND OutOfDiskSpace &lt;&gt; 1\" />",
                wxs);
        }

        [Fact]
        public void TheChoicePageCarriesTheCheckboxAndTheMessageCommunicationWarning()
        {
            var wxs = Collapsed(SetupUiWxs());
            Assert.Contains("Property=\"RESTART_EVENT_SERVER\"", wxs);

            // The ticket demands the dialog spell out the dispose consequence.
            var wxl = Collapsed(Wxl());
            Assert.Contains("WebContentEsOptionsWarning", wxl);
            Assert.Contains("disconnects the plugin connections held by every running XProtect client", wxl);
            Assert.Contains("Restart Smart Client and Management Client afterwards", wxl);
        }

        [Fact]
        public void TheForkStillEndsTheDialogLoopLikeWixUiMinimal()
        {
            var wxs = Collapsed(SetupUiWxs());
            Assert.Contains(
                "<Control Id=\"Install\" Type=\"PushButton\" X=\"212\" Y=\"243\" Width=\"80\" Height=\"17\" Default=\"yes\" Text=\"!(loc.WelcomeEulaDlgInstall)\"> <Publish Event=\"EndDialog\" Value=\"Return\" Condition=\"OutOfDiskSpace &lt;&gt; 1\" />",
                wxs);
        }

        [Fact]
        public void MaintenanceFlowsArePreservedFromWixUiMinimal()
        {
            var wxs = Collapsed(SetupUiWxs());
            Assert.Contains("<Property Id=\"ARPNOMODIFY\" Value=\"1\" />", wxs);
            Assert.Contains("<DialogRef Id=\"WelcomeDlg\" />", wxs);
            Assert.Contains("Condition=\"Installed AND PATCH\"", wxs);
        }

        // ---------------------------------------------------------------- build plumbing

        [Fact]
        public void ServiceCtlExeIsBuiltForTheInstallerButNeverHarvestedIntoTheDrop()
        {
            var root = RepoLocator.FindRepoRoot();
            var wixproj = Collapsed(File.ReadAllText(Path.Combine(root, "src", "Setup", "setup.wixproj")));
            Assert.Contains(
                "<ProjectReference Include=\"../WebContentServiceCtl/WebContentServiceCtl.csproj\"> <ReferenceOutputAssembly>false</ReferenceOutputAssembly>",
                wixproj);

            var csproj = File.ReadAllText(Path.Combine(root, "src", "WebContentServiceCtl", "WebContentServiceCtl.csproj"));
            // The helper exe must NOT share the harvested drop output path (unlike the #72
            // trust helper); it rides the MSI as a Binary stream only.
            Assert.DoesNotContain("output\\WebContent", csproj);
        }

        [Fact]
        public void TheCoreDecisionTableIsTestedThroughTheLinkedSource()
        {
            var tests = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent.Tests", "WebContent.Tests.csproj"));
            Assert.Contains(
                "<Compile Include=\"..\\WebContentServiceCtl\\ServiceCtlCore.cs\" Link=\"ServiceCtlCore.cs\" />",
                tests);
        }

        [Fact]
        public void TheBuildScriptGatesTheSequencing()
        {
            var build = File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "build.ps1"));
            Assert.Contains("StopEventServerService", build);
            Assert.Contains("RestartEventServerService", build);
            Assert.Contains("WebContentServiceCtl.exe", build);
        }
    }
}
