using System;
using WebContent.Client;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #26: address-bar interaction seam. The UI decides what to paint; this seam decides
    /// accept/reject/ignore per Enter press, carries the always-visible warning state, and keeps
    /// #14's single normalization seam plus its MIP log markers spam-free under key auto-repeat.
    /// </summary>
    public class AddressBarInteractionTests
    {
        [Fact]
        public void EnterWithValidHostNavigatesToCanonicalUri()
        {
            var bar = new AddressBarInteraction();
            var outcome = bar.HandleEnter("server:8443", false, out var uri);
            Assert.Equal(AddressEntryOutcome.Accepted, outcome);
            Assert.Equal("https://server:8443/", uri.AbsoluteUri);
            Assert.False(bar.IsWarningActive);
        }

        [Fact]
        public void EnterWithGarbageIsRejectedAndRaisesVisibleWarningState()
        {
            var bar = new AddressBarInteraction();
            var outcome = bar.HandleEnter("not a valid url", false, out var uri);
            Assert.Equal(AddressEntryOutcome.Rejected, outcome);
            Assert.Null(uri);
            Assert.True(bar.IsWarningActive);
            Assert.Contains("not a valid url", bar.WarningMessage);
        }

        [Fact]
        public void EnterKeyAutoRepeatNeverNavigatesAndNeverReraisesWarning()
        {
            var bar = new AddressBarInteraction();
            var first = bar.HandleEnter("not a valid url", false, out _);
            Assert.Equal(AddressEntryOutcome.Rejected, first);
            var firstMessage = bar.WarningMessage;

            var repeated = bar.HandleEnter("not a valid url", true, out var repeatUri);
            Assert.Equal(AddressEntryOutcome.IgnoredRepeat, repeated);
            Assert.Null(repeatUri);
            Assert.True(bar.IsWarningActive);
            Assert.Equal(firstMessage, bar.WarningMessage);

            var ignoredValidRepeat = bar.HandleEnter("server:8443", true, out var ignoredUri);
            Assert.Equal(AddressEntryOutcome.IgnoredRepeat, ignoredValidRepeat);
            Assert.Null(ignoredUri);
        }

        [Fact]
        public void OperatorEditingTheAddressRetiresTheWarning()
        {
            var bar = new AddressBarInteraction();
            bar.HandleEnter("not a valid url", false, out _);
            Assert.True(bar.IsWarningActive);

            bar.ClearWarning();
            Assert.False(bar.IsWarningActive);
            Assert.Null(bar.WarningMessage);
        }

        [Fact]
        public void SuccessfulEnterAfterRejectionRetiresTheWarning()
        {
            var bar = new AddressBarInteraction();
            bar.HandleEnter("javascript:alert(1)", false, out _);
            Assert.True(bar.IsWarningActive);

            var outcome = bar.HandleEnter("example.com", false, out var uri);
            Assert.Equal(AddressEntryOutcome.Accepted, outcome);
            Assert.Equal("https://example.com/", uri.AbsoluteUri);
            Assert.False(bar.IsWarningActive);
        }
    }
}
