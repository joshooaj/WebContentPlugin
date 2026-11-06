using System;

namespace WebContent
{
    /// <summary>
    /// Ticket #72: the one live "does this machine trust the deployment CA behind my proxied
    /// https tunnels" posture per browser view. <see cref="ProxyLoopbackTls"/> publishes it on
    /// every leaf mint from the <see cref="ProxyLeafResponsePayload"/> the Event Server already
    /// sends (thumbprint + CA DER ride the exchange since #56); the Smart Client view chrome
    /// renders it as the untrusted-CA banner, and setup mode offers the elevated fix through the
    /// same record (the DER the banner fix imports is exactly the DER the mint reported - no
    /// second export path, no user-picked file).
    ///
    /// Deliberately a dumb state holder: it decides nothing about trust (the root-store read is
    /// <see cref="Client.ProxyLoopbackTrust"/>'s), never writes a store, and fires
    /// <see cref="Changed"/> only when the posture actually changes so per-page-load traffic
    /// never re-triggers banner work. Subscribers marshal to their own UI threads; a throwing
    /// subscriber is swallowed here and never reaches the tunnel flow.
    /// </summary>
    internal sealed class ProxyCaTrustState
    {
        private readonly object _sync = new object();
        private string _authorityThumbprint;
        private string _authorityDerBase64;
        private bool _isTrusted;
        private bool _hasAuthority;

        /// <summary>
        /// #80: the "host:port" of the most recent mint that published this posture. The
        /// untrusted-posture block report names it, so the Event Server's ring shows the https
        /// origin the user actually tried, not a synthetic marker. It is NOT part of the change
        /// detection: a same-posture mint for another host refreshes this field quietly.
        /// </summary>
        private string _lastMintOrigin;

        /// <summary>
        /// Raised after the posture changes - from whatever socket thread ran the mint (the
        /// listener's) or from the caller of <see cref="Clear"/>/<see cref="Publish"/>. Subscriber
        /// faults never propagate back into the proxy flow.
        /// </summary>
        internal event Action Changed;

        /// <summary>True once at least one mint reported this deployment CA (an unproxied or
        /// never-https browser view never has, so its chrome shows no CA banner at all).</summary>
        internal bool HasAuthority
        {
            get { lock (_sync) return _hasAuthority; }
        }

        internal string AuthorityThumbprint
        {
            get { lock (_sync) return _authorityThumbprint; }
        }

        /// <summary>Public CA DER (base64) exactly as the mint response carried it - what the
        /// setup-mode fix helper imports; the chain anchor the leaves actually present.</summary>
        internal string AuthorityDerBase64
        {
            get { lock (_sync) return _authorityDerBase64; }
        }

        /// <summary>Root-store verdict for that thumbprint as of the last publish.</summary>
        internal bool IsTrusted
        {
            get { lock (_sync) return _isTrusted; }
        }

        /// <summary>The exact banner condition: we know the CA, and this machine does not trust it.</summary>
        internal bool IsUntrustedConfirmed
        {
            get { lock (_sync) return _hasAuthority && !_isTrusted; }
        }

        /// <summary>
        /// #80: the "host:port" of the most recent mint that published this posture, or null until
        /// the first mint names one. The untrusted-posture block report files itself under this
        /// origin so the Event Server's ring shows the https origin a user actually reached for.
        /// </summary>
        internal string LastMintOrigin
        {
            get { lock (_sync) return _lastMintOrigin; }
        }

        /// <summary>
        /// Record one mint's verdict. Returns true when anything changed (first report, a trust
        /// flip, or a different CA thumbprint); a same-value report fires nothing.
        /// #80: <paramref name="mintOrigin"/> ("host:port" of the mint that produced this verdict)
        /// refreshes <see cref="LastMintOrigin"/> quietly - it is never part of change detection,
        /// so per-page-load mint traffic stays silent.
        /// </summary>
        internal bool Publish(string authorityThumbprint, string authorityDerBase64, bool isTrusted, string mintOrigin = null)
        {
            if (string.IsNullOrEmpty(authorityThumbprint))
            {
                return false;
            }

            bool changed;
            lock (_sync)
            {
                changed = !_hasAuthority
                    || !string.Equals(_authorityThumbprint, authorityThumbprint, StringComparison.OrdinalIgnoreCase)
                    || _isTrusted != isTrusted;
                _hasAuthority = true;
                _authorityThumbprint = authorityThumbprint;
                if (!string.IsNullOrEmpty(authorityDerBase64))
                {
                    _authorityDerBase64 = authorityDerBase64;
                }
                if (!string.IsNullOrEmpty(mintOrigin))
                {
                    _lastMintOrigin = mintOrigin;
                }
                _isTrusted = isTrusted;
            }

            if (changed)
            {
                RaiseChanged();
            }

            return changed;
        }

        /// <summary>
        /// Forget the posture entirely (the proxy was switched off for this browser since the
        /// posture was recorded): the banner condition can never hold for a view that no longer
        /// routes through the loopback listener.
        /// </summary>
        internal void Clear()
        {
            bool changed;
            lock (_sync)
            {
                changed = _hasAuthority;
                _hasAuthority = false;
                _authorityThumbprint = null;
                _authorityDerBase64 = null;
                _isTrusted = false;
            }

            if (changed)
            {
                RaiseChanged();
            }
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch
            {
                // A failing banner callback must never reach the tunnel that published the posture.
            }
        }
    }
}
