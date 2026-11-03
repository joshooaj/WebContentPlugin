using System.IO;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #72 live-shape guards: the banner is rendered by WPF views the unit harness can
    /// never host, so these source pins hold the composition the live probes drive - the named
    /// banner elements, the setup-only visibility gate for the fix button, the elevated helper
    /// invocation that must never suppress either OS dialog, the post-fix re-verification that
    /// never trusts the helper's own claim, and the leaf-cache invalidation that makes the fix
    /// take effect without a Smart Client restart. The behavioural decisions sit in
    /// ProxyCaTrustStateTests and ProxyTrustHelperTests; the live rows are in
    /// docs/verification-matrix.md.
    /// </summary>
    public class ProxyCaTrustBannerWiringGuardTests
    {
        private static string Read(string file)
        {
            return File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "Client", file));
        }

        private static string Collapse(string source)
        {
            return System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ");
        }

        [Fact]
        public void TheBannerElementsExistAndStartCollapsed()
        {
            var xaml = Read("WebContentUserControl.xaml");

            // The names are the AutomationIds the UIA probe finds; all three start collapsed so a
            // view that never minted through the proxy shows no CA chrome at all.
            Assert.Contains("Name=\"proxyCaBanner\" DockPanel.Dock=\"Top\" Visibility=\"Collapsed\"", xaml);
            Assert.Contains("Name=\"proxyCaBannerText\"", xaml);
            Assert.Contains("Name=\"proxyCaFixButton\" Grid.Column=\"1\" Visibility=\"Collapsed\"", xaml);
            Assert.Contains("Name=\"proxyCaDismissButton\"", xaml);
        }

        [Fact]
        public void TheFixButtonIsSetupStateOnlyWhileTheBannerFollowsThePosture()
        {
            var collapsed = Collapse(Read("WebContentUserControl.xaml.cs"));

            // Standard users see the banner but never the fix affordance (the ticket's explicit
            // posture); the fix click additionally re-gates on setup state, belt and braces.
            Assert.Contains(
                "proxyCaFixButton.Visibility = _viewModel?.IsSetupState == true ? Visibility.Visible : Visibility.Collapsed;",
                collapsed);
            Assert.Contains(
                "if (trust == null || !trust.IsUntrustedConfirmed || _viewModel?.IsSetupState != true)",
                collapsed);
        }

        [Fact]
        public void TheFixGoesThroughTheBundledHelperViaUacNeverAnInProcessStoreWrite()
        {
            var collapsed = Collapse(Read("WebContentUserControl.xaml.cs"));

            Assert.Contains("private const string TrustHelperExecutableName = \"WebContentTrustHelper.exe\";", collapsed);
            Assert.Contains("Verb = \"runas\",", collapsed);

            // The plugin itself must not reach the machine root store from the fix flow: the only
            // LocalMachine involvement in the view code is the READ-only verification probe.
            Assert.DoesNotContain("StoreLocation.LocalMachine", Read("WebContentUserControl.xaml.cs"));
        }

        [Fact]
        public void DecliningUacIsHandledQuietlyAndTheResultIsReVerifiedNotTrusted()
        {
            var collapsed = Collapse(Read("WebContentUserControl.xaml.cs"));

            // 1223 = the user cancelled the UAC prompt: a logged note, never an exception surface.
            Assert.Contains("private const int ErrorCancelled = 1223;", collapsed);
            Assert.Contains(
                "catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)",
                collapsed);

            // The helper's exit 0 alone never flips the banner: the plugin re-reads the root stores.
            Assert.Contains("var nowTrusted = exitCode == 0 && ProxyLoopbackTrust.IsRootTrusted(", collapsed);
        }

        [Fact]
        public void AConfirmedFixDropsCachedLeavesAndReloadsHttpsContentWithoutARestart()
        {
            var collapsed = Collapse(Read("WebContentUserControl.xaml.cs"));

            Assert.Contains("browser?.ProxyListener?.InvalidateLeafCache();", collapsed);
            Assert.Contains("_viewModel?.ReloadCommand?.Execute(null);", collapsed);
        }

        [Fact]
        public void NavigationReChecksTheTrustSoAnExternalFixHealsTheBannerToo()
        {
            var collapsed = Collapse(Read("BrowserInstance.cs"));

            // The fix helper is not the only way the CA becomes trusted (an admin may import it
            // manually); the next navigation re-probes and heals the posture + leaf cache itself.
            Assert.Contains("RecheckProxyCaTrust();", collapsed);
            Assert.Contains("ProxyListener.InvalidateLeafCache();", collapsed);
        }

        [Fact]
        public void ProxiedBrowsersGetATrustStateAndProxyOffClearsIt()
        {
            var collapsed = Collapse(Read("BrowserManager.cs"));

            Assert.Contains("var caTrust = proxyProfile == null ? null : new ProxyCaTrustState();", collapsed);
            Assert.Contains("browser.ProxyCaTrust?.Clear();", collapsed);
        }

        [Fact]
        public void TheHelperShipsInTheDropButThePluginNeverLinksIt()
        {
            var csproj = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "WebContent.csproj"));
            var collapsed = Collapse(csproj);

            // The build order rides the ProjectReference; the plugin must not compile against the
            // helper (its store-write code belongs in an elevated separate process only).
            Assert.Contains(
                "<ProjectReference Include=\"..\\WebContentTrustHelper\\WebContentTrustHelper.csproj\"> <ReferenceOutputAssembly>false</ReferenceOutputAssembly>",
                collapsed);
        }
    }
}
