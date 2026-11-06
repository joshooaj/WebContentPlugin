using System;
using System.IO;
using System.Text.RegularExpressions;

using WebContent.ProxyCa;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #77: source pins for the install-time deployment CA. The compiled-MSI shape
    /// (CustomAction type/binary, sequence slot, condition needles, checkbox Control row)
    /// is gated by build.ps1's structural guard at installer-build time; these rows pin
    /// the AUTHORING that guard cannot see: the condition text and ordering in setup.wxs,
    /// the checkbox on the same options page, the CN staying equal to the plugin's, the
    /// CA exe being built for the installer but never harvested into the drop, the core
    /// being linked into this test project, and the script being retired to legacy duty.
    /// </summary>
    public class InstallerProxyCaGuardTests
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

        // ---------------------------------------------------------------- action + sequence

        [Fact]
        public void CaActionIsADeferredSystemActionFromTheStreamedBinary()
        {
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<Binary Id=\"WebContentProxyCa.exe\" SourceFile=\"$(var.ProxyCaExePath)\" />",
                wxs);
            Assert.Contains(
                "<CustomAction Id=\"CreateProxyAuthority\" BinaryRef=\"WebContentProxyCa.exe\" ExeCommand=\"provision MilestoneEventServerService\" Execute=\"deferred\" Return=\"check\" />",
                wxs);
        }

        [Fact]
        public void CaActionIsGatedOnEsPresentAndTheProxyOptInAndScheduledAfterTheFileCopy()
        {
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<Custom Action=\"CreateProxyAuthority\" After=\"InstallFiles\" Condition=\"NOT REMOVE AND EVENTSERVERSERVICEPRESENT AND PROXY_SUPPORT=&quot;1&quot;\" />",
                wxs);
        }

        [Fact]
        public void ServiceRestartsAfterTheCaIsProvisioned()
        {
            // The restart action moves one slot later so the Event Server comes back on a
            // provisioned CA; its own condition text (the #82 pin) is unchanged.
            var wxs = Collapsed(SetupWxs());
            Assert.Contains(
                "<Custom Action=\"RestartEventServerService\" After=\"CreateProxyAuthority\" Condition=\"NOT REMOVE AND EVENTSERVERSERVICEPRESENT AND RESTART_EVENT_SERVER=&quot;1&quot;\" />",
                wxs);
        }

        [Fact]
        public void ProxyOptInDefaultsToOnAndIsPublicForScriptedDecline()
        {
            var wxs = Collapsed(SetupWxs());
            Assert.Contains("<Property Id=\"PROXY_SUPPORT\" Value=\"1\" />", wxs);
        }

        // ---------------------------------------------------------------- UI

        [Fact]
        public void OptInCheckboxLivesOnTheSameEventServerOptionsPage()
        {
            var wxs = Collapsed(SetupUiWxs());
            Assert.Contains(
                "Type=\"CheckBox\" X=\"130\" Y=\"70\" Width=\"226\" Height=\"18\" CheckBoxValue=\"1\" Property=\"PROXY_SUPPORT\" Text=\"!(loc.WebContentEsOptionsProxy)\"",
                wxs);
        }

        [Fact]
        public void DialogCopyNamesTheReuseSemanticsAndTheClientTrustStep()
        {
            // The epic's posture: the CA is created here, private key never leaves, and the
            // per-machine Root trust is still an admin step spelled out on the page.
            var wxl = Collapsed(Wxl());
            Assert.Contains("creates the proxy certificate; an existing certificate is reused", wxl);
            Assert.Contains("private key never leaves here", wxl);
            Assert.Contains("service account is granted read access", wxl);
            Assert.Contains("Every Smart Client machine must still trust the certificate once", wxl);
        }

        // ---------------------------------------------------------------- drift pins

        [Fact]
        public void TheCoreCnMatchesThePluginsAuthorityCn()
        {
            // The grant step finds the CA by CN; the plugin loads it by the same name. A
            // rename on one side without the other strands the grant (or the load).
            Assert.Equal("XProtect WebContent Plugin Loopback Proxy", ProxyCaCore.AuthorityCommonName);

            var tls = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "ProxyLoopbackTls.cs"));
            Assert.Contains(
                "internal const string AuthorityCommonName = \"XProtect WebContent Plugin Loopback Proxy\";",
                tls);
        }

        [Fact]
        public void TheGatewayDrivesThePluginsOwnLoadOrCreateByReflection()
        {
            // One minting recipe (ADR-0003): the helper never builds certificates itself.
            var program = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContentProxyCa", "Program.cs"));
            Assert.Contains("Assembly.LoadFrom(pluginDllPath)", program);
            Assert.Contains("GetType(\"WebContent.Authority\")", program);
            Assert.Contains("LoadOrCreateDeploymentAuthority", program);
            Assert.DoesNotContain("CertificateRequest", program);
            Assert.DoesNotContain("CreateSelfSigned", program);

            // The key grant locates the key file through the CAPI handle, NOT the legacy
            // md5-of-container-name guess that forced the script's creation-time fallback.
            Assert.Contains("CspKeyContainerInfo.UniqueKeyContainerName", program);
            Assert.DoesNotContain("MD5", program);
        }

        [Fact]
        public void ReusedDaclIsUntouchedAndMissingGrantsAreRepaired()
        {
            // Idempotent grant: already-granted logs "DACL untouched" and writes nothing;
            // the write path exists for legacy/broken states.
            var program = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContentProxyCa", "Program.cs"));
            Assert.Contains("DACL untouched", program);
            Assert.Contains("AddAccessRule", program);
            Assert.Contains("SetAccessControl", program);
        }

        // ---------------------------------------------------------------- build plumbing

        [Fact]
        public void ProxyCaExeIsBuiltForTheInstallerButNeverHarvestedIntoTheDrop()
        {
            var root = RepoLocator.FindRepoRoot();
            var wixproj = Collapsed(File.ReadAllText(Path.Combine(root, "src", "Setup", "setup.wixproj")));
            Assert.Contains(
                "<ProjectReference Include=\"../WebContentProxyCa/WebContentProxyCa.csproj\"> <ReferenceOutputAssembly>false</ReferenceOutputAssembly>",
                wixproj);
            Assert.Contains("<ProxyCaExePath>..\\WebContentProxyCa\\bin\\$(Configuration)\\WebContentProxyCa.exe</ProxyCaExePath>", wixproj);
            Assert.Contains("ProxyCaExePath=$(ProxyCaExePath)", wixproj);

            var csproj = File.ReadAllText(Path.Combine(root, "src", "WebContentProxyCa", "WebContentProxyCa.csproj"));
            // The helper exe must NOT share the harvested drop output path (unlike the #72
            // trust helper); it rides the MSI as a Binary stream only.
            Assert.DoesNotContain("output\\WebContent", csproj);
            // x64: it must load the x64 plugin assembly.
            Assert.Contains("<PlatformTarget>x64</PlatformTarget>", csproj);
            Assert.Contains("<TargetFramework>net472</TargetFramework>", csproj);
        }

        [Fact]
        public void TheCoreDecisionTableIsTestedThroughTheLinkedSource()
        {
            var tests = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent.Tests", "WebContent.Tests.csproj"));
            Assert.Contains(
                "<Compile Include=\"..\\WebContentProxyCa\\ProxyCaCore.cs\" Link=\"ProxyCaCore.cs\" />",
                tests);
        }

        [Fact]
        public void TheBuildScriptGatesTheProxyCaWiring()
        {
            var build = File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "build.ps1"));
            Assert.Contains("CreateProxyAuthority", build);
            Assert.Contains("WebContentProxyCa.exe", build);
            Assert.Contains("PROXY_SUPPORT", build);
        }

        [Fact]
        public void LegacyProvisionScriptIsRetiredButStillShipsForLegacySetups()
        {
            // The ticket: retire it from the supported path, keep it documented for
            // legacy/repair use. The README must point at the installer checkbox, not the
            // script, as step 1.
            var script = File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "scripts", "provision-event-server-authority.ps1"));
            Assert.Contains("LEGACY", script);
            Assert.Contains("#77", script);

            var readme = File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "README.md"));
            Assert.Contains("Enable web content proxy support", readme);
        }
    }
}
