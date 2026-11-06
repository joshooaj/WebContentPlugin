using System.IO;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #83 live-shape guards for the setup-mode blocked-resources indicator. The first
    /// live SC-7 run (2026-11-03) exposed the defect these pins forbid: the "N resources
    /// blocked" TextBlock sat as the CONTENT of a VideoOSButtonTransparentSmall, an icon-only
    /// control whose template never presents text content - the label rendered nothing, had no
    /// UIA peer, and the toolbar affordance was an anonymous icon. The label must therefore be
    /// a toolbar SIBLING of the button with an explicit AutomationId, and both elements must
    /// toggle visibility in lockstep in UpdateBlockedIndicator (the live probe asserts on both
    /// ids; a label that outlives the button, or a button that outlives the label, re-opens
    /// the ambiguity the fix removed).
    /// </summary>
    public class ProxyBlockIndicatorWiringGuardTests
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
        public void TheLabelIsAToolbarSiblingNotContentInsideTheIconButton()
        {
            var xaml = Read("WebContentUserControl.xaml");

            // The button keeps an icon (an icon-only button with no IconSource renders as an
            // anonymous box) and the label sits beside it with the AutomationId the probe reads.
            var button = System.Text.RegularExpressions.Regex.Match(
                xaml,
                "<platformUiControls:VideoOSButtonTransparentSmall Name=\"blockedIndicator\".*?</platformUiControls:VideoOSButtonTransparentSmall>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(button.Success, "blockedIndicator button not found in the toolbar XAML");
            Assert.Contains("Icon=\"Warning_Combined\"", button.Value);
            Assert.DoesNotContain("blockedIndicatorText", button.Value, System.StringComparison.Ordinal);

            var collapsed = Collapse(xaml);
            Assert.Contains(
                "Name=\"blockedIndicatorText\" DockPanel.Dock=\"Right\" Visibility=\"Collapsed\"",
                collapsed);
            Assert.Contains("AutomationProperties.AutomationId=\"blockedIndicatorText\"", collapsed);
        }

        [Fact]
        public void BothChromeElementsStartCollapsedAndToggleInLockstep()
        {
            var collapsed = Collapse(Read("WebContentUserControl.xaml.cs"));

            // Show: text, then label, then button - all three assignments or none.
            Assert.Contains(
                "blockedIndicatorText.Text = total == 1",
                collapsed);
            Assert.Contains(
                "blockedIndicatorText.Visibility = Visibility.Visible; blockedIndicator.Visibility = Visibility.Visible;",
                collapsed);

            // Hide: button and label collapse together (a label left behind would keep the
            // setup-mode chrome claiming blocks that no longer exist).
            Assert.Contains(
                "blockedIndicator.Visibility = Visibility.Collapsed; blockedIndicatorText.Visibility = Visibility.Collapsed; blockedResourcesPanel.Visibility = Visibility.Collapsed;",
                collapsed);
        }

        [Fact]
        public void TheIndicatorRemainsSetupModeOnlyBehindTheTallyGate()
        {
            var collapsed = Collapse(Read("WebContentUserControl.xaml.cs"));

            // The gate itself (setup mode AND a non-zero tally) is the whole visibility
            // decision; ordinary users in runtime mode never see the label or the button.
            Assert.Contains(
                "if (_blockTally != null && _viewModel?.IsSetupState == true && total > 0)",
                collapsed);
        }
    }
}
