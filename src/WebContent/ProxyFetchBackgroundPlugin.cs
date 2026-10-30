using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VideoOS.Platform;
using VideoOS.Platform.Background;
using VideoOS.Platform.Messaging;

namespace WebContent
{
    /// <summary>
    /// Ticket #53: the server-side half of the proxy-fetch contract. The same WebContent.dll
    /// loads into the XProtect Event Server (plugin.def load env "Service"), where this
    /// BackgroundPlugin answers WebContent.Proxy.FetchRequest messages: it performs the HTTP(S)
    /// GET from the server network under the ProxyHttpFetcher budgets (connect timeout, read
    /// timeout, maximum response size) and streams the outcome back to the requester as framed,
    /// plugin-namespaced response messages (metadata frame with status line and content headers,
    /// then base64 body chunks).
    ///
    /// Failure classes (unreachable target, size cap exceeded, timeout) are logged to the Event
    /// Server MIP log with actionable context; successful fetches are silent because proxied page
    /// views make that stream high-frequency.
    /// </summary>
    internal sealed class ProxyFetchBackgroundPlugin : BackgroundPlugin
    {
        private static readonly Guid PluginId = new Guid("ced898c8-25f6-406e-b4fa-aecae669fbb0");

        private const int MaxConcurrentFetches = 3;
        private static readonly TimeSpan FetchSlotWait = TimeSpan.FromSeconds(10);

        private ServerId _serverId;
        private MessageCommunication _messageCommunication;
        private object _requestFilterRegistration;
        private SemaphoreSlim _fetchSlots;

        public override Guid Id => PluginId;

        public override string Name => "Web Content proxy fetcher";

        public override List<EnvironmentType> TargetEnvironments { get; } = new List<EnvironmentType> { EnvironmentType.Service };

        public override void Init()
        {
            _fetchSlots = new SemaphoreSlim(MaxConcurrentFetches, MaxConcurrentFetches);

            try
            {
                _serverId = EnvironmentManager.Instance.MasterSite.ServerId;

                // Per the MIP messaging docs the Event Server plug-in may initialize the message
                // communication client directly in Init().
                MessageCommunicationManager.Start(_serverId);
                _messageCommunication = MessageCommunicationManager.Get(_serverId);
                _requestFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyFetchRequest,
                    new CommunicationIdFilter(ProxyMessageIds.FetchRequestId));

                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy fetcher ready (concurrency={MaxConcurrentFetches}, request id={ProxyMessageIds.FetchRequestId}).");
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy fetcher failed to start: {exception.Message}");
            }
        }

        public override void Close()
        {
            if (_messageCommunication != null && _requestFilterRegistration != null)
            {
                try
                {
                    _messageCommunication.UnRegisterCommunicationFilter(_requestFilterRegistration);
                }
                catch (Exception exception)
                {
                    EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy fetch filter detach failed: {exception.Message}");
                }
            }

            _requestFilterRegistration = null;
            _messageCommunication = null;

            // Deliberately no MessageCommunicationManager.Stop: the messaging session is
            // process-wide and may be shared by other plugins (no MIP sample ever stops it).
            _serverId = null;

            var slots = _fetchSlots;
            _fetchSlots = null;
            slots?.Dispose();

            EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), "Proxy fetcher stopped.");
        }

        /// <summary>
        /// Message-receiver thread: keep it non-blocking. The fetch runs on the thread pool and
        /// the response stream is pushed back to the requester's endpoint FQID.
        /// </summary>
        private object OnProxyFetchRequest(Message message, FQID destination, FQID source)
        {
            var payload = message?.Data as ProxyFetchRequestPayload;
            if (payload == null || string.IsNullOrEmpty(payload.CorrelationId) || string.IsNullOrEmpty(payload.Url))
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), "Proxy fetch request carried no usable payload; refused.");
                return null;
            }

            Uri target;
            if (!Uri.TryCreate(payload.Url, UriKind.Absolute, out target) || !ProxyPolicy.IsHttpUri(target))
            {
                LogRefused(payload, "the url was not an absolute http(s) uri");
                TransmitFailure(payload.CorrelationId, source, ProxyFetchErrorCodes.BadRequest, "The proxy-fetch url must be an absolute http(s) uri.");
                return null;
            }

            var communication = _messageCommunication;
            if (communication == null)
            {
                LogRefused(payload, "the fetcher is not connected to the Event Server");
                TransmitFailure(payload.CorrelationId, source, ProxyFetchErrorCodes.Error, "The proxy fetcher is not ready.");
                return null;
            }

            var slots = _fetchSlots;
            if (slots == null)
            {
                return null;
            }

            _ = Task.Run(async () =>
            {
                if (!await slots.WaitAsync(FetchSlotWait))
                {
                    LogRefused(payload, "the fetcher queue was saturated");
                    TransmitFailure(payload.CorrelationId, source, ProxyFetchErrorCodes.Error, "The proxy fetcher is saturated; try again later.");
                    return;
                }

                try
                {
                    await ServeFetchAsync(communication, payload, target, source);
                }
                finally
                {
                    slots.Release();
                }
            });

            return null;
        }

        private async Task ServeFetchAsync(MessageCommunication communication, ProxyFetchRequestPayload payload, Uri target, FQID requesterFqid)
        {
            try
            {
                var limits = ProxyFetchLimits.Default();
                var result = await new ProxyHttpFetcher().FetchAsync(target, limits);

                if (!result.Success)
                {
                    // Ticket acceptance: failure cases produce clear log entries in the Event
                    // Server log. Successes stay quiet: proxied page views make fetches frequent.
                    EnvironmentManager.Instance.Log(
                        true,
                        nameof(ProxyFetchBackgroundPlugin),
                        $"Proxy fetch failed (code={result.ErrorCode}, target={ProxyPolicy.CanonicalOrigin(target)}): {result.ErrorMessage}");
                }

                foreach (var frame in ProxyFetchFramer.Frame(payload.CorrelationId, result, limits.ChunkSizeBytes))
                {
                    communication.TransmitMessage(new Message(ProxyMessageIds.FetchResponseId, frame), requesterFqid, null, null);
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy fetch aborted (target={(target == null ? "unknown" : ProxyPolicy.CanonicalOrigin(target))}): {exception.Message}");

                try
                {
                    TransmitFailure(payload.CorrelationId, requesterFqid, ProxyFetchErrorCodes.Error, exception.Message);
                }
                catch (Exception transmitException)
                {
                    EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy failure frame not sent: {transmitException.Message}");
                }
            }
        }

        private void TransmitFailure(string correlationId, FQID requester, string errorCode, string message)
        {
            var communication = _messageCommunication;
            if (communication == null || requester == null)
            {
                return;
            }

            communication.TransmitMessage(
                new Message(
                    ProxyMessageIds.FetchResponseId,
                    new ProxyFetchResponsePayload
                    {
                        CorrelationId = correlationId,
                        Kind = ProxyFetchFrameKinds.Failure,
                        Sequence = -1,
                        ErrorCode = errorCode,
                        ErrorMessage = message,
                    }),
                requester,
                null,
                null);
        }

        private static void LogRefused(ProxyFetchRequestPayload payload, string reason)
        {
            EnvironmentManager.Instance.Log(
                true,
                nameof(ProxyFetchBackgroundPlugin),
                $"Proxy fetch request {(payload == null ? "" : payload.CorrelationId)} refused: {reason}.");
        }
    }
}
