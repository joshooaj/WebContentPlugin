using System;

namespace WebContent.Client
{
    internal enum AddressEntryOutcome
    {
        Accepted,
        Rejected,
        IgnoredRepeat,
    }

    /// <summary>
    /// Pure interaction state for the address bar, kept out of the WPF code-behind so the
    /// Enter semantics stay unit-testable: single normalization seam (#14), operator-visible
    /// warning state (#26), and no spam from Enter auto-repeat.
    /// </summary>
    internal sealed class AddressBarInteraction
    {
        private string _warningMessage;

        internal AddressEntryOutcome HandleEnter(string rawAddress, bool isRepeat, out Uri uri)
        {
            uri = null;
            if (isRepeat)
            {
                return AddressEntryOutcome.IgnoredRepeat;
            }

            if (WebContentConfiguration.TryCreateNavigationUri(rawAddress, out uri))
            {
                _warningMessage = null;
                return AddressEntryOutcome.Accepted;
            }

            _warningMessage = string.Format(Strings.InvalidAddressMessage, rawAddress);
            return AddressEntryOutcome.Rejected;
        }

        internal bool IsWarningActive => _warningMessage != null;

        internal string WarningMessage => _warningMessage;

        internal void ClearWarning()
        {
            _warningMessage = null;
        }
    }
}
