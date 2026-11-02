using System;

using WebContent.Client;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Pins the toolbar admission rule that keeps a failed CoreWebView2 initialization from
    /// crashing the Smart Client: every command is a call into the live CoreWebView2, and the
    /// WebView2 wrapper throws InvalidOperationException from VerifyCoreWebView2 (Reload, GoBack,
    /// GoForward, ShowPrintUI) whenever CoreWebView2 is null. The live failure (2026-11-02): a
    /// second Smart Client process failed init against the shared proxied user-data folder, the
    /// old gate only checked the WPF-level IsLoaded - which stays true through an init failure -
    /// and a Refresh click died in Reload with an exception dialog.
    /// </summary>
    public class BrowserCommandAdmissionTests
    {
        // The crash state itself: control alive, IsLoaded-style checks satisfied, navigation
        // history flags stale - but no CoreWebView2. Nothing may be admitted. BrowserAction is
        // internal, so the theory rows carry it as a string (public test methods cannot take it).
        [Theory]
        [InlineData("Refresh")]
        [InlineData("NavigateBack")]
        [InlineData("NavigateForward")]
        [InlineData("NavigateHome")]
        [InlineData("Print")]
        public void UninitializedView_admitsNothing(string actionName)
        {
            var action = (BrowserAction)Enum.Parse(typeof(BrowserAction), actionName);
            // canGoBack/canGoForward deliberately true: the rule must not trust stale flags.
            Assert.False(BrowserCommandAdmission.IsAvailable(action, coreReady: false, canGoBack: true, canGoForward: true));
        }

        [Theory]
        [InlineData("Refresh")]
        [InlineData("NavigateHome")]
        [InlineData("Print")]
        public void InitializedView_admitsStatelessCommands(string actionName)
        {
            var action = (BrowserAction)Enum.Parse(typeof(BrowserAction), actionName);
            Assert.True(BrowserCommandAdmission.IsAvailable(action, coreReady: true, canGoBack: false, canGoForward: false));
        }

        [Fact]
        public void BackAndForward_needTheirHistoryFlag()
        {
            Assert.True(BrowserCommandAdmission.IsAvailable(BrowserAction.NavigateBack, coreReady: true, canGoBack: true, canGoForward: false));
            Assert.True(BrowserCommandAdmission.IsAvailable(BrowserAction.NavigateForward, coreReady: true, canGoBack: false, canGoForward: true));
            Assert.False(BrowserCommandAdmission.IsAvailable(BrowserAction.NavigateBack, coreReady: true, canGoBack: false, canGoForward: true));
            Assert.False(BrowserCommandAdmission.IsAvailable(BrowserAction.NavigateForward, coreReady: true, canGoBack: true, canGoForward: false));
        }

        [Fact]
        public void EveryAction_isKnownToTheGate()
        {
            // A new BrowserAction value must be decided about explicitly: an unknown member
            // admits nothing even on a healthy view, so the matrix test above cannot silently
            // pass a future command through.
            foreach (BrowserAction action in Enum.GetValues(typeof(BrowserAction)))
            {
                var admitted = BrowserCommandAdmission.IsAvailable(action, coreReady: true, canGoBack: true, canGoForward: true);
                if (action == BrowserAction.NavigateTo)
                {
                    // NavigateTo has no toolbar command and an empty Execute - never admitted.
                    Assert.False(admitted);
                }
                else
                {
                    Assert.True(admitted);
                }
            }
        }
    }
}
