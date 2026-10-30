using System;
using System.Threading.Tasks;
using VideoOS.Platform;
using VideoOS.Platform.Messaging;

namespace WebContent
{
    /// <summary>
    /// Requester side of the proxy-fetch contract (#53): sends WebContent.Proxy.FetchRequest over
    /// classic MIP messaging and hands back the reassembled outcome from the framed response
    /// stream. This is the client the ticket's "Management Client-driven harness" drives, and the
    /// seam the Smart Client view consumes in #54.
    ///
    /// The response receiver feeds the correlator; correlation identity (Guid) is local, so no
    /// other host's in-flight exchanges leak into this requester's results.
    /// </summary>
    internal sealed class ProxyFetchClient
    {
        /// <summary>
        /// Wired up during PluginDefinition.Init in the Administration environment; null until an
        /// environment provisions it.
        /// </summary>
        internal static ProxyFetchClient Active;

        private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

        private ServerId _serverId;
        private MessageCommunication _communication;
        private object _responseFilterRegistration;
        private ProxyFetchCorrelation _correlation;

        internal bool IsReady => _communication != null;

        /// <summary>
        /// Start MIP message communication for the master site and subscribe to proxy-fetch
        /// responses. Failures are logged, not thrown: an unready harness must not fail the host.
        /// </summary>
        internal void Setup(ServerId masterSite)
        {
            if (masterSite == null)
            {
                EnvironmentManager.Instance.Log(false, nameof(ProxyFetchClient), "Proxy fetch harness skipped: no master site is connected.");
                return;
            }

            try
            {
                MessageCommunicationManager.Start(masterSite);
                _serverId = masterSite;
                _communication = MessageCommunicationManager.Get(masterSite);
                _correlation = new ProxyFetchCorrelation();
                _responseFilterRegistration = _communication.RegisterCommunicationFilter(
                    OnProxyFetchResponse,
                    new CommunicationIdFilter(ProxyMessageIds.FetchResponseId));

                EnvironmentManager.Instance.Log(false, nameof(ProxyFetchClient), "Proxy fetch harness ready.");
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy fetch harness failed to start: {exception.Message}");
                _communication = null;
                _correlation = null;
            }
        }

        internal void Close()
        {
            var communication = _communication;
            if (communication != null && _responseFilterRegistration != null)
            {
                try
                {
                    communication.UnRegisterCommunicationFilter(_responseFilterRegistration);
                }
                catch (Exception exception)
                {
                    EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy fetch response filter detach failed: {exception.Message}");
                }
            }

            _responseFilterRegistration = null;
            _communication = null;
            _correlation = null;

            // Deliberately no MessageCommunicationManager.Stop: the messaging session is
            // process-wide and may be shared by other plugins (no MIP sample ever stops it).
            _serverId = null;
        }

        /// <summary>
        /// Send one proxy-fetch request and return the task completed with the reassembled
        /// outcome. The deadline bounds the whole exchange, not each frame.
        /// </summary>
        internal Task<ProxyFetchResult> FetchAsync(string url, TimeSpan? deadline = null)
        {
            var communication = _communication;
            if (communication == null)
            {
                return Task.FromResult(Failure(ProxyFetchErrorCodes.Error, "The proxy fetch harness is not connected to the Event Server."));
            }

            Uri target;
            if (!Uri.TryCreate(url, UriKind.Absolute, out target) || !ProxyPolicy.IsHttpUri(target))
            {
                return Task.FromResult(Failure(ProxyFetchErrorCodes.BadRequest, "The proxy-fetch url must be an absolute http(s) uri."));
            }

            var correlationId = Guid.NewGuid().ToString("N");
            var completion = _correlation.Begin(correlationId, deadline ?? DefaultDeadline);

            try
            {
                communication.TransmitMessage(
                    new Message(
                        ProxyMessageIds.FetchRequestId,
                        new ProxyFetchRequestPayload
                        {
                            CorrelationId = correlationId,
                            Url = target.AbsoluteUri,
                        }),
                    null,
                    null,
                    null);
            }
            catch (Exception exception)
            {
                // MIP messaging is down: the correlator already owns the task, so let its
                // deadline/abandon path complete it, and label the outcome locally.
                _correlation.Abandon(correlationId);
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy fetch request not sent: {exception.Message}");
            }

            return completion;
        }

        private object OnProxyFetchResponse(Message message, FQID destination, FQID source)
        {
            var frame = message?.Data as ProxyFetchResponsePayload;
            if (frame != null)
            {
                _correlation?.Feed(frame);
            }

            return null;
        }

        private static ProxyFetchResult Failure(string errorCode, string message)
        {
            return new ProxyFetchResult
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = message,
            };
        }
    }
}
